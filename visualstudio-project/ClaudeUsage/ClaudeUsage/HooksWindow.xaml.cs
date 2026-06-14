using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClaudeUsage.Helpers;
using ClaudeUsage.Services;
using Wpf.Ui.Controls;

namespace ClaudeUsage;

public partial class HooksWindow : FluentWindow
{
    private readonly HookServer? _hookServer;

    // Looping demo for the live-widget preview at the bottom of the window.
    private DispatcherTimer? _previewTimer;
    private int _previewIndex;

    private readonly (string Name, string Detail, string Elapsed, string Dot, bool Pulse, bool Fast, string CompactA, string CompactB)[] _previewScenes =
    {
        ("claude-usage", "Thinking…",        "0:04", "#22C55E", true,  false, "#F59E0B", "#3B82F6"),
        ("claude-usage", "Editing App.xaml.cs",   "1:12", "#22C55E", false, false, "#F59E0B", "#3B82F6"),
        ("api-server",   "Waiting for input",     "",     "#F59E0B", true,  true,  "#22C55E", "#3B82F6"),
        ("web-ui",       "Running npm test",      "2:38", "#22C55E", false, false, "#22C55E", "#F59E0B"),
        ("docs-site",    "Done",                  "5:01", "#22C55E", false, false, "#22C55E", "#F59E0B"),
    };

