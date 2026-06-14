using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudeUsage.Models;
using ClaudeUsage.Services;

namespace ClaudeUsage;

public partial class LiveWidget : Window
{
    private readonly DispatcherTimer _elapsedTimer;
    private ClaudeSessionState? _expandedState;

    // Shared pulse templates + clocks. Every green dot is driven by the SAME clock
    // (and every amber dot by another), so they all pulse in phase no matter when
    // they appear — instead of each starting its own out-of-sync animation.
    private static readonly DoubleAnimation _greenPulse = new()
    {
        From = 0.4, To = 1.0,
        Duration = TimeSpan.FromMilliseconds(800),
        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
        EasingFunction = new SineEase()
    };
    private static readonly DoubleAnimation _amberPulse = new()
    {
        From = 0.35, To = 1.0,
        Duration = TimeSpan.FromMilliseconds(450),
        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
        EasingFunction = new SineEase()
    };
    private AnimationClock? _greenClock;
    private AnimationClock? _amberClock;
    private AnimationClock GreenClock => _greenClock ??= _greenPulse.CreateClock();
    private AnimationClock AmberClock => _amberClock ??= _amberPulse.CreateClock();

    // Win32: hide from alt-tab + force always on top
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // Low-level keyboard hook for double-tap Right Alt
    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandle(string? lpModuleName);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYUP = 0x0101;
    private const int VK_RMENU = 0xA5;

    private static nint _hookId;
    private static LowLevelKeyboardProc? _hookProc; // prevent GC
    private static DateTime _lastRightAltUp = DateTime.MinValue;
    private static LiveWidget? _instance;

    /// <summary>
    /// When false, the double-tap Right Alt shortcut is ignored. Toggled from the
    /// Hooks settings window and persisted via the "HotkeyEnabled" hook setting.
    /// </summary>
    public static bool HotkeyEnabled { get; set; } = true;

    /// <summary>
    /// Virtual-key code of the key the user double-taps to show/hide the widget.
    /// Defaults to Right Alt; chosen in the Hooks settings window and persisted via
    /// the "HotkeyVk" hook setting.
    /// </summary>
    public static int HotkeyVk { get; set; } = VK_RMENU;

    // Callback to notify state manager when user clicks a compact dot
    public event Action<string>? SessionExpandRequested;
    public event Action<string>? SessionDismissRequested;

    public LiveWidget()
    {
        _instance = this;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        HotkeyEnabled = Helpers.StartupHelper.GetHookSetting("HotkeyEnabled") != "0";
        HotkeyVk = int.TryParse(Helpers.StartupHelper.GetHookSetting("HotkeyVk"), out var vk) ? vk : VK_RMENU;
        InstallKeyboardHook();

        _elapsedTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _elapsedTimer.Tick += (_, _) => UpdateElapsed();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static void InstallKeyboardHook()
    {
        if (_hookId != nint.Zero) return;
        _hookProc = KeyboardHookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(curModule.ModuleName), 0);
    }

