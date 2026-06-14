using Forms = System.Windows.Forms;

namespace ClaudeUsage.Services;

public static class ToastService
{
    private static Forms.NotifyIcon? _notifyIcon;
    private static DateTime _lastToast = DateTime.MinValue;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);

    private static bool _enabled = true;

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Helpers.StartupHelper.SaveHookSetting("ToastEnabled", value ? "1" : "0");
        }
    }

    public static void Initialize(Forms.NotifyIcon notifyIcon)
    {
        _notifyIcon = notifyIcon;
        var saved = Helpers.StartupHelper.GetHookSetting("ToastEnabled");
        _enabled = saved != "0"; // Default on
    }

    public static void Show(string type, string message)
    {
        if (!_enabled || _notifyIcon == null) return;
        if ((DateTime.Now - _lastToast) < Cooldown) return;

        var (title, icon) = type switch
        {
            "task_complete" => ("Claude Code", Forms.ToolTipIcon.Info),
            "needs_attention" => ("Claude Code - Action Needed", Forms.ToolTipIcon.Warning),
            "error" => ("Claude Code - Error", Forms.ToolTipIcon.Error),
            _ => ("Claude Code", Forms.ToolTipIcon.Info)
        };

        _lastToast = DateTime.Now;
        _notifyIcon.ShowBalloonTip(5000, title, message, icon);
    }
}