    private readonly DoubleAnimation _previewPulse =
        new(0.35, 1.0, new Duration(TimeSpan.FromMilliseconds(750)))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };

    private readonly DoubleAnimation _previewFastPulse =
        new(0.35, 1.0, new Duration(TimeSpan.FromMilliseconds(380)))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };

    public HooksWindow(HookServer? hookServer)
    {
        _hookServer = hookServer;
        InitializeComponent();
        PopulateState();
        StartPreviewLoop();
    }

    private void StartPreviewLoop()
    {
        ApplyPreviewScene(0, animate: false);
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewIndex = (_previewIndex + 1) % _previewScenes.Length;
            ApplyPreviewScene(_previewIndex, animate: true);
        };
        _previewTimer.Start();
    }

    private void ApplyPreviewScene(int index, bool animate)
    {
        var s = _previewScenes[index];

        void Swap()
        {
            PreviewName.Text = s.Name;
            PreviewDetail.Text = s.Detail;
            var hasElapsed = !string.IsNullOrEmpty(s.Elapsed);
            PreviewSep.Text = hasElapsed ? "  ·  " : "";
            PreviewElapsed.Text = s.Elapsed;

            PreviewDot.Fill = Hex(s.Dot);
            PreviewCompactA.Fill = Hex(s.CompactA);
            PreviewCompactB.Fill = Hex(s.CompactB);

            PreviewDot.BeginAnimation(OpacityProperty, null);
            if (s.Pulse)
                PreviewDot.BeginAnimation(OpacityProperty, s.Fast ? _previewFastPulse : _previewPulse);
            else
                PreviewDot.Opacity = 1;
        }

        if (!animate)
        {
            Swap();
            PreviewText.Opacity = 1;
            return;
        }

        var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(160)));
        fadeOut.Completed += (_, _) =>
        {
            Swap();
            PreviewText.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220))));
        };
        PreviewText.BeginAnimation(OpacityProperty, fadeOut);
    }

    private static SolidColorBrush Hex(string hex)
        => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    protected override void OnClosed(EventArgs e)
    {
        _previewTimer?.Stop();
        base.OnClosed(e);
    }

    private int Port => _hookServer?.Port ?? 0;
    private bool ServerRunning => _hookServer?.IsRunning == true;

    /// <summary>
    /// Reachable host for the install command: Tailscale IP, else this PC's LAN IP,
    /// else a placeholder the user must edit.
    /// </summary>
    private string Host() =>
        HookServer.DetectTailscaleIp() ?? HookServer.DetectLanIp() ?? "YOUR_WINDOWS_IP";

    private void PopulateState()
    {
        if (ServerRunning)
        {
            StatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x52, 0xD1, 0x7C));
            StatusText.Text = $"Listening on port {Port}";
        }
        else
        {
            StatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x78, 0x82, 0x8C));
            StatusText.Text = "Not running";
        }

        EnableHooksToggle.IsChecked = ServerRunning && HookSetupService.AreHooksConfigured(Port);
        EnableHooksToggle.IsEnabled = ServerRunning;
        ToastToggle.IsChecked = ToastService.Enabled;
        HotkeyToggle.IsChecked = StartupHelper.GetHookSetting("HotkeyEnabled") != "0";
        HotkeyKeyText.Text = HotkeyKeyName(
            int.TryParse(StartupHelper.GetHookSetting("HotkeyVk"), out var hv) ? hv : 0xA5);
        RemoteToggle.IsChecked = StartupHelper.GetHookSetting("RemoteEnabled") == "1";

        InstallCommandBox.Text = ServerRunning
            ? HookSetupService.GetInstallCommand(Host(), Port)
            : "(start the hook server first)";

        var tailscale = HookServer.DetectTailscaleIp();
        var lan = HookServer.DetectLanIp();
        if (tailscale != null)
        {
            HostNote.Text = $"Using your Tailscale address {tailscale} — reachable from anywhere on your tailnet.";
        }
        else if (lan != null)
        {
            HostNote.Text = $"Using this PC's local IP {lan} (works on the same network). " +
                "For reliable access across networks, install Tailscale and the command will use that IP instead.";
        }
        else
        {
            HostNote.Text = "Couldn't detect an address — replace YOUR_WINDOWS_IP with an IP this PC is " +
                "reachable at from the remote machine. A Tailscale IP is the most reliable option.";
        }

        // Remote commands only matter once the server is bound for remote access.
        var remoteReady = ServerRunning;
        InstallCommandBox.IsEnabled = remoteReady;
    }

    private async void EnableHooksToggle_Click(object sender, RoutedEventArgs e)
    {
        // The switch has already flipped to the requested state; confirm before
        // touching the user's Claude Code settings file, and revert on Cancel.
        if (!ServerRunning)
        {
            EnableHooksToggle.IsChecked = false;
            return;
        }

        var enabling = EnableHooksToggle.IsChecked == true;

        var message = enabling
            ? "ClaudeUsage will add real-time hook entries to your Claude Code settings file:\n\n" +
              "    %USERPROFILE%\\.claude\\settings.json\n\n" +
              "Claude Code will then send session events (start, tool use, finish, exit) to this " +
              "widget so it can show live activity. Your existing settings are preserved, and you " +
              "can turn this off here at any time."
            : "ClaudeUsage will remove its hook entries from your Claude Code settings file:\n\n" +
              "    %USERPROFILE%\\.claude\\settings.json\n\n" +
              "The widget will stop tracking sessions. Your other settings are left untouched.";

        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = enabling ? "Enable real-time hooks" : "Disable real-time hooks",
            Content = message,
            PrimaryButtonText = enabling ? "Enable" : "Disable",
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            CloseButtonText = "Cancel",
        };

        var result = await dialog.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            EnableHooksToggle.IsChecked = !enabling; // revert; programmatic change doesn't re-fire Click
            return;
        }

        if (enabling)
        {
            HookSetupService.ConfigureHooks(Port);
            StartupHelper.SaveHookSetting("Enabled", "1");
            ShowFeedback("Real-time hooks enabled — restart any active Claude Code session to load them.");
        }
        else
        {
            HookSetupService.RemoveHooks();
            StartupHelper.SaveHookSetting("Enabled", "0");
            ShowFeedback("Real-time hooks removed from your Claude Code settings.");
        }
    }

    private void ToastToggle_Click(object sender, RoutedEventArgs e)
    {
        ToastService.Enabled = ToastToggle.IsChecked == true;
    }

    private void HotkeyToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = HotkeyToggle.IsChecked == true;
        LiveWidget.HotkeyEnabled = enabled;
        StartupHelper.SaveHookSetting("HotkeyEnabled", enabled ? "1" : "0");
    }

    // Keys offered for the double-tap shortcut (modifiers report distinct L/R
    // virtual-key codes to the low-level hook).
    private static readonly (string Name, int Vk)[] HotkeyKeys =
    {
        ("Right Alt", 0xA5), ("Left Alt", 0xA4),
        ("Right Ctrl", 0xA3), ("Left Ctrl", 0xA2),
        ("Right Shift", 0xA1), ("Left Shift", 0xA0),
    };

    private static string HotkeyKeyName(int vk)
    {
        foreach (var k in HotkeyKeys)
            if (k.Vk == vk) return k.Name;
        return "Right Alt";
    }

    private void HotkeyEdit_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = HotkeyEditButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (var (name, vk) in HotkeyKeys)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = name,
                IsChecked = vk == LiveWidget.HotkeyVk,
            };
            var capturedVk = vk;
            var capturedName = name;
            item.Click += (_, _) =>
            {
                LiveWidget.HotkeyVk = capturedVk;
                StartupHelper.SaveHookSetting("HotkeyVk", capturedVk.ToString());
                HotkeyKeyText.Text = capturedName;
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private async void RemoteToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = RemoteToggle.IsChecked == true;
        StartupHelper.SaveHookSetting("RemoteEnabled", enabled ? "1" : "0");
        RemoteRestartNote.Visibility = Visibility.Visible;

        if (!enabled) return;

        // Adding the firewall rules is what actually makes remote (and WSL) reachable.
        // Only prompt for elevation once.
        if (StartupHelper.GetHookSetting("FirewallConfigured") == "1") return;

        var port = Port > 0 ? Port : 19532;
        ShowFeedback("Requesting permission to add a Windows Firewall rule…");
        var ok = await Task.Run(() => FirewallHelper.TryAddRules(port));
        if (ok)
        {
            StartupHelper.SaveHookSetting("FirewallConfigured", "1");
            ShowFeedback("Firewall rule added. Restart ClaudeUsage to finish enabling remote access.");
        }
        else
        {
            ShowFeedback("Couldn't add the firewall rule (admin required). Run this in an elevated PowerShell:  "
                + FirewallHelper.GetManualCommand(port));
        }
    }

    private void CopyInstallCommand_Click(object sender, RoutedEventArgs e)
    {
        if (!ServerRunning) return;
        Copy(HookSetupService.GetInstallCommand(Host(), Port));
    }

    private void CopyLocalConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!ServerRunning) return;
        Copy(HookSetupService.GetHookConfigJson(Port));
    }

    private void CopyRemoteConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!ServerRunning) return;
        Copy(HookSetupService.GetHookConfigJson(Port, Host()));
    }

    private void Copy(string text)
    {
        try
        {
            System.Windows.Clipboard.SetDataObject(text, true);
            ShowFeedback("Copied to clipboard");
        }
        catch
        {
            ShowFeedback("Couldn't access the clipboard — try again");
        }
    }

    private void ShowFeedback(string message)
    {
        CopyFeedback.Text = message;
        CopyFeedback.Visibility = Visibility.Visible;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