    private static nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && wParam == WM_KEYUP)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            if (vkCode == HotkeyVk && HotkeyEnabled)
            {
                var now = DateTime.UtcNow;
                var elapsed = (now - _lastRightAltUp).TotalMilliseconds;
                _lastRightAltUp = now;

                if (elapsed < 400) // double-tap within 400ms
                {
                    _lastRightAltUp = DateTime.MinValue; // reset to avoid triple-tap
                    _instance?.Dispatcher.BeginInvoke(() =>
                    {
                        if (_instance.IsVisible)
                        {
                            _instance.HideWithAnimation();
                            Helpers.StartupHelper.SaveHookSetting("WidgetVisible", "0");
                        }
                        else
                        {
                            _instance.ShowWithAnimation();
                            Helpers.StartupHelper.SaveHookSetting("WidgetVisible", "1");
                        }
                    });
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hookId != nint.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = nint.Zero;
        }
        base.OnClosed(e);
    }

    public void UpdateSessions(List<ClaudeSessionState> sessions, string? expandedId)
    {
        // Find expanded session
        _expandedState = sessions.FirstOrDefault(s => s.Id == expandedId)
                         ?? sessions.FirstOrDefault();

        // Render compact dots as mini pills for non-expanded sessions
        CompactDotsPanel.Children.Clear();
        foreach (var session in sessions)
        {
            if (session.Id == expandedId) continue;

            var dot = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(GetDotColorForStatus(session.Status)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };

            // Dark circle container
            var pill = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1A1A1A")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = session.SessionName ?? session.Id,
                Child = dot
            };
            pill.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10, ShadowDepth = 2, Opacity = 0.4, Color = Colors.Black
            };

            var sessionId = session.Id;
            pill.MouseLeftButtonUp += (_, _) => SessionExpandRequested?.Invoke(sessionId);
            pill.MouseUp += (_, me) =>
            {
                if (me.ChangedButton == MouseButton.Middle)
                    SessionDismissRequested?.Invoke(sessionId);
            };

            // Pulse busy/waiting dots. All share one clock per colour, so every green
            // dot pulses in phase and every amber dot pulses in phase. Static = done.
            if (session.Status is ClaudeStatus.Active or ClaudeStatus.WorkingTool or ClaudeStatus.WaitingPermission)
            {
                var fast = session.Status == ClaudeStatus.WaitingPermission;
                dot.ApplyAnimationClock(OpacityProperty, fast ? AmberClock : GreenClock);
            }

            CompactDotsPanel.Children.Add(pill);
        }

        // Update expanded session detail
        if (_expandedState != null)
            UpdateExpandedState(_expandedState);
        else
        {
            StatusRun.Text = "Claude Code";
            DetailRun.Text = "No sessions";
            ShowElapsed(false);
            SetDotColor("#6B7280");
            StopPulse();
            _elapsedTimer.Stop();
        }

        // Re-center
        if (IsVisible)
        {
            UpdateLayout();
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
        }
    }

    private void UpdateExpandedState(ClaudeSessionState state)
    {
        var label = state.SessionName ?? "Claude Code";

        switch (state.Status)
        {
            case ClaudeStatus.Active:
                StatusRun.Text = label;
                DetailRun.Text = "Thinking...";
                SetDotColor("#22C55E");
                StartPulse();
                _elapsedTimer.Start();
                ShowElapsed(true);
                break;

            case ClaudeStatus.WorkingTool:
                StatusRun.Text = label;
                var tool = TruncateTool(state.CurrentTool ?? "...");
                DetailRun.Text = $"{tool}  ·  {state.ToolCount} tools";
                SetDotColor("#22C55E");
                StartPulse();
                _elapsedTimer.Start();
                ShowElapsed(true);
                break;

            case ClaudeStatus.WaitingPermission:
                StatusRun.Text = label;
                DetailRun.Text = "Waiting for input";
                SetDotColor("#F59E0B");
                StartPulse(fast: true);
                _elapsedTimer.Stop();
                ShowElapsed(false);
                break;

            case ClaudeStatus.Idle:
                StatusRun.Text = label;
                DetailRun.Text = "Done";
                SetDotColor("#22C55E");
                StopPulse();
                _elapsedTimer.Stop();
                ShowElapsed(false);
                break;

            case ClaudeStatus.Error:
                StatusRun.Text = label;
                DetailRun.Text = "Error";
                SetDotColor("#EF4444");
                StopPulse();
                _elapsedTimer.Stop();
                ShowElapsed(false);
                break;

            case ClaudeStatus.Disconnected:
            default:
                StatusRun.Text = label;
                DetailRun.Text = "Disconnected";
                SetDotColor("#6B7280");
                StopPulse();
                _elapsedTimer.Stop();
                ShowElapsed(false);
                break;
        }

        UpdateElapsed();
    }

    private static System.Windows.Media.Color GetDotColorForStatus(ClaudeStatus status) => status switch
    {
        ClaudeStatus.Active or ClaudeStatus.WorkingTool or ClaudeStatus.Idle
            => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#22C55E"),
        ClaudeStatus.WaitingPermission
            => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F59E0B"),
        ClaudeStatus.Error
            => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"),
        _ => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#6B7280"),
    };

    private void UpdateElapsed()
    {
        if (_expandedState?.SessionStartedAt == null)
        {
            ShowElapsed(false);
            return;
        }
        ElapsedRun.Text = _expandedState.ElapsedFormatted;
    }

    private void ShowElapsed(bool show)
    {
        if (show)
        {
            SeparatorRun.Text = "  ·  ";
            if (_expandedState?.SessionStartedAt != null)
                ElapsedRun.Text = _expandedState.ElapsedFormatted;
        }
        else
        {
            SeparatorRun.Text = "";
            ElapsedRun.Text = "";
        }
    }

    private void SetDotColor(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        StatusDot.Fill = new SolidColorBrush(color);
    }

    private void StartPulse(bool fast = false)
    {
        StatusDot.ApplyAnimationClock(OpacityProperty, fast ? AmberClock : GreenClock);
    }

    private void StopPulse()
    {
        StatusDot.ApplyAnimationClock(OpacityProperty, null);
        StatusDot.Opacity = 1.0;
    }

    private static string TruncateTool(string tool)
    {
        return tool.Length > 20 ? tool[..17] + "..." : tool;
    }

    public void ShowWithAnimation()
    {
        if (IsVisible) return;

        var workArea = SystemParameters.WorkArea;

        Left = -9999;
        Top = -9999;
        Opacity = 0;
        Show();
        UpdateLayout();

        Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
        var finalTop = workArea.Bottom - ActualHeight - 4;

        Top = workArea.Bottom;

        _elapsedTimer.Start();

        var slideAnim = new DoubleAnimation
        {
            From = workArea.Bottom,
            To = finalTop,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        slideAnim.Completed += (_, _) =>
        {
            BeginAnimation(TopProperty, null);
            Top = finalTop;
        };

        var fadeAnim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200)
        };
        fadeAnim.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };

        BeginAnimation(TopProperty, slideAnim);
        BeginAnimation(OpacityProperty, fadeAnim);
    }

    public void HideWithAnimation()
    {
        if (!IsVisible) return;

        _elapsedTimer.Stop();

        var workArea = SystemParameters.WorkArea;

        var slideAnim = new DoubleAnimation
        {
            To = workArea.Bottom,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        var fadeAnim = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(200)
        };

        fadeAnim.Completed += (_, _) =>
        {
            Hide();
            BeginAnimation(TopProperty, null);
            BeginAnimation(OpacityProperty, null);
        };

        BeginAnimation(TopProperty, slideAnim);
        BeginAnimation(OpacityProperty, fadeAnim);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // Dismiss just this session. When it's the last one, OnSessionsChanged
        // hides the widget (no sessions left); otherwise the next session shows.
        var id = _expandedState?.Id;
        if (!string.IsNullOrEmpty(id))
            SessionDismissRequested?.Invoke(id);
        else
            HideWithAnimation();
    }
}
