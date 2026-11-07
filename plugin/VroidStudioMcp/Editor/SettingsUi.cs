using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using static VroidStudioMcp.Interop;

namespace VroidStudioMcp;

/// <summary>
/// Native settings-dialog integration: appends a "Programmatic Control (MCP)" section at the top
/// of the settings list. The dialog is a Unity UI tree (Mux) with a VerticalLayoutGroup on the
/// scroll content, so a plain uGUI child with a LayoutElement is laid out like the built-in rows.
/// </summary>
public static class SettingsUi
{
    private static object Dialog;
    private static object AccountVm;
    private static object AccountIconView;

    private static readonly HashSet<IntPtr> Done = new();
    private static bool _forceDump;
    private static Sprite _solid;

    private static Toggle ToggleWidget;
    private static InputField TcpWidget;
    private static InputField McpWidget;

    public static string WidgetState()
    {
        return MiniJson.Serialize(new Dictionary<string, object>
        {
            ["enabled"] = ServerSettings.Enabled,
            ["tcp"] = ServerSettings.TcpAddress,
            ["mcp"] = ServerSettings.McpAddress,
            ["toggle"] = ToggleWidget != null ? ToggleWidget.isOn : (object)null,
            ["toggleInteractable"] = ToggleWidget != null ? ToggleWidget.interactable : (object)null,
            ["toggleCanvas"] = CanvasState(ToggleWidget != null ? ToggleWidget.transform : null),
            ["tcpField"] = TcpWidget != null ? TcpWidget.text : null,
            ["tcpInteractable"] = TcpWidget != null ? TcpWidget.interactable : (object)null,
            ["tcpCanvas"] = CanvasState(TcpWidget != null ? TcpWidget.transform : null),
            ["mcpField"] = McpWidget != null ? McpWidget.text : null,
            ["mcpInteractable"] = McpWidget != null ? McpWidget.interactable : (object)null,
        }, false);
    }

    private static string CanvasState(Transform t)
    {
        if (t == null) return null;
        var parts = new List<string>();
        var cur = t;
        while (cur != null)
        {
            var cg = cur.GetComponent<CanvasGroup>();
            if (cg != null) parts.Add($"{cur.name}:interactable={cg.interactable},blocks={cg.blocksRaycasts},alpha={cg.alpha:0.##}");
            cur = cur.parent;
        }
        return string.Join(" | ", parts);
    }

    public static string SetEnabledFromCommand(bool value)
    {
        if (ToggleWidget == null) return "no toggle";
        ToggleWidget.isOn = value; // fires onValueChanged
        return WidgetState();
    }

    public static string EditFromCommand(string which, string value)
    {
        var f = string.Equals(which, "mcp", StringComparison.OrdinalIgnoreCase) ? McpWidget : TcpWidget;
        if (f == null) return "no field";
        f.text = value;
        f.onEndEdit.Invoke(value); // exercise the real listener
        return WidgetState();
    }

