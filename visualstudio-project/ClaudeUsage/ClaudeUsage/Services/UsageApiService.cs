using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

public class UsageApiService
{
    private static readonly HttpClient _httpClient = new();
    private const string UsageApiUrl = "https://api.anthropic.com/api/oauth/usage";
    private const int MaxRetries = 5;

    private static string? _cachedClaudeCodeVersion;

    private static string GetClaudeCodeVersion()
    {
        if (_cachedClaudeCodeVersion != null)
            return _cachedClaudeCodeVersion;

        try
        {
            // Try native Windows first via cmd.exe so PATHEXT resolves the npm
            // `claude.cmd` shim (a bare Process.Start("claude") uses CreateProcess,
            // which ignores PATHEXT and silently misses .cmd installs). Only fall
            // back to WSL if it's actually installed — invoking wsl.exe on a system
            // without WSL triggers the Windows "install WSL" prompt (issue #4).
            var version = TryGetVersionFromProcess("cmd.exe", "/c claude --version");
            if (version == null && IsWslInstalled())
            {
                version = TryGetVersionFromProcess("wsl", "claude --version");
            }

            _cachedClaudeCodeVersion = version ?? "2.1.100";
            System.Diagnostics.Debug.WriteLine($"Claude Code version detected: {_cachedClaudeCodeVersion}");
        }
        catch
        {
            _cachedClaudeCodeVersion = "2.1.100";
            System.Diagnostics.Debug.WriteLine("Claude Code version detection failed, using fallback 2.1.100");
        }

        return _cachedClaudeCodeVersion;
    }

    /// <summary>
    /// Returns true only when at least one WSL distribution is registered.
    /// Reads the Lxss registry key (a passive check) instead of executing
    /// wsl.exe, which on a WSL-less machine pops the OS install prompt (issue #4).
    /// </summary>
    private static bool IsWslInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Lxss");
            return key != null && key.GetSubKeyNames().Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetVersionFromProcess(string fileName, string arguments)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit(5000);

            // Extract the first dotted-version substring anywhere in the output.
            // Handles "1.2.3", "claude-code 1.2.3", and the current Claude Code
            // format "2.1.143 (Claude Code)" (the old last-token parser picked
            // "Code)" and gave up, falling back to a throttled User-Agent).
            var match = System.Text.RegularExpressions.Regex.Match(output, @"\d+\.\d+(?:\.\d+)*");
            if (match.Success)
                return match.Value;

            System.Diagnostics.Debug.WriteLine(
                $"Version detection: no version in output from '{fileName} {arguments}'. stdout='{output}' stderr='{error}'");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Version detection failed for '{fileName}': {ex.Message}");
        }

        return null;
    }

    public static async Task<UsageData?> GetUsageAsync()
    {
        var token = await CredentialService.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var claudeVersion = GetClaudeCodeVersion();

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, UsageApiUrl);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Add("User-Agent", $"claude-code/{claudeVersion}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

                System.Diagnostics.Debug.WriteLine($"Request: User-Agent=claude-code/{claudeVersion}, Token={token?[..Math.Min(20, token.Length)]}...");

                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine($"API Response: {json}");
                    return JsonSerializer.Deserialize<UsageData>(json);
                }

                var statusCode = (int)response.StatusCode;
                var errorBody = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine(
                    $"API Error (attempt {attempt + 1}/{MaxRetries + 1}): {response.StatusCode} - {errorBody}");

                // Retry on 429 (rate limit) or 5xx (server error)
                if ((statusCode == 429 || statusCode >= 500) && attempt < MaxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1s, 2s, 4s, 8s, 16s
                    System.Diagnostics.Debug.WriteLine($"Retrying in {delay.TotalSeconds}s...");
                    await Task.Delay(delay);
                    continue;
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Exception in GetUsageAsync (attempt {attempt + 1}/{MaxRetries + 1}): {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");

                // Only retry transient transport failures. A JsonException (or any
                // non-network error) means the request SUCCEEDED but we mis-parsed —
                // retrying just hammers the endpoint into a self-inflicted 429.
                var transient = ex is System.Net.Http.HttpRequestException or TaskCanceledException;
                if (transient && attempt < MaxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    await Task.Delay(delay);
                    continue;
                }

                return null;
            }
        }

        return null;
    }
}
