using System;
using System.Collections.Generic;

namespace VroidStudioMcp;

/// <summary>Registry of named tests, driven by the command server.</summary>
public static class TestRunner
{
    private static readonly Dictionary<string, Func<VroidTest>> Factories = new()
    {
        ["selftest"] = () => new SelfTest(),
        ["scopetest"] = () => new ScopeTest(),
        ["icons"] = () => new IconTest(),
        ["colormap"] = () => new ColorMap(),
        ["colortestall"] = () => new ColorTestAll(),
        ["colortest"] = () => new ColorTest(),
        ["haircolortest"] = () => new HairColorTest(),
        ["cattest"] = () => new CatTest(),
        ["catwalk"] = () => new CatWalk(),
        ["presettest"] = () => new PresetTest(),
        ["paramwalk"] = () => new ParamWalk(),
        ["presetwalk"] = () => new PresetWalk(),
        ["basehairtest"] = () => new BaseHairTest(),
        ["eyetest"] = () => new EyeTest(),
        ["exittest"] = () => new ExitTest(),
        ["camtest"] = () => new CamTest(),
        ["camwalk"] = () => new CamWalk(),
        ["mcptest"] = () => new McpTest(),
        ["labeltest"] = () => new LabelTest(),
        ["createtest"] = () => new CreateTest(),
        ["previewtest"] = () => new PreviewTest(),
        ["timescalefuzz"] = () => new TimeScaleFuzzTest(),
        ["pathtest"] = () => new PathTest(),
        ["apitest"] = () => new ApiTest(),
        ["colorparse"] = () => new ColorParseTest(),
        ["camstate"] = () => new CameraStateTest(),
        ["hairglobal"] = () => new HairGlobalColorTest(),
        ["hairclear"] = () => new HairClearTest(),
        ["hairrestore"] = () => new HairRestoreTest(),
        ["renderchange"] = () => new RenderChangeTest(),
        ["sidebar"] = () => new SidebarTest(),
        ["thumbnaildirect"] = () => new ThumbnailDirectTest(),
        ["customitemtest"] = () => new CustomItemTest(),
    };

    public static VroidTest Create(string name, string path)
    {
        if (string.IsNullOrEmpty(name) || !Factories.TryGetValue(name, out var factory)) return null;
        var test = factory();
        test.CharacterPath = path;
        return test;
    }
}