    private static Sprite SolidSprite()
    {
        if (_solid != null) return _solid;
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        _solid = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 100f);
        return _solid;
    }

    public static void Register()
    {
        try
        {
            var dialogInit = new HarmonyMethod(typeof(SettingsUi).GetMethod(nameof(OnDialogInit), BindingFlags.Public | BindingFlags.Static));
            Hooks.PatchByTypeName("VRoid.Studio.AccountIcon.SettingDialog.SettingDialogTransform", "InitializeComponent", dialogInit);

            var account = new HarmonyMethod(typeof(SettingsUi).GetMethod(nameof(OnAccount), BindingFlags.Public | BindingFlags.Static));
            Hooks.PatchByTypeName("VRoid.Studio.AccountIcon.ViewModel", "SetupAccountMenuItemDataSources", account);
            Hooks.PatchByTypeName("VRoid.Studio.AccountIcon.ViewModel", "OnUpdateLanguage", account);

            var iconView = new HarmonyMethod(typeof(SettingsUi).GetMethod(nameof(OnAccountIconView), BindingFlags.Public | BindingFlags.Static));
            Hooks.PatchByTypeName("VRoid.Studio.View.AccountIconTransform", "InitializeComponent", iconView);

            var loginInit = new HarmonyMethod(typeof(SettingsUi).GetMethod(nameof(OnLoginDialogInit), BindingFlags.Public | BindingFlags.Static));
            Hooks.PatchByTypeName("VRoid.Studio.HubConnection.LoginDialog.View.LoginDialogTransform", "InitializeComponent", loginInit);
        }
        catch (Exception e) { PluginLog.LogError($"settings ui register: {e.Message}"); }
    }

    public static void OnLoginDialogInit(object __instance)
    {
        try
        {
            var body = Get(__instance, "Body") as RectTransform;
            if (body == null) return;
            var arr = body.GetComponentsInChildren<InputField>(true);
            if (arr.Length > 0) CacheNativeInputTemplate(arr[0]);
        }
        catch (Exception e) { PluginLog.LogError($"settings ui: login init: {e.Message}"); }
    }

    private static InputField _nativeInputTemplate;

    public static void OnDialogInit(object __instance) { if (__instance != null) { Dialog = __instance; PluginLog.LogDebug("settings ui: dialog captured"); } }
    public static void OnAccount(object __instance) { if (__instance != null) AccountVm = __instance; }
    public static void OnAccountIconView(object __instance) { if (__instance != null) AccountIconView = __instance; }

    public static object ResolveAccount()
    {
        if (AccountVm != null) return AccountVm;
        if (AccountIconView != null)
        {
            var ctx = Get(AccountIconView, "BindingContext");
            if (ctx != null && (TypeName(ctx) ?? "").Contains("AccountIcon")) AccountVm = ctx;
        }
        return AccountVm;
    }

    public static object Open()
    {
        var vm = ResolveAccount();
        if (vm == null) return "no account vm";
        try { Invoke(vm, "OpenSettings"); return "ok"; }
        catch (Exception e) { return e.Message; }
    }

    public static void RequestDump() { _forceDump = true; }

    public static void Tick()
    {
        if (Dialog == null) return;
        try
        {
            var content = Get(Dialog, "ScrollableDialogContent");
            if (content == null) return;
            var body = Get(content, "Body") as RectTransform;
            if (body == null) return;
            if (body is Il2CppObjectBase b)
            {
                if (!_forceDump && Done.Contains(b.Pointer)) return;
                Done.Add(b.Pointer);
            }
            bool forced = _forceDump;
            _forceDump = false;

            if (forced) { Dump(body, Path.Combine(BepInEx.Paths.GameRootPath, "settings_ui_dump.txt")); return; }
            BuildSection(body);
        }
        catch (Exception e) { PluginLog.LogError($"settings ui tick: {e.Message}"); }
    }

    // ---------------------------------------------------------------- section

    private static void BuildSection(RectTransform body)
    {
        var src = FindText(body);
        var font = src != null ? src.font : null;
        var mat = src != null ? src.material : null;
        var sprite = SolidSprite();
        var layer = body.gameObject.layer;

        // Native style sources inside the settings dialog. Labels are cloned from an always-enabled
        // row ("High"): cloning a disabled one (e.g. "English", greyed out while editing a model)
        // would inherit its grey colour.
        var titleSrc = FindTextByContent(body, "Language");
        var descSrc = FindTextContaining(body, "This setting is for using VRoid Studio");
        var labelSrc = FindTextByContent(body, "High") ?? FindTextByContent(body, "English");
        var inputSrc = FindNativeInput(body) ?? _nativeInputTemplate;
        PluginLog.LogDebug($"settings ui: sources title={titleSrc != null} desc={descSrc != null} label={labelSrc != null} input={(inputSrc != null ? inputSrc.gameObject.name : "none")}");

        var go = NewRect("VroidStudioMcpSection", body, layer, new Vector2(392f, 200f), Vector2.zero);
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = 392f; le.minWidth = 392f;
        le.preferredHeight = 200f; le.minHeight = 200f;
        le.flexibleHeight = 0f;
        go.GetComponent<RectTransform>().SetAsFirstSibling();

        CloneText(go.transform, titleSrc, font, mat, "Programmatic Control (MCP)",
            new Vector2(4f, -10f), new Vector2(380f, 18f), TextAnchor.MiddleLeft);
        CloneText(go.transform, descSrc, font, mat,
            "Exposes the editor to external tools over a local command socket and a Model Context Protocol (MCP) HTTP endpoint. " +
            "Enable this to let scripts drive sections, parameters, the camera and saving.",
            new Vector2(4f, -30f), new Vector2(380f, 56f), TextAnchor.UpperLeft, wrap: true);

        var toggle = MakeToggle(go.transform, body, layer, font, mat, new Vector2(4f, -92f));
        CloneText(go.transform, labelSrc, font, mat, "Enable programmatic control",
            new Vector2(28f, -92f), new Vector2(356f, 20f), TextAnchor.MiddleLeft);

        CloneText(go.transform, labelSrc, font, mat, "Command socket (TCP)",
            new Vector2(4f, -120f), new Vector2(164f, 24f), TextAnchor.MiddleLeft);
        MakeInput(go.transform, body, layer, font, mat, sprite, inputSrc, new Vector2(176f, -120f), new Vector2(204f, 26f),
            ServerSettings.TcpAddress, ServerSettings.DefaultTcp, OnTcpEdit, out var tcpField);

        CloneText(go.transform, labelSrc, font, mat, "MCP endpoint (HTTP)",
            new Vector2(4f, -152f), new Vector2(164f, 24f), TextAnchor.MiddleLeft);
        MakeInput(go.transform, body, layer, font, mat, sprite, inputSrc, new Vector2(176f, -152f), new Vector2(204f, 26f),
            ServerSettings.McpAddress, ServerSettings.DefaultMcp, OnMcpEdit, out var mcpField);

        if (toggle != null)
        {
            toggle.isOn = ServerSettings.Enabled;
            Action<bool> toggleCb = OnEnabledChanged;
            toggle.onValueChanged.AddListener(toggleCb);
        }
        ToggleWidget = toggle;
        TcpWidget = tcpField;
        McpWidget = mcpField;

        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(body);
        PluginLog.LogDebug($"settings ui: section built (toggle={toggle != null} tcp={tcpField != null} mcp={mcpField != null})");
    }

    private static Text CloneText(Transform parent, Text src, Font fallbackFont, Material fallbackMat,
        string content, Vector2 pos, Vector2 size, TextAnchor align, bool wrap = false)
    {
        Text t;
        if (src != null)
        {
            var clone = UnityEngine.Object.Instantiate(src.gameObject, parent);
            clone.name = "Text";
            clone.layer = parent.gameObject.layer;
            var rt = clone.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            t = clone.GetComponent<Text>();
        }
        else
        {
            var go = NewRect("Text", parent, parent.gameObject.layer, size, pos);
            t = go.AddComponent<Text>();
            if (fallbackFont != null) t.font = fallbackFont;
            if (fallbackMat != null) t.material = fallbackMat;
            t.color = new Color(0.15f, 0.15f, 0.2f);
        }
        t.text = content;
        t.alignment = align;
        t.horizontalOverflow = wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Truncate;
        t.raycastTarget = false;
        t.SetAllDirty();
        return t;
    }

    private static Text FindTextByContent(Transform t, string content)
    {
        var own = t.GetComponent<Text>();
        if (own != null && own.text == content) return own;
        for (int i = 0; i < t.childCount; i++)
        {
            var r = FindTextByContent(t.GetChild(i), content);
            if (r != null) return r;
        }
        return null;
    }

    private static Text FindTextContaining(Transform t, string part)
    {
        var own = t.GetComponent<Text>();
        if (own != null && own.text != null && own.text.Contains(part)) return own;
        for (int i = 0; i < t.childCount; i++)
        {
            var r = FindTextContaining(t.GetChild(i), part);
            if (r != null) return r;
        }
        return null;
    }

    private static InputField FindNativeInput(Transform body)
    {
        InputField fallback = null;
        try
        {
            var arr = Resources.FindObjectsOfTypeAll<InputField>();
            for (int i = 0; i < arr.Length; i++)
            {
                var f = arr[i];
                if (f == null) continue;
                var go = f.gameObject;
                if (go == null || !go.scene.IsValid()) continue;
                if (go.transform.IsChildOf(body)) continue;
                if (go.name.StartsWith("VroidStudioMcp") || go.name == "EnableToggle") continue;
                if (go.activeInHierarchy) return f;
                fallback ??= f;
            }
        }
        catch (Exception e) { PluginLog.LogError($"settings ui: find input: {e.Message}"); }
        return fallback;
    }

    private static void OnEnabledChanged(bool value)
    {
        try { ServerSettings.SetEnabled(value); PluginLog.LogDebug($"settings ui: enabled={value}"); }
        catch (Exception e) { PluginLog.LogError($"settings ui toggle: {e.Message}"); }
    }

    private static void OnTcpEdit(string value)
    {
        if (ServerSettings.SetTcpAddress(value)) PluginLog.LogDebug($"settings ui: tcp={ServerSettings.TcpAddress}");
        else
        {
            PluginLog.LogWarning($"settings ui: invalid tcp address '{value}'");
            if (TcpWidget != null && TcpWidget.text != ServerSettings.TcpAddress) TcpWidget.text = ServerSettings.TcpAddress;
        }
    }

    private static void OnMcpEdit(string value)
    {
        if (ServerSettings.SetMcpAddress(value)) PluginLog.LogDebug($"settings ui: mcp={ServerSettings.McpAddress}");
        else
        {
            PluginLog.LogWarning($"settings ui: invalid mcp address '{value}'");
            if (McpWidget != null && McpWidget.text != ServerSettings.McpAddress) McpWidget.text = ServerSettings.McpAddress;
        }
    }

    // ---------------------------------------------------------------- widgets

    private static GameObject NewRect(string name, Transform parent, int layer, Vector2 size, Vector2 pos)
    {
        var go = new GameObject(name) { layer = layer };
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = size;
        rt.anchoredPosition = pos;
        return go;
    }

    private static InputField MakeInput(Transform parent, Transform body, int layer, Font font, Material mat, Sprite sprite,
        InputField src, Vector2 pos, Vector2 size, string value, string placeholder, Action<string> onEnd, out InputField field)
    {
        if (src != null)
        {
            try
            {
                var clone = UnityEngine.Object.Instantiate(src.gameObject, parent);
                clone.name = "Input";
                clone.layer = layer;
                clone.SetActive(true);
                var crt = clone.GetComponent<RectTransform>();
                crt.anchorMin = new Vector2(0f, 1f);
                crt.anchorMax = new Vector2(0f, 1f);
                crt.pivot = new Vector2(0f, 1f);
                crt.sizeDelta = size;
                crt.anchoredPosition = pos;
                field = clone.GetComponent<InputField>();
                if (field != null)
                {
                    if (field.placeholder is Text phText) phText.text = placeholder;
                    field.text = value;
                    if (onEnd != null) field.onEndEdit.AddListener(onEnd);
                    CacheNativeInputTemplate(src);
                    PluginLog.LogDebug($"settings ui: cloned native input '{src.gameObject.name}'");
                    return field;
                }
            }
            catch (Exception e) { PluginLog.LogError($"settings ui: clone input failed: {e.Message}"); }
        }

        var go = NewRect("Input", parent, layer, size, pos);
        if (sprite != null)
        {
            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.type = Image.Type.Simple;
            img.color = new Color(0.94f, 0.94f, 0.96f);
            var ol = go.AddComponent<Outline>();
            ol.effectColor = new Color(0.65f, 0.65f, 0.7f);
            ol.effectDistance = new Vector2(1f, -1f);
        }
        field = go.AddComponent<InputField>();

        var textGo = NewRect("Text", go.transform, layer, Vector2.zero, Vector2.zero);
        var trt = textGo.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(6f, 3f); trt.offsetMax = new Vector2(-6f, -3f);
        var text = textGo.AddComponent<Text>();
        if (font != null) text.font = font;
        if (mat != null) text.material = mat;
        text.fontSize = 12;
        text.alignment = TextAnchor.MiddleLeft;
        text.color = new Color(0.1f, 0.1f, 0.15f);
        text.supportRichText = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.raycastTarget = false;

        var phGo = NewRect("Placeholder", go.transform, layer, Vector2.zero, Vector2.zero);
        var prt = phGo.GetComponent<RectTransform>();
        prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
        prt.offsetMin = new Vector2(6f, 3f); prt.offsetMax = new Vector2(-6f, -3f);
        var ph = phGo.AddComponent<Text>();
        if (font != null) ph.font = font;
        if (mat != null) ph.material = mat;
        ph.text = placeholder;
        ph.fontSize = 12;
        ph.fontStyle = FontStyle.Italic;
        ph.alignment = TextAnchor.MiddleLeft;
        ph.color = new Color(0.55f, 0.55f, 0.6f);
        ph.supportRichText = false;
        ph.horizontalOverflow = HorizontalWrapMode.Overflow;
        ph.verticalOverflow = VerticalWrapMode.Truncate;
        ph.raycastTarget = false;

        field.textComponent = text;
        field.placeholder = ph;
        field.text = value;
        field.contentType = InputField.ContentType.Standard;
        field.lineType = InputField.LineType.SingleLine;
        field.characterValidation = InputField.CharacterValidation.None;
        if (onEnd != null) field.onEndEdit.AddListener(onEnd);
        return field;
    }

    private static void CacheNativeInputTemplate(InputField src)
    {
        if (_nativeInputTemplate != null) return;
        try
        {
            var holder = new GameObject("VroidStudioMcpInputTemplate");
            UnityEngine.Object.DontDestroyOnLoad(holder);
            holder.SetActive(false);
            var clone = UnityEngine.Object.Instantiate(src.gameObject, holder.transform);
            clone.SetActive(false);
            _nativeInputTemplate = clone.GetComponent<InputField>();
            PluginLog.LogDebug("settings ui: cached native input template");
        }
        catch (Exception e) { PluginLog.LogError($"settings ui: cache input: {e.Message}"); }
    }

    private static Toggle MakeToggle(Transform parent, Transform body, int layer, Font font, Material mat, Vector2 pos)
    {
        try
        {
            var src = FindNativeCheckbox(body) ?? body.GetComponentInChildren<Toggle>(true);
            if (src != null)
            {
                var clone = UnityEngine.Object.Instantiate(src.gameObject, parent);
                clone.name = "EnableToggle";
                clone.layer = layer;
                clone.SetActive(true);
                var crt = clone.GetComponent<RectTransform>();
                crt.anchorMin = new Vector2(0f, 1f);
                crt.anchorMax = new Vector2(0f, 1f);
                crt.pivot = new Vector2(0f, 1f);
                crt.sizeDelta = new Vector2(20f, 20f);
                crt.anchoredPosition = pos;
                return clone.GetComponent<Toggle>();
            }
        }
        catch (Exception e) { PluginLog.LogError($"settings ui: toggle clone failed: {e.Message}"); }

        var go = NewRect("EnableToggle", parent, layer, new Vector2(20f, 20f), pos);
        var bg = go.AddComponent<Image>();
        bg.color = new Color(0.85f, 0.85f, 0.88f);
        var t2 = go.AddComponent<Toggle>();
        t2.targetGraphic = bg;
        return t2;
    }

    private static Toggle FindNativeCheckbox(Transform body)
    {
        Toggle fallback = null;
        try
        {
            var arr = Resources.FindObjectsOfTypeAll<Toggle>();
            for (int i = 0; i < arr.Length; i++)
            {
                var t = arr[i];
                if (t == null) continue;
                var go = t.gameObject;
                if (go == null || !go.scene.IsValid()) continue;
                var n = go.name;
                if (n.StartsWith("VroidStudioMcp") || n == "EnableToggle") continue;
                if (n.IndexOf("CheckBox", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (go.activeInHierarchy) { PluginLog.LogDebug($"settings ui: native checkbox '{n}'"); return t; }
                fallback ??= t;
            }
        }
        catch (Exception e) { PluginLog.LogError($"settings ui: find checkbox: {e.Message}"); }
        if (fallback != null) PluginLog.LogDebug($"settings ui: native checkbox (inactive) '{fallback.gameObject.name}'");
        return fallback;
    }

    private static Text FindText(Transform t)
    {
        var own = t.GetComponent<Text>();
        if (own != null) return own;
        for (int i = 0; i < t.childCount; i++)
        {
            var r = FindText(t.GetChild(i));
            if (r != null) return r;
        }
        return null;
    }

    // ---------------------------------------------------------------- debug

    private static void Dump(Transform root, string path)
    {
        var sb = new StringBuilder();
        Walk(root, 0, sb);
        File.WriteAllText(path, sb.ToString());
        PluginLog.LogDebug($"settings ui: dumped {path}");
    }

    private static void Walk(Transform t, int depth, StringBuilder sb)
    {
        try
        {
            var rt = t.GetComponent<RectTransform>();
            sb.Append(new string(' ', depth * 2)).Append(t.name);
            if (rt != null) sb.Append($" rect={rt.rect} size={rt.sizeDelta} pos={rt.anchoredPosition} min={rt.anchorMin} max={rt.anchorMax}");
            var txt = t.GetComponent<Text>();
            if (txt != null) sb.Append($" TEXT=\"{txt.text}\"");
            var cg = t.GetComponent<CanvasGroup>();
            if (cg != null) sb.Append($" CG(interactable={cg.interactable},blocks={cg.blocksRaycasts},alpha={cg.alpha:0.##})");
            var sel = t.GetComponent<Selectable>();
            if (sel != null) sb.Append($" SEL(interactable={sel.interactable})");
            sb.Append('\n');
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, sb);
        }
        catch { }
    }
}
