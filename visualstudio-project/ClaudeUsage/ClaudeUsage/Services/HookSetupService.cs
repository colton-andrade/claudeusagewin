using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeUsage.Services;

public static class HookSetupService
{
    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "settings.json"
        );

    private static readonly string[] HookEvents =
        ["Stop", "Notification", "PreToolUse", "PostToolUse", "SubagentStart", "SubagentStop", "SessionEnd", "UserPromptSubmit"];

    public static bool AreHooksConfigured(int port)
    {
        if (port <= 0) return false;

        try
        {
            if (!File.Exists(SettingsPath)) return false;

            var json = File.ReadAllText(SettingsPath);
            var root = JsonNode.Parse(json);
            var hooks = root?["hooks"]?.AsObject();
            if (hooks == null) return false;

            // Check if at least the Stop hook points to our port
            var stopHooks = hooks["Stop"]?.AsArray();
            if (stopHooks == null) return false;

            var targetUrl = $"http://localhost:{port}/hooks/";
            return stopHooks.Any(entry =>
                entry?["hooks"]?.AsArray()?.Any(h =>
                    h?["url"]?.GetValue<string>()?.StartsWith(targetUrl) == true) == true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"HookSetup check error: {ex.Message}");
            return false;
        }
    }

    public static bool ConfigureHooks(int port)
    {
        if (port <= 0) return false;

        try
        {
            JsonNode root;

            if (File.Exists(SettingsPath))
            {
                var existing = File.ReadAllText(SettingsPath);
                root = JsonNode.Parse(existing) ?? new JsonObject();
            }
            else
            {
                // Ensure directory exists
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                root = new JsonObject();
            }

            var rootObj = root.AsObject();

            // Get or create hooks object
            if (rootObj["hooks"] is not JsonObject hooks)
            {
                hooks = new JsonObject();
                rootObj["hooks"] = hooks;
            }

            // Add our hook entries for each event
            foreach (var eventName in HookEvents)
            {
                var urlPath = eventName switch
                {
                    "PreToolUse" => "pre-tool-use",
                    "PostToolUse" => "post-tool-use",
                    "SubagentStart" => "subagent-start",
                    "SubagentStop" => "subagent-stop",
                    "SessionEnd" => "session-end",
                    "UserPromptSubmit" => "user-prompt-submit",
                    _ => eventName.ToLowerInvariant()
                };

                var hookUrl = $"http://localhost:{port}/hooks/{urlPath}";
                var hookEntry = new JsonObject
                {
                    ["matcher"] = "",
                    ["hooks"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "http",
                            ["url"] = hookUrl
                        }
                    }
                };

                // Get existing array or create new one
                if (hooks[eventName] is JsonArray existingArray)
                {
                    // Remove any existing ClaudeUsage entries (our localhost entries)
                    for (int i = existingArray.Count - 1; i >= 0; i--)
                    {
                        var entry = existingArray[i];
                        var entryHooks = entry?["hooks"]?.AsArray();
                        if (entryHooks?.Any(h =>
                            h?["url"]?.GetValue<string>()?.Contains("localhost:195") == true) == true)
                        {
                            existingArray.RemoveAt(i);
                        }
                    }
                    existingArray.Add(hookEntry);
                }
                else
                {
                    hooks[eventName] = new JsonArray { hookEntry };
                }
            }

            // Atomic write: write to temp file then rename
            var tempPath = SettingsPath + ".tmp";
            var jsonString = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempPath, jsonString);
            File.Move(tempPath, SettingsPath, overwrite: true);

            System.Diagnostics.Debug.WriteLine($"HookSetup: configured hooks for port {port}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"HookSetup configure error: {ex.Message}");
            return false;
        }
    }

    public static bool RemoveHooks()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return true;

            var json = File.ReadAllText(SettingsPath);
            var root = JsonNode.Parse(json);
            if (root == null) return true;

            var hooks = root["hooks"]?.AsObject();
            if (hooks == null) return true;

            foreach (var eventName in HookEvents)
            {
                if (hooks[eventName] is not JsonArray eventArray) continue;

                for (int i = eventArray.Count - 1; i >= 0; i--)
                {
                    var entry = eventArray[i];
                    var entryHooks = entry?["hooks"]?.AsArray();
                    if (entryHooks?.Any(h =>
                        h?["url"]?.GetValue<string>()?.Contains("localhost:195") == true) == true)
                    {
                        eventArray.RemoveAt(i);
                    }
                }

                // Remove the event key entirely if no hooks left
                if (eventArray.Count == 0)
                {
                    hooks.Remove(eventName);
                }
            }

            // Remove hooks key if empty
            if (hooks.Count == 0)
            {
                root.AsObject().Remove("hooks");
            }

            var tempPath = SettingsPath + ".tmp";
            var jsonString = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempPath, jsonString);
            File.Move(tempPath, SettingsPath, overwrite: true);

            System.Diagnostics.Debug.WriteLine("HookSetup: removed hooks");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"HookSetup remove error: {ex.Message}");
            return false;
        }
    }

    public static string GetHookConfigJson(int port, string? remoteHost = null)
    {
        var host = remoteHost ?? "localhost";
        var config = new JsonObject();
        var hooks = new JsonObject();

        foreach (var eventName in HookEvents)
        {
            var urlPath = eventName switch
            {
                "PreToolUse" => "pre-tool-use",
                "PostToolUse" => "post-tool-use",
                "SubagentStart" => "subagent-start",
                "SubagentStop" => "subagent-stop",
                "SessionEnd" => "session-end",
                "UserPromptSubmit" => "user-prompt-submit",
                _ => eventName.ToLowerInvariant()
            };

            hooks[eventName] = new JsonArray
            {
                new JsonObject
                {
                    ["matcher"] = "",
                    ["hooks"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "http",
                            ["url"] = $"http://{host}:{port}/hooks/{urlPath}"
                        }
                    }
                }
            };
        }

        config["hooks"] = hooks;

        var json = config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var settingsPath = Path.Combine("~", ".claude", "settings.json");
        var location = remoteHost != null
            ? $"//   Remote: ~/.claude/settings.json (on the remote machine)\n"
            : $"//   Windows: %USERPROFILE%\\.claude\\settings.json\n" +
              $"//   Linux/WSL: ~/.claude/settings.json\n";

        var header =
            $"// ClaudeUsage Widget — Hook Configuration{(remoteHost != null ? " (Remote)" : "")}\n" +
            $"// Merge the \"hooks\" section below into your Claude Code settings file:\n" +
            location +
            $"// If you already have a \"hooks\" key, add these entries to it.\n" +
            $"// The ClaudeUsage app must be running on Windows for hooks to work ({host}:{port}).\n\n";

        return header + json;
    }

    public static string GetRemoteSetupPrompt(string host, int port)
    {
        var installCmd = GetInstallCommand(host, port);
        var baseUrl = $"http://{host}:{port}/hooks";
        return
            "Set up ClaudeUsage hooks on this machine so my ClaudeUsage Windows widget can track " +
            "this Claude Code session remotely.\n\n" +
            "Easiest path — run this one command. It installs a tiny relay script and patches " +
            "~/.claude/settings.json:\n\n" +
            "```sh\n" +
            installCmd + "\n" +
            "```\n\n" +
            "The relay fails fast when the widget isn't reachable, so it never stalls the session, " +
            "and it reconnects automatically once the widget is running again.\n\n" +
            "If you'd rather not pipe to sh, you can add HTTP hooks manually instead (merge into any " +
            "existing \"hooks\" key). Note these wait up to \"timeout\" seconds when the widget is " +
            "offline, which is why the installer above is preferred:\n\n" +
            "```json\n" +
            "{\n" +
            "  \"hooks\": {\n" +
           $"    \"Stop\": [{{ \"matcher\": \"\", \"hooks\": [{{ \"type\": \"http\", \"url\": \"{baseUrl}/stop\", \"timeout\": 3 }}] }}],\n" +
           $"    \"Notification\": [{{ \"matcher\": \"\", \"hooks\": [{{ \"type\": \"http\", \"url\": \"{baseUrl}/notification\", \"timeout\": 3 }}] }}]\n" +
            "  }\n" +
            "}\n" +
            "```\n\n" +
           $"Verify the widget is reachable:\n" +
           $"  curl -s -m 3 -X POST {baseUrl}/stop -H \"Content-Type: application/json\" -d '{{}}'\n" +
            "It should return {\"result\":\"ok\"} if the Windows widget is running and reachable.";
    }

    /// <summary>
    /// The Moshi-style one-liner shown in the Remote setup UI. The ClaudeUsage
    /// widget's own HookServer serves the installer at /hooks/install.sh, so no
    /// external hosting is needed.
    /// </summary>
    public static string GetInstallCommand(string host, int port)
        => $"curl -fsSL http://{host}:{port}/hooks/install.sh | sh";

    /// <summary>
    /// The circuit-breaker relay script installed on the remote machine. Hooks call
    /// this instead of POSTing over HTTP directly: when the widget is unreachable it
    /// records a "down" marker and every subsequent hook for TTL seconds returns
    /// instantly with no network call, so an offline widget never stalls the session.
    /// The marker is re-probed after the TTL, so launching the widget mid-session is
    /// picked up automatically. It always exits 0 and so can never block a Claude
    /// Code operation. Line endings are normalized to LF for /bin/sh.
    /// </summary>
    public static string GetRelayHookScript(string host, int port)
        => RelayScriptTemplate
            .Replace("__HOST__", host)
            .Replace("__PORT__", port.ToString())
            .Replace("\r\n", "\n");

    /// <summary>
    /// The self-contained installer served at /hooks/install.sh. Writes the relay
    /// script and patches ~/.claude/settings.json (via python3, with a manual
    /// fallback message if python3 is absent). Line endings normalized to LF.
    /// </summary>
    public static string GetInstallScript(string host, int port)
        => InstallScriptTemplate
            .Replace("__HOST__", host)
            .Replace("__PORT__", port.ToString())
            .Replace("__HOOK_BODY__", GetRelayHookScript(host, port))
            .Replace("\r\n", "\n");

    private const string RelayScriptTemplate = """
        #!/bin/sh
        # ClaudeUsage hook relay (circuit breaker). Auto-installed by the ClaudeUsage
        # widget; safe to delete. Always exits 0 so it can never block or fail a
        # Claude Code operation, and skips instantly while the widget is unreachable.
        EVENT="$1"
        URL="http://__HOST__:__PORT__/hooks/$EVENT"
        STATE="${TMPDIR:-/tmp}/claudeusage-__PORT__.state"
        TTL=60
        PAYLOAD="$(cat)"
        now="$(date +%s)"
        if [ -f "$STATE" ]; then
          mark="$(cut -d' ' -f1 "$STATE" 2>/dev/null)"
          ts="$(cut -d' ' -f2 "$STATE" 2>/dev/null)"
          if [ "$mark" = down ] && [ "$((now - ${ts:-0}))" -lt "$TTL" ]; then
            exit 0
          fi
        fi
        if curl -s --connect-timeout 1 -m 2 -X POST "$URL" \
             -H 'Content-Type: application/json' --data-binary "$PAYLOAD" >/dev/null 2>&1; then
          echo "up $now" > "$STATE"
        else
          echo "down $now" > "$STATE"
        fi
        exit 0
        """;

    private const string InstallScriptTemplate = """
        #!/bin/sh
        # ClaudeUsage remote hook installer
        set -e
        CLAUDE_DIR="$HOME/.claude"
        HOOK_SCRIPT="$CLAUDE_DIR/claudeusage-hook.sh"
        mkdir -p "$CLAUDE_DIR"
        printf '%s\n' "Installing ClaudeUsage hook relay (target http://__HOST__:__PORT__)..."

        cat > "$HOOK_SCRIPT" <<'CU_HOOK_EOF'
        __HOOK_BODY__
        CU_HOOK_EOF
        chmod +x "$HOOK_SCRIPT"

        if command -v python3 >/dev/null 2>&1; then
        python3 - "$HOOK_SCRIPT" <<'CU_PY_EOF'
        import json, os, sys
        script = sys.argv[1]
        p = os.path.expanduser("~/.claude/settings.json")
        try:
            with open(p) as f:
                cfg = json.load(f)
        except Exception:
            cfg = {}
        if not isinstance(cfg, dict):
            cfg = {}
        hooks = cfg.setdefault("hooks", {})
        events = {
            "Stop": "stop",
            "Notification": "notification",
            "PreToolUse": "pre-tool-use",
            "PostToolUse": "post-tool-use",
            "SubagentStart": "subagent-start",
            "SubagentStop": "subagent-stop",
            "SessionEnd": "session-end",
            "UserPromptSubmit": "user-prompt-submit",
        }
        for ev, seg in events.items():
            arr = hooks.get(ev) or []
            arr = [e for e in arr if not any("claudeusage-hook.sh" in (h.get("command", "") or "") for h in (e.get("hooks") or []))]
            arr.append({"matcher": "", "hooks": [{"type": "command", "command": script + " " + seg, "timeout": 5}]})
            hooks[ev] = arr
        os.makedirs(os.path.dirname(p), exist_ok=True)
        tmp = p + ".tmp"
        with open(tmp, "w") as f:
            json.dump(cfg, f, indent=2)
        os.replace(tmp, p)
        print("Configured " + str(len(events)) + " hook events in " + p)
        CU_PY_EOF
        else
        printf '%s\n' "python3 not found: wrote $HOOK_SCRIPT but did NOT edit settings.json."
        printf '%s\n' "Add command hooks manually (tray menu: Hooks > Copy remote config)."
        fi

        if ! command -v curl >/dev/null 2>&1; then
        printf '%s\n' "WARNING: curl is not on PATH; the relay needs curl to deliver events."
        fi
        printf '%s\n' "Done. Restart any active Claude Code session to load the hooks."
        """;
}
