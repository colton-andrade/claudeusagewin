using System.Windows.Threading;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

public class HookStateManager
{
    private readonly Dictionary<string, ClaudeSessionState> _sessions = new();
    private readonly Dictionary<string, DispatcherTimer> _timeoutTimers = new();
    private string? _expandedSessionId;

    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(10);

    // Fires whenever any session changes — provides all sessions + which is expanded
    public event Action<List<ClaudeSessionState>, string?>? SessionsChanged;
    public event Action<string, string>? NotificationRequested; // (type, message)

    // Tools that mean Claude is waiting for the user
    private static readonly HashSet<string> WaitingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "AskUserQuestion", "Elicitation"
    };

    public List<ClaudeSessionState> AllSessions => _sessions.Values.ToList();
    public string? ExpandedSessionId => _expandedSessionId;

    public void ProcessEvent(HookEvent evt)
    {
        // Session key: session_id preferred, then cwd. Events with neither — e.g. an
        // empty health-check POST (`curl .../hooks/stop -d '{}'`, including our own
        // docs' verify command) — are ignored so they don't spawn a phantom unnamed
        // "Claude Code" session in the widget.
        var sessionKey = !string.IsNullOrEmpty(evt.SessionId) ? evt.SessionId : evt.Cwd;
        if (string.IsNullOrEmpty(sessionKey))
            return;

        // Claude Code exited — remove this session from the widget immediately
        // instead of waiting for the inactivity timeout.
        if (evt.EventType == HookEventType.SessionEnd)
        {
            DismissSession(sessionKey);
            return;
        }

        // Get or create session
        if (!_sessions.TryGetValue(sessionKey, out var session))
        {
            session = new ClaudeSessionState { Id = sessionKey };
            _sessions[sessionKey] = session;
        }

        // Reset per-session inactivity timer
        ResetTimeout(sessionKey);

        session.LastEventAt = DateTime.UtcNow;

        // Update session name from cwd
        if (!string.IsNullOrEmpty(evt.Cwd))
        {
            var name = evt.Cwd.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault();
            if (!string.IsNullOrEmpty(name))
                session.SessionName = name;
        }

        // If this is the first session, auto-expand it
        if (_expandedSessionId == null)
            _expandedSessionId = sessionKey;

        var previousStatus = session.Status;

        switch (evt.EventType)
        {
            case HookEventType.PreToolUse:
                HandlePreToolUse(session, evt);
                break;
            case HookEventType.PostToolUse:
                session.CurrentTool = null;
                session.Status = ClaudeStatus.Active;
                break;
            case HookEventType.UserPromptSubmit:
                // Fires the moment the user sends a message — show "Thinking…"
                // immediately, before any tool runs. Start a fresh turn if idle.
                if (session.Status is ClaudeStatus.Disconnected or ClaudeStatus.Idle)
                    StartSession(session);
                session.Status = ClaudeStatus.Active;
                session.CurrentTool = null;
                break;
            case HookEventType.Stop:
                HandleStop(session, evt);
                break;
            case HookEventType.Notification:
                HandleNotification(session, evt);
                break;
            case HookEventType.SubagentStart:
                session.ActiveSubagents++;
                if (session.Status is ClaudeStatus.Disconnected or ClaudeStatus.Idle)
                    StartSession(session);
                break;
            case HookEventType.SubagentStop:
                session.ActiveSubagents = Math.Max(0, session.ActiveSubagents - 1);
                break;
            default:
                if (session.Status == ClaudeStatus.Disconnected)
                    StartSession(session);
                break;
        }

        // Auto-expand: WaitingPermission always takes priority
        if (session.Status == ClaudeStatus.WaitingPermission && previousStatus != ClaudeStatus.WaitingPermission)
            _expandedSessionId = sessionKey;

        // Auto-expand: active session takes over from idle expanded session
        if (session.Status is ClaudeStatus.Active or ClaudeStatus.WorkingTool
            && _expandedSessionId != sessionKey
            && _sessions.TryGetValue(_expandedSessionId ?? "", out var expanded)
            && expanded.Status is ClaudeStatus.Idle or ClaudeStatus.Disconnected)
            _expandedSessionId = sessionKey;

        // If expanded session disconnected, pick the best remaining one
        if (_expandedSessionId == sessionKey && session.Status == ClaudeStatus.Disconnected)
            _expandedSessionId = PickBestSession();

        // Always expand the most recent active session if nothing is expanded
        _expandedSessionId ??= PickBestSession();

        EmitChange();
    }

    public void SetExpandedSession(string sessionId)
    {
        if (_sessions.ContainsKey(sessionId))
        {
            _expandedSessionId = sessionId;
            EmitChange();
        }
    }

    private void EmitChange()
    {
        SessionsChanged?.Invoke(_sessions.Values.ToList(), _expandedSessionId);
    }

    private string? PickBestSession()
    {
        // Priority: WaitingPermission > Active/WorkingTool > Idle > Disconnected
        return _sessions.Values
            .Where(s => s.Status != ClaudeStatus.Disconnected)
            .OrderByDescending(s => s.Status == ClaudeStatus.WaitingPermission ? 3 : 0)
            .ThenByDescending(s => s.Status is ClaudeStatus.Active or ClaudeStatus.WorkingTool ? 2 : 0)
            .ThenByDescending(s => s.LastEventAt ?? DateTime.MinValue)
            .FirstOrDefault()?.Id;
    }

    private void HandlePreToolUse(ClaudeSessionState session, HookEvent evt)
    {
        if (session.Status is ClaudeStatus.Disconnected or ClaudeStatus.Idle)
            StartSession(session);

        if (WaitingTools.Contains(evt.ToolName ?? ""))
        {
            session.Status = ClaudeStatus.WaitingPermission;
            session.CurrentTool = null;
            NotificationRequested?.Invoke("needs_attention",
                evt.Message ?? "Claude Code needs your input");
            return;
        }

        session.Status = ClaudeStatus.WorkingTool;
        session.CurrentTool = evt.ToolName;
        session.ToolCount++;
    }

    private void HandleStop(ClaudeSessionState session, HookEvent evt)
    {
        var reason = evt.StopReason?.ToLowerInvariant();

        switch (reason)
        {
            case "end_turn":
                session.Status = ClaudeStatus.Idle;
                session.CurrentTool = null;
                NotificationRequested?.Invoke("task_complete",
                    evt.Message ?? LocalizationService.T("toast_task_complete"));
                break;
            case "tool_use":
                session.Status = ClaudeStatus.WaitingPermission;
                session.CurrentTool = null;
                NotificationRequested?.Invoke("needs_attention",
                    evt.Message ?? LocalizationService.T("toast_needs_attention"));
                break;
            case "max_tokens":
                session.Status = ClaudeStatus.Idle;
                session.CurrentTool = null;
                NotificationRequested?.Invoke("needs_attention",
                    evt.Message ?? LocalizationService.T("toast_token_limit"));
                break;
            default:
                session.Status = ClaudeStatus.Idle;
                session.CurrentTool = null;
                if (!string.IsNullOrEmpty(evt.Message))
                    NotificationRequested?.Invoke("task_complete", evt.Message);
                break;
        }
    }

    private void HandleNotification(ClaudeSessionState session, HookEvent evt)
    {
        if (session.Status == ClaudeStatus.Disconnected)
            StartSession(session);

        if (evt.NotificationType == "permission_prompt")
        {
            session.Status = ClaudeStatus.WaitingPermission;
            session.CurrentTool = null;
            NotificationRequested?.Invoke("needs_attention",
                evt.Message ?? "Claude Code needs your permission");
        }
        else if (!string.IsNullOrEmpty(evt.Message))
        {
            NotificationRequested?.Invoke("notification", evt.Message);
        }
    }

    private static void StartSession(ClaudeSessionState session)
    {
        session.Status = ClaudeStatus.Active;
        session.SessionStartedAt = DateTime.UtcNow;
        session.ToolCount = 0;
        session.ActiveSubagents = 0;
        session.CurrentTool = null;
    }

    private void ResetTimeout(string sessionKey)
    {
        if (_timeoutTimers.TryGetValue(sessionKey, out var oldTimer))
        {
            oldTimer.Stop();
            _timeoutTimers.Remove(sessionKey);
        }

        var timer = new DispatcherTimer { Interval = InactivityTimeout };
        var key = sessionKey;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _sessions.Remove(key);
            _timeoutTimers.Remove(key);
            if (_expandedSessionId == key)
                _expandedSessionId = PickBestSession();
            EmitChange();
        };

        _timeoutTimers[sessionKey] = timer;
        timer.Start();
    }

    public void DismissSession(string sessionId)
    {
        if (_timeoutTimers.TryGetValue(sessionId, out var timer))
        {
            timer.Stop();
            _timeoutTimers.Remove(sessionId);
        }
        _sessions.Remove(sessionId);
        if (_expandedSessionId == sessionId)
            _expandedSessionId = PickBestSession();
        EmitChange();
    }
}
