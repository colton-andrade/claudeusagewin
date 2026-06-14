using System.Text.Json;

namespace ClaudeUsage.Models;

public enum HookEventType
{
    Stop,
    Notification,
    PreToolUse,
    PostToolUse,
    SubagentStart,
    SubagentStop,
    SessionEnd,
    UserPromptSubmit,
    Unknown
}

public class HookEvent
{
    public HookEventType EventType { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    // Parsed from payload
    public string? SessionId { get; set; }
    public string? ToolName { get; set; }
    public string? Message { get; set; }
    public string? StopReason { get; set; }
    public string? Cwd { get; set; }
    public string? NotificationType { get; set; }

    // Raw payload for forward compatibility
    public JsonElement? RawPayload { get; set; }

    public static HookEventType ParseEventType(string path)
    {
        // Path comes in as e.g. "/hooks/stop" or "/hooks/pre-tool-use"
        var segment = path.TrimEnd('/').Split('/').LastOrDefault()?.ToLowerInvariant();
        return segment switch
        {
            "stop" => HookEventType.Stop,
            "notification" => HookEventType.Notification,
            "pre-tool-use" => HookEventType.PreToolUse,
            "post-tool-use" => HookEventType.PostToolUse,
            "subagent-start" => HookEventType.SubagentStart,
            "subagent-stop" => HookEventType.SubagentStop,
            "session-end" => HookEventType.SessionEnd,
            "user-prompt-submit" => HookEventType.UserPromptSubmit,
            _ => HookEventType.Unknown
        };
    }

    public static HookEvent FromRequest(string path, string jsonBody)
    {
        var evt = new HookEvent
        {
            EventType = ParseEventType(path)
        };

        if (string.IsNullOrWhiteSpace(jsonBody))
            return evt;

        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;
            evt.RawPayload = root.Clone();

            if (root.TryGetProperty("session_id", out var sid))
                evt.SessionId = sid.GetString();

            if (root.TryGetProperty("tool_name", out var tn))
                evt.ToolName = tn.GetString();
            else if (root.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.Object
                     && t.TryGetProperty("name", out var tName))
                evt.ToolName = tName.GetString();

            if (root.TryGetProperty("message", out var msg))
                evt.Message = msg.GetString();

            if (root.TryGetProperty("stop_reason", out var sr))
                evt.StopReason = sr.GetString();
            else if (root.TryGetProperty("reason", out var r))
                evt.StopReason = r.GetString();

            if (root.TryGetProperty("cwd", out var cwd))
                evt.Cwd = cwd.GetString();

            if (root.TryGetProperty("notification_type", out var notifType))
                evt.NotificationType = notifType.GetString();
        }
        catch
        {
            // Malformed JSON — keep what we have
        }

        return evt;
    }
}
