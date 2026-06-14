using System.Diagnostics;

namespace ClaudeUsage.Services;

/// <summary>
/// Adds the Windows Firewall rules needed for remote sessions to reach the hook
/// server. Two rules are required: a standard Defender inbound rule (for physical
/// machines on the LAN) and a Hyper-V firewall rule for the WSL vSwitch — on
/// Windows 11 the Hyper-V firewall defaults to blocking WSL→host inbound, which is
/// what silently breaks WSL hooks.
/// </summary>
public static class FirewallHelper
{
    // Well-known VMCreatorId of the WSL Hyper-V vSwitch.
    private const string WslVmCreatorId = "{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}";

    private const string RuleName = "ClaudeUsage Hooks";

    /// <summary>
    /// Attempts to add the firewall rules, prompting for elevation (UAC). Returns
    /// true if the elevated process completed successfully, false if the user
    /// declined UAC or the command failed. Blocks until the UAC flow finishes, so
    /// call it off the UI thread.
    /// </summary>
    public static bool TryAddRules(int port)
    {
        var script =
            "$ErrorActionPreference='SilentlyContinue';" +
            // Standard inbound rule (LAN / physical machines)
            $"if(-not (Get-NetFirewallRule -DisplayName '{RuleName}')){{" +
            $"New-NetFirewallRule -DisplayName '{RuleName}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {port} | Out-Null}};" +
            // Hyper-V rule for WSL (Windows 11 22H2+); fall back to opening the vSwitch default inbound
            "if(Get-Command New-NetFirewallHyperVRule){" +
            "if(-not (Get-NetFirewallHyperVRule -Name 'ClaudeUsageHooks')){" +
            $"New-NetFirewallHyperVRule -Name 'ClaudeUsageHooks' -DisplayName '{RuleName} (WSL)' -Direction Inbound -VMCreatorId '{WslVmCreatorId}' -Protocol TCP -LocalPorts {port} -Action Allow | Out-Null}}}}" +
            $"else{{Set-NetFirewallHyperVVMSetting -Name '{WslVmCreatorId}' -DefaultInboundAction Allow}}";

        try
        {
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
                UseShellExecute = true,   // required for Verb=runas
                Verb = "runas",           // triggers the UAC elevation prompt
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(20000);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            // Win32Exception (1223) when the user cancels the UAC prompt, etc.
            System.Diagnostics.Debug.WriteLine($"FirewallHelper: {ex.Message}");
            return false;
        }
    }

    /// <summary>The equivalent command for users who'd rather run it themselves.</summary>
    public static string GetManualCommand(int port)
        => $"New-NetFirewallRule -DisplayName '{RuleName}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {port}";
}
