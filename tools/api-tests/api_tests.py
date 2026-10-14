#!/usr/bin/env python3
"""External API test harness for vroid-studio-mcp.

Runs against a *running* app (TCP command socket + MCP HTTP endpoint) and checks the things an
in-plugin test cannot: the transport/protocol itself, queue semantics under concurrency, and an
end-to-end "change -> the MCP-screenshot actually changed" render scenario.

The in-plugin tests (Tests/*.cs, `test <name> <path>` over TCP) cover deterministic engine
behaviour on the main thread. This harness is deliberately external: an in-process test that called
the MCP endpoint would deadlock, because MCP calls and the test coroutine share the same
main-thread command queue.

Usage (assumes the app is already running):
    python3 tools/api-tests/api_tests.py --model /path/to/model.vroid
    python3 tools/api-tests/api_tests.py --suite protocol
    python3 tools/api-tests/api_tests.py --list

Exit code is non-zero if any check fails.
"""
import argparse
import base64
import hashlib
import json
import os
import socket
import sys
import tempfile
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor

DEFAULT_TCP = "127.0.0.1:17890"
DEFAULT_MCP = "http://127.0.0.1:17891/mcp/"


class Fail(Exception):
    pass


class Harness:
    def __init__(self, tcp, mcp, model, timeout, verbose=False):
        self.tcp_host, self.tcp_port = tcp.rsplit(":", 1)
        self.tcp_port = int(self.tcp_port)
        self.mcp = mcp
        self.model = model
        self.timeout = timeout
        self.verbose = verbose
        self.passed = 0
        self.failed = 0
        self._id = 0

    # ---------- reporting ----------
    def check(self, name, cond, detail=None):
        if cond:
            self.passed += 1
            print(f"  PASS {name}" + (f"  {detail}" if detail else ""))
        else:
            self.failed += 1
            print(f"  FAIL {name}" + (f"  {detail}" if detail else ""))

    def info(self, msg):
        if self.verbose:
            print(f"       {msg}")

    # ---------- MCP ----------
    def _next_id(self):
        self._id += 1
        return self._id

    def mcp_raw(self, payload, timeout=None):
        body = payload.encode() if isinstance(payload, str) else json.dumps(payload).encode()
        req = urllib.request.Request(self.mcp, data=body, headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=timeout or self.timeout) as r:
                return r.status, r.read()
        except urllib.error.HTTPError as e:
            return e.code, e.read()

    def mcp_rpc(self, method, params=None, timeout=None):
        payload = {"jsonrpc": "2.0", "id": self._next_id(), "method": method}
        if params is not None:
            payload["params"] = params
        status, raw = self.mcp_raw(payload, timeout)
        doc = json.loads(raw) if raw else None
        return status, doc

    def tool_raw(self, name, args=None):
        return self.mcp_rpc("tools/call", {"name": name, "arguments": args or {}})

    def tool_text(self, name, args=None, timeout=None):
        status, doc = self.mcp_rpc("tools/call", {"name": name, "arguments": args or {}}, timeout)
        if doc is None or "result" not in doc:
            raise Fail(f"{name}: no result ({status} {doc})")
        res = doc["result"]
        text = "".join(b.get("text", "") for b in res.get("content", []))
        return res.get("isError", False), text

    def tool_ok(self, name, args=None, timeout=None):
        err, text = self.tool_text(name, args, timeout)
        if err:
            raise Fail(f"{name}: {text}")
        return text

    def screenshot_sha(self):
        status, doc = self.mcp_rpc("tools/call", {"name": "screenshot", "arguments": {"mode": "inline"}})
        if doc is None or "result" not in doc:
            raise Fail(f"screenshot: no result ({status})")
        for block in doc["result"].get("content", []):
            if block.get("type") == "image" and block.get("data"):
                return hashlib.sha256(base64.b64decode(block["data"])).hexdigest()
        raise Fail("screenshot: no image block")

    def stable_sha(self, need=3, timeout=15.0):
        """Screenshot hash once `need` consecutive frames agree (scene settled)."""
        last = None
        stable = 0
        t0 = time.time()
        while time.time() - t0 < timeout:
            h = self.screenshot_sha()
            if h == last:
                stable += 1
                if stable >= need:
                    return h
            else:
                stable = 1
                last = h
            time.sleep(0.05)
        return last

    # ---------- TCP ----------
    def tcp_line(self, line, timeout=None):
        with socket.create_connection((self.tcp_host, self.tcp_port), timeout=5) as s:
            s.settimeout(timeout or self.timeout)
            f = s.makefile("rw", encoding="utf-8", newline="\n")
            f.write(line + "\n")
            f.flush()
            return f.readline().rstrip("\n")

    def tcp_state(self):
        return json.loads(self.tcp_line("state"))["result"]

    def at_model_select(self):
        return self.tcp_state()["in_edit"] is False

    def ensure_model_select(self):
        if not self.at_model_select():
            self.tcp_line("main_menu")
            for _ in range(40):
                if self.at_model_select():
                    return
                time.sleep(0.25)

    # ---------- suites ----------
    def suite_protocol(self):
        print("[protocol] MCP JSON-RPC")
        status, doc = self.mcp_rpc("initialize", {"protocolVersion": "2025-06-18"})
        self.check("initialize.200", status == 200)
        self.check("initialize.server", doc and doc["result"]["serverInfo"]["name"] == "vroid-studio-mcp")
        self.check("initialize.version", doc and doc["result"]["protocolVersion"] == "2025-06-18")

        status, doc = self.mcp_rpc("initialize", {"protocolVersion": "1999-01-01"})
        self.check("initialize.unknown-version-negotiated",
                   doc and doc["result"]["protocolVersion"] == "2025-06-18",
                   doc and doc["result"]["protocolVersion"])

        status, doc = self.mcp_rpc("tools/list", {})
        names = [t["name"] for t in doc["result"]["tools"]] if doc else []
        for need in ("state", "sections", "open", "enter", "set_param", "screenshot", "exit", "batch", "help", "status"):
            self.check(f"tools-list.{need}", need in names)

        # status is answered out of the main-thread queue and reports queue depth
        try:
            err, text = self.tool_text("status", {})
            sd = json.loads(text) if not err else {}
            self.check("status.shape", (not err) and "queued" in sd, (text or "")[:80])
        except Exception as e:
            self.check("status.shape", False, repr(e)[:100])

        status, doc = self.mcp_rpc("nope/nope", {})
        self.check("unknown-method.-32601", doc and doc["error"]["code"] == -32601)

        # notifications (no id) must not receive a body
        status, raw = self.mcp_raw('{"jsonrpc":"2.0","method":"ping"}')
        self.check("notification.no-body", status == 202 and not raw, f"HTTP {status}")

        # malformed JSON -> JSON-RPC parse error over HTTP 200
        status, raw = self.mcp_raw("this is not json")
        doc = json.loads(raw) if raw else None
        self.check("parse-error.-32700", status == 200 and doc and doc["error"]["code"] == -32700, f"HTTP {status}")

        # top-level JSON-RPC array (batching) is not supported -> graceful -32600, not an HTTP 500
        status, raw = self.mcp_raw(json.dumps([{"jsonrpc": "2.0", "id": 1, "method": "ping"}]))
        doc = json.loads(raw) if raw else None
        self.check("batch-array.-32600", status == 200 and doc and doc.get("error", {}).get("code") == -32600,
                   f"HTTP {status} {raw[:80]!r}")
        # any non-object root is an invalid request, not a server fault
        status, raw = self.mcp_raw('"just a string"')
        doc = json.loads(raw) if raw else None
        self.check("non-object.-32600", status == 200 and doc and doc.get("error", {}).get("code") == -32600,
                   f"HTTP {status} {raw[:80]!r}")

        # wrong-typed fields must be clean JSON-RPC errors, never an HTTP 500
        for name, body in (("method-number", '{"jsonrpc":"2.0","id":1,"method":5}'),
                           ("id-object", '{"jsonrpc":"2.0","id":{"a":1},"method":"ping"}')):
            status, raw = self.mcp_raw(body)
            doc = json.loads(raw) if raw else None
            self.check(f"malformed.{name}.{'-32600'}",
                       status == 200 and doc and doc.get("error", {}).get("code") == -32600,
                       f"HTTP {status} {raw[:80]!r}")
        status, raw = self.mcp_raw('{"jsonrpc":"2.0","id":1,"method":"tools/call","params":[1]}')
        self.check("malformed.params-array-no500", status == 200, f"HTTP {status}")

        # id echo (integer and string)
        for want in (7, "abc"):
            status, raw = self.mcp_raw(json.dumps({"jsonrpc": "2.0", "id": want, "method": "ping"}))
            doc = json.loads(raw)
            self.check(f"id-echo.{want}", doc.get("id") == want)

        # connection churn: many sequential fresh connections must all be served (a chunked/keep-alive
        # leak used to stall the listener after ~140 requests)
        bad = 0
        for _ in range(300):
            status, raw = self.mcp_raw('{"jsonrpc":"2.0","id":1,"method":"ping"}')
            if status != 200 or not raw:
                bad += 1
        self.check("sequential-300", bad == 0, f"{bad} failures")

    def suite_tcp(self):
        print("[tcp] command socket")
        r = json.loads(self.tcp_line("ping"))
        self.check("ping", r.get("ok") and r.get("result") == "pong")
        r = json.loads(self.tcp_line("nosuchcmd"))
        self.check("unknown-command", r.get("ok") is False and "error" in r)
        r = json.loads(self.tcp_line("привет"))
        self.check("unicode-preserved", r.get("ok") is False and "привет" in r.get("error", ""), r.get("error"))
        # batch array
        r = json.loads(self.tcp_line('["ping","state"]'))
        self.check("batch-array", isinstance(r, list) and len(r) == 2 and r[0].get("result") == "pong")
        # json-rpc line
        r = json.loads(self.tcp_line('{"jsonrpc":"2.0","id":5,"method":"ping"}'))
        self.check("jsonrpc-line", r.get("id") == 5 and "result" in r)

    def suite_queue(self):
        print("[queue] concurrency + batching")
        # concurrent read-only requests must all succeed and not deadlock. Retry transient
        # connection errors (the OS/http.sys backlog can drop a few under a cold burst).
        def one(_):
            for _attempt in range(3):
                try:
                    err, _t = self.tool_text("state", {})
                    return not err
                except Exception:
                    time.sleep(0.2)
            return False
        self.tool_text("state", {})  # warm up
        with ThreadPoolExecutor(max_workers=40) as ex:
            res = list(ex.map(one, range(40)))
        self.check("concurrent-40", all(res), f"{sum(res)}/40")

        # a large batch must complete and return one result per op
        n = 200
        status, doc = self.mcp_rpc("tools/call", {"name": "batch",
                                  "arguments": {"operations": [{"method": "ping"} for _ in range(n)]}},
                                   timeout=max(self.timeout, 60))
        text = doc["result"]["content"][0]["text"] if doc and "result" in doc else "[]"
        try:
            arr = json.loads(text)
        except Exception:
            arr = []
        self.check("large-batch-200", len(arr) == n and all(x.get("ok") for x in arr), f"{len(arr)}/{n}")

        # ordering: last write wins, read back through the production handler
        if self.model:
            if self.at_model_select():
                self.tool_ok("open", {"path": self.model})
            path = "Face/Face Sets/Eye Size X"
            for v in ("0.7", "0.2", "0.9"):
                self.tool_ok("set_param", {"section": "Face", "category": "Face Sets", "index": path, "value": v})
            txt = self.tool_ok("enter", {"section": "Face", "category": "Face Sets", "view": "keep"})
            data = json.loads(txt)
            back = [p["value"] for p in data["parameters"] if p["path"] == path]
            self.check("ordering.last-wins", back and abs(float(back[0]) - 0.9) < 1e-6, back)
            self.tool_ok("set_param", {"section": "Face", "category": "Face Sets", "index": path, "value": "0.537"})
        else:
            self.info("ordering check skipped (no model)")

    def suite_guards(self):
        print("[guards] session state machine at the model select")
        self.ensure_model_select()
        for name, args in (("save", {}), ("enter", {"section": "Face", "category": "Face Sets"}),
                           ("set_param", {"section": "Face", "category": "Face Sets", "index": "0", "value": "0"}),
                           ("camera", {"action": "reset"}), ("screenshot", {})):
            err, text = self.tool_text(name, args)
            self.check(f"{name}.rejected-at-select", err and "no model open" in text, text)
        # ui_screenshot is intentionally available without a model (when the dev tool is enabled)
        status, doc = self.mcp_rpc("tools/list", {})
        names = [t["name"] for t in doc["result"]["tools"]] if doc else []
        if "ui_screenshot" in names:
            err, text = self.tool_text("ui_screenshot", {"mode": "inline"}, timeout=max(self.timeout, 30))
            self.check("ui_screenshot.allowed-at-select", not err, text[:80])
        err, text = self.tool_text("open", {"path": "/no/such/model.vroid"})
        self.check("open.missing-file", err and "file not found" in text, text)
        err, text = self.tool_text("open", {"path": "/tmp"})
        self.check("open.directory", err and ("not a file" in text or "file not found" in text), text)

    def suite_render(self):
        print("[render] change -> MCP screenshot changed (end-to-end)")
        if not self.model:
            self.info("skipped (pass --model)")
            return
        self.ensure_model_select()
        self.tool_ok("open", {"path": self.model})
        st = self.tcp_state()
        self.check("open.edit-ready", st["in_edit"] and st["ready"], st)

        # names are case-sensitive: exact section/category works, a case variant is rejected
        err, text = self.tool_text("enter", {"section": "face", "category": "Irises"})
        self.check("case.section-strict", err and "unknown section" in text, text)
        err, text = self.tool_text("enter", {"section": "Face", "category": "irises"})
        self.check("case.tab-strict", err and "no tab" in text, text)

        # numeric
        txt = self.tool_ok("enter", {"section": "Face", "category": "Face Sets", "view": "keep"})
        data = json.loads(txt)
        path = "Face/Face Sets/Eye Size X"
        orig = next((p["value"] for p in data["parameters"] if p["path"] == path), None)
        self.tool_ok("set_param", {"section": "Face", "category": "Face Sets", "index": path, "value": "1.0"})
        h_hi = self.stable_sha()
        self.tool_ok("set_param", {"section": "Face", "category": "Face Sets", "index": path, "value": "-1.0"})
        h_lo = self.stable_sha()
        self.check("numeric.screenshot-changed", h_hi != h_lo, f"{str(h_hi)[:8]} vs {str(h_lo)[:8]}")
        rb = json.loads(self.tool_ok("enter", {"section": "Face", "category": "Face Sets", "view": "keep"}))
        rb = next((p["value"] for p in rb["parameters"] if p["path"] == path), None)
        self.check("numeric.readback", rb is not None and abs(float(rb) + 1.0) < 1e-6, rb)
        if orig is not None:
            self.tool_ok("set_param", {"section": "Face", "category": "Face Sets", "index": path, "value": str(orig)})

        # color
        self.tool_ok("enter", {"section": "Face", "category": "Irises", "view": "keep"})
        self.tool_ok("set_param", {"section": "Face", "category": "Irises", "index": "Face/Irises/Both Eyes", "value": "#FF0000"})
        h_r = self.stable_sha()
        self.tool_ok("set_param", {"section": "Face", "category": "Irises", "index": "Face/Irises/Both Eyes", "value": "#00FF00"})
        h_g = self.stable_sha()
        self.check("color.screenshot-changed", h_r != h_g, f"{str(h_r)[:8]} vs {str(h_g)[:8]}")

        # batch returns captured frames inline (apply -> camera -> capture in one call)
        status, doc = self.mcp_rpc("tools/call", {"name": "batch", "arguments": {"operations": [
            {"method": "enter", "args": ["Face", "Irises", "keep"]},
            {"method": "camera", "args": ["frame", "face"]},
            {"method": "screenshot", "args": ["inline"]},
        ]}}, timeout=max(self.timeout, 60))
        content = doc["result"]["content"] if doc and "result" in doc else []
        imgs = [c for c in content if isinstance(c, dict) and c.get("type") == "image"]
        self.check("batch.inline-image", len(imgs) == 1 and imgs[0].get("mimeType") == "image/png",
                   f"{len(content)} content blocks, {len(imgs)} images")

        # globals are reported apart from the category parameter list
        data = json.loads(self.tool_ok("enter", {"section": "Face", "category": "Nose", "view": "keep"}))
        ppaths = [p["path"] for p in data.get("parameters", [])]
        gpaths = [p["path"] for p in data.get("globals", [])]
        self.check("globals.skin-separated", "Skin/Color" in gpaths and "Skin/Color" not in ppaths, gpaths)

        # presets() reports the applied selection (read-only): each entry carries `selected`, and at
        # most one real preset is marked; the clear slot carries null.
        plist = json.loads(self.tool_ok("presets", {"section": "Face", "category": "Irises"}))
        self.check("presets.selected-field", all("selected" in e for e in plist) and len(plist) > 1, f"{len(plist)} entries")
        self.check("presets.selected-single", sum(1 for e in plist if e.get("selected") is True) <= 1,
                   [e.get("name") for e in plist if e.get("selected") is True])

        # invalid set: error + screenshot unchanged (stable, so residual settling cannot fool it).
        # Enter the target category first: entering a category can itself change the preview
        # (edit-mode highlight), and set_param always re-enters it, so compare within one category.
        self.tool_ok("enter", {"section": "Face", "category": "Face Sets", "view": "keep"})
        h0 = self.stable_sha()
        err, text = self.tool_text("set_param", {"section": "Face", "category": "Face Sets", "index": path, "value": "NaN"})
        self.check("invalid.nan-error", err and "finite" in text, text)
        err, text = self.tool_text("set_param", {"section": "Face", "category": "Face Sets", "index": "Face/Face Sets/Nope", "value": "0"})
        self.check("invalid.missing-error", err, text)
        h1 = self.stable_sha()
        self.check("invalid.no-render-change", h0 == h1, f"{str(h0)[:8]} vs {str(h1)[:8]}")

        # save-as to a directory: error and fast (must not wait the full fallback timeout on failure)
        t0 = time.time()
        err, text = self.tool_text("save", {"path": "/tmp"}, timeout=max(self.timeout, 30))
        dt = time.time() - t0
        self.check("save-badpath.error", err and "save failed" in text, text)
        self.check("save-badpath.fast", dt < 5.0, f"{dt:.2f}s")

        # failed open: fast + session recoverable
        self.ensure_model_select()
        fd, bad = tempfile.mkstemp(suffix=".vroid")
        os.write(fd, b"not a vroid model\n")
        os.close(fd)
        t0 = time.time()
        err, text = self.tool_text("open", {"path": bad}, timeout=max(self.timeout, 30))
        dt = time.time() - t0
        self.check("failed-open.error", err and "failed to load" in text, text)
        self.check("failed-open.fast", dt < 5.0, f"{dt:.2f}s")
        os.unlink(bad)
        st = self.tcp_state()
        self.check("failed-open.session-reset", st["in_edit"] is False, st)
        self.tool_ok("open", {"path": self.model})
        self.check("failed-open.reopen-ok", self.tcp_state()["in_edit"] is True)

    def suite_stress(self):
        print("[stress] edge arguments must error, not hang")
        self.ensure_model_select()
        if self.model:
            self.tool_ok("open", {"path": self.model})
        cases = [
            ("camera", {"action": "rotate", "value": 999999}, True),
            ("camera", {"action": "frame", "region": "nose"}, True),
            ("camera", {"action": "frame", "region": "face", "angle": "up"}, True),
            ("set_param", {"section": "Face", "category": "Irises", "index": "Face/Irises/Both Eyes", "value": "#11223344"}, True),
            ("preset_preview_range", {"section": "Face", "category": "Face Sets", "start": 2147483647, "count": 12}, True),
            ("preset_preview_list", {"section": "Face", "category": "Face Sets", "indexes": list(range(1, 20))}, True),
        ]
        for name, args, want_err in cases:
            t0 = time.time()
            err, text = self.tool_text(name, args, timeout=max(self.timeout, 45))
            self.check(f"{name}.rejected", bool(err) == want_err and (time.time() - t0) < 30, text[:80])

    # ---------- misuse matrix ----------
    # The threat model is "honest but careless client", not a DoS attacker: every command must answer
    # a wrong/missing/ill-typed argument with a clean error, never HTTP 500, a hang, or a wedged
    # session. After every case the server must still answer (MCP ping bypasses the queue) and a
    # model-open session must stay in_edit.
    def _misuse_case(self, name, tool, args, want_err=True, timeout=None):
        try:
            err, text = self.tool_text(tool, args, timeout)
        except Exception as e:
            self.check(f"misuse.{name}", False, repr(e)[:100])
            return
        self.check(f"misuse.{name}", bool(err) == want_err, (text or "")[:90])

    def _alive(self, model_expected=False):
        try:
            status, doc = self.mcp_rpc("ping", {})
            if status != 200 or not doc or "result" not in doc:
                return False, f"ping HTTP {status}"
        except Exception as e:
            return False, f"ping {e!r}"
        try:
            status, doc = self.mcp_rpc("tools/call", {"name": "state", "arguments": {}},
                                       timeout=max(self.timeout, 15))
            st = json.loads(doc["result"]["content"][0]["text"]) if doc and "result" in doc else {}
        except Exception as e:
            return False, f"state {e!r}"
        if model_expected and st.get("in_edit") is not True:
            return False, f"state={st}"
        return True, ""

    def suite_misuse(self):
        print("[misuse] malformed/incorrect usage against every command")
        # --- model-select context: model-dependent commands must refuse cleanly, not crash ---
        self.ensure_model_select()
        required = [
            ("categories", {"section": "Face"}),
            ("enter", {"section": "Face", "category": "Irises"}),
            ("presets", {"section": "Face", "category": "Irises"}),
            ("select_preset", {"section": "Face", "category": "Irises", "index": 0}),
            ("set_param", {"section": "Face", "category": "Irises", "index": "0", "value": "0"}),
            ("camera", {"action": "reset"}),
            ("screenshot", {}),
            ("save", {}),
            ("preset_preview_list", {"section": "Face", "category": "Irises", "indexes": [0]}),
            ("preset_preview_range", {"section": "Face", "category": "Irises"}),
        ]
        for tool, args in required:
            self._misuse_case(f"select.{tool}.no-model", tool, args, want_err=True)
        for name, tool, args in (
            ("open.missing", "open", {}),
            ("open.badtype", "open", {"path": ["x"]}),
            ("open.dir", "open", {"path": "/tmp"}),
            ("open.nofile", "open", {"path": "/no/such/model.vroid"}),
            ("create.missing", "create", {}),
            ("create.badgender", "create", {"gender": "cat"}),
            ("create.badtype", "create", {"gender": ["x"]}),
        ):
            self._misuse_case(f"select.{name}", tool, args, want_err=True)
        self.check("misuse.alive-after-select", self._alive()[0], "server must still answer")

        if not self.model:
            self.info("model-open matrix skipped (pass --model)")
            return
        self.tool_ok("open", {"path": self.model})

        cases = [
            # categories: args missing / unknown / wrong case / wrong type
            ("categories.noarg", "categories", {}, True),
            ("categories.unknown", "categories", {"section": "Nope"}, True),
            ("categories.lowercase", "categories", {"section": "face"}, True),
            ("categories.badtype", "categories", {"section": 5}, True),
            # enter
            ("enter.nocat", "enter", {"section": "Face"}, True),
            ("enter.noargs", "enter", {}, True),
            ("enter.unknownsec", "enter", {"section": "Nope", "category": "Irises"}, True),
            ("enter.unknowntab", "enter", {"section": "Face", "category": "Nope"}, True),
            ("enter.lowertab", "enter", {"section": "Face", "category": "irises"}, True),
            ("enter.badview", "enter", {"section": "Face", "category": "Irises", "view": "nope"}, True),
            ("enter.badtype", "enter", {"section": "Face", "category": 5}, True),
            # presets
            ("presets.noarg", "presets", {}, True),
            ("presets.unknowntab", "presets", {"section": "Face", "category": "Nope"}, True),
            # select_preset
            ("select.missing", "select_preset", {"section": "Face", "category": "Irises"}, True),
            ("select.negative", "select_preset", {"section": "Face", "category": "Irises", "index": -1}, True),
            ("select.oob", "select_preset", {"section": "Face", "category": "Irises", "index": 9999}, True),
            ("select.nonint", "select_preset", {"section": "Face", "category": "Irises", "index": "abc"}, True),
            ("select.float", "select_preset", {"section": "Face", "category": "Irises", "index": 1.9}, True),
            # set_param
            ("set.missing", "set_param", {"section": "Face", "category": "Irises"}, True),
            ("set.noargs", "set_param", {}, True),
            ("set.unknownparam", "set_param", {"section": "Face", "category": "Irises", "index": "Nope", "value": "1"}, True),
            ("set.badfloat", "set_param", {"section": "Face", "category": "Irises", "index": "Face/Irises/Iris Size (X)", "value": "abc"}, True),
            ("set.nan", "set_param", {"section": "Face", "category": "Irises", "index": "Face/Irises/Iris Size (X)", "value": "NaN"}, True),
            ("set.inf", "set_param", {"section": "Face", "category": "Irises", "index": "Face/Irises/Iris Size (X)", "value": "Infinity"}, True),
            ("set.badcolor", "set_param", {"section": "Face", "category": "Irises", "index": "Face/Irises/Both Eyes", "value": "#ZZZ"}, True),
            ("set.color-oob", "set_param", {"section": "Face", "category": "Irises", "index": "Face/Irises/Both Eyes", "value": "2,0,0"}, True),
            ("set.crosscat", "set_param", {"section": "Face", "category": "Irises", "index": "Face/Eyebrows/Eyebrow Thickness", "value": "0.5"}, True),
            ("set.oob-index", "set_param", {"section": "Face", "category": "Irises", "index": "9999", "value": "0.5"}, True),
            ("set.badtype", "set_param", {"section": "Face", "category": "Irises", "index": ["x"], "value": "0.5"}, True),
            # camera: every validated action with a missing/invalid value
            ("cam.noaction", "camera", {}, True),
            ("cam.unknown", "camera", {"action": "nope"}, True),
            ("cam.frame-badregion", "camera", {"action": "frame", "region": "nose"}, True),
            ("cam.frame-badangle", "camera", {"action": "frame", "region": "face", "angle": "up"}, True),
            ("cam.angles-onearg", "camera", {"action": "angles", "x": 10}, True),
            ("cam.angles-nan", "camera", {"action": "angles", "x": "NaN", "y": 0}, True),
            ("cam.zoom-noarg", "camera", {"action": "zoom"}, True),
            ("cam.zoom-huge", "camera", {"action": "zoom", "value": 1e30}, True),
            ("cam.angles-huge", "camera", {"action": "angles", "x": 1e30, "y": 0}, True),
            ("cam.view-onearg", "camera", {"action": "view", "x": 0.5}, True),
            ("cam.rotate-bad", "camera", {"action": "rotate", "value": 99}, True),
            # screenshot
            ("shot.badmode", "screenshot", {"mode": "nope"}, True),
            # save
            ("save.badpath", "save", {"path": "/tmp"}, True),
            ("save.missing-parent", "save", {"path": "/no/such/dir/m.vroid"}, True),
            # previews
            ("prev.list.missing", "preset_preview_list", {"section": "Face", "category": "Irises"}, True),
            ("prev.list.empty", "preset_preview_list", {"section": "Face", "category": "Irises", "indexes": []}, True),
            ("prev.list.oob", "preset_preview_list", {"section": "Face", "category": "Irises", "indexes": [9999]}, True),
            ("prev.list.many", "preset_preview_list", {"section": "Face", "category": "Irises", "indexes": list(range(1, 20))}, True),
            ("prev.list.badmode", "preset_preview_list", {"section": "Face", "category": "Irises", "indexes": [1], "mode": "nope"}, True),
            ("prev.range.neg", "preset_preview_range", {"section": "Face", "category": "Irises", "start": -1}, True),
            ("prev.range.count0", "preset_preview_range", {"section": "Face", "category": "Irises", "start": 1, "count": 0}, True),
            ("prev.range.count13", "preset_preview_range", {"section": "Face", "category": "Irises", "start": 1, "count": 13}, True),
            ("prev.range.oob", "preset_preview_range", {"section": "Face", "category": "Irises", "start": 99999}, True),
            # batch: malformed top-level shape is a tool error; per-op failures surface element-wise
            ("batch.noops", "batch", {}, True),
            ("batch.ops-object", "batch", {"operations": {"method": "state"}}, True),
            ("batch.nested", "batch", {"operations": [{"method": "batch", "args": []}]}, False),
            ("batch.unknown", "batch", {"operations": [{"method": "nope", "args": []}]}, False),
            # read-only tools: extra/odd arguments must be tolerated, not error
            ("state.extra", "state", {"x": 1}, False),
            ("sections.extra", "sections", {"x": 1}, False),
            ("status.extra", "status", {"x": 1}, False),
            ("help.unknown-topic", "help", {"topic": "nope"}, False),
        ]
        alive_fail = []
        for name, tool, args, want in cases:
            self._misuse_case(name, tool, args, want)
            ok, detail = self._alive(model_expected=True)
            if not ok:
                alive_fail.append(f"{name}: {detail}")
        self.check("misuse.alive-after-each-case", not alive_fail, "; ".join(alive_fail[:3]))
        # batch reports per-op failures element-wise (the tool call itself is ok)
        for nm, ops, sub in (("batch.nested", [{"method": "batch", "args": []}], "cannot nest batch"),
                             ("batch.unknown", [{"method": "nope", "args": []}], "unknown method")):
            try:
                err, text = self.tool_text("batch", {"operations": ops})
                self.check(f"misuse.{nm}.element", (not err) and sub in (text or ""), (text or "")[:90])
            except Exception as e:
                self.check(f"misuse.{nm}.element", False, repr(e)[:100])
        self.tcp_line("main_menu")

    SUITES = {
        "protocol": "suite_protocol",
        "tcp": "suite_tcp",
        "queue": "suite_queue",
        "guards": "suite_guards",
        "render": "suite_render",
        "stress": "suite_stress",
        "misuse": "suite_misuse",
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--tcp", default=DEFAULT_TCP)
    ap.add_argument("--mcp", default=DEFAULT_MCP)
    ap.add_argument("--model", default=None, help=".vroid path for the render suite (required; '' to skip)")
    ap.add_argument("--suite", action="append", choices=list(Harness.SUITES), help="run only these suites (repeatable)")
    ap.add_argument("--timeout", type=float, default=30.0)
    ap.add_argument("--list", action="store_true", help="list suites and exit")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()

    if args.list:
        for s in Harness.SUITES:
            print(s)
        return 0

    if args.model is None:
        print("--model is required (a .vroid path; pass '' to skip the render suite)", file=sys.stderr)
        return 2

    h = Harness(args.tcp, args.mcp, args.model, args.timeout, args.verbose)

    # fail fast if the endpoints are not up
    try:
        h.tcp_line("ping", timeout=5)
    except Exception as e:
        print(f"TCP {args.tcp} not reachable: {e}", file=sys.stderr)
        return 2
    try:
        st, doc = h.mcp_rpc("ping", {})
        if st != 200:
            raise Fail(f"HTTP {st}")
    except Exception as e:
        print(f"MCP {args.mcp} not reachable: {e}", file=sys.stderr)
        return 2

    suites = args.suite or list(Harness.SUITES)
    for s in suites:
        try:
            getattr(h, Harness.SUITES[s])()
        except Fail as e:
            h.failed += 1
            print(f"  FAIL {s}: {e}")
        except Exception as e:
            h.failed += 1
            print(f"  ERROR {s}: {type(e).__name__}: {e}")

    print(f"\n{h.passed} passed, {h.failed} failed")
    return 1 if h.failed else 0


if __name__ == "__main__":
    sys.exit(main())
