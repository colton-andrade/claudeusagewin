namespace ClaudeUsage.Models;

public enum ClaudeStatus
{
    Disconnected,
    Active,
    WorkingTool,
    WaitingPermission,
    Idle,
    Error
}

public class ClaudeSessionState
{
    public string Id { get; set; } = "";
    public ClaudeStatus Status { get; set; } = ClaudeStatus.Disconnected;
    public string? CurrentTool { get; set; }
    public int ToolCount { get; set; }
    public int ActiveSubagents { get; set; }
    public DateTime? SessionStartedAt { get; set; }
    public DateTime? LastEventAt { get; set; }
    public string? SessionName { get; set; }

    public TimeSpan Elapsed =>
        SessionStartedAt.HasValue
            ? DateTime.UtcNow - SessionStartedAt.Value
            : TimeSpan.Zero;

    public string ElapsedFormatted
    {
        get
        {
            var e = Elapsed;
            if (e.TotalHours >= 1)
                return $"{(int)e.TotalHours}h {e.Minutes}m";
            if (e.TotalMinutes >= 1)
                return $"{e.Minutes}m {e.Seconds}s";
            return $"{e.Seconds}s";
        }
    }
}
