using System.IO;
using System.Net;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

public class HookServer
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly Action<HookEvent> _onEvent;

    private const int PortRangeStart = 19532;
    private const int PortRangeEnd = 19542;

    public int Port { get; private set; }
    public bool IsRunning => _listener?.IsListening == true;

    public bool RemoteEnabled { get; private set; }

    public HookServer(Action<HookEvent> onEvent)
    {
        _onEvent = onEvent;
    }

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        RemoteEnabled = Helpers.StartupHelper.GetHookSetting("RemoteEnabled") == "1";

        for (int port = PortRangeStart; port <= PortRangeEnd; port++)
        {
            // Try remote binding first if enabled, fall back to localhost
            if (RemoteEnabled && TryBind($"http://+:{port}/hooks/", port))
                break;
            if (TryBind($"http://localhost:{port}/hooks/", port))
                break;
        }

        if (_listener == null || !_listener.IsListening)
        {
            System.Diagnostics.Debug.WriteLine("HookServer: failed to bind any port");
            return Task.CompletedTask;
        }

        // Run accept loop on background thread
        Task.Run(() => AcceptLoop(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch (ObjectDisposedException)
            {
                break; // Listener was stopped
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HookServer accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;

            var requestPath = request.Url?.AbsolutePath ?? "";

            // Serve the self-hosted remote installer (Moshi-style `curl ... | sh`).
            if (request.HttpMethod == "GET" &&
                requestPath.EndsWith("/install.sh", StringComparison.OrdinalIgnoreCase))
            {
                // Reply on whatever host the client reached us on — by definition
                // routable from their side — so the installed relay points back here.
                var host = request.Url?.Host;
                if (string.IsNullOrEmpty(host) || host == "+" || host == "localhost")
                    host = DetectTailscaleIp() ?? DetectLanIp() ?? host ?? "localhost";

                var script = HookSetupService.GetInstallScript(host, Port);
                var scriptBytes = System.Text.Encoding.UTF8.GetBytes(script);
                response.StatusCode = 200;
                response.ContentType = "text/x-shellscript; charset=utf-8";
                response.ContentLength64 = scriptBytes.Length;
                await response.OutputStream.WriteAsync(scriptBytes);
                response.Close();
                return;
            }

            // Hook events are POST only
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                response.Close();
                return;
            }

            // Read body
            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                body = await reader.ReadToEndAsync();
            }

            System.Diagnostics.Debug.WriteLine($"HookServer received: {request.Url?.AbsolutePath} - {body[..Math.Min(200, body.Length)]}");

            // Parse event
            var path = request.Url?.AbsolutePath ?? "";
            var hookEvent = HookEvent.FromRequest(path, body);

            // Dispatch to WPF thread
            try
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _onEvent(hookEvent));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HookServer dispatch error: {ex.Message}");
            }

            // Always respond 200
            response.StatusCode = 200;
            response.ContentType = "application/json";
            var responseBytes = System.Text.Encoding.UTF8.GetBytes("{\"result\":\"ok\"}");
            response.ContentLength64 = responseBytes.Length;
            await response.OutputStream.WriteAsync(responseBytes);
            response.Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"HookServer request error: {ex.Message}");
            try { context.Response.Close(); } catch { }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }

        System.Diagnostics.Debug.WriteLine("HookServer stopped");
    }

    private bool TryBind(string prefix, int port)
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            Port = port;
            System.Diagnostics.Debug.WriteLine($"HookServer listening: {prefix}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"HookServer bind failed ({prefix}): {ex.Message}");
            _listener?.Close();
            _listener = null;
            return false;
        }
    }

    public static string? DetectTailscaleIp()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Select(addr => addr.Address.ToString())
                .FirstOrDefault(ip => ip.StartsWith("100."));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Best-guess LAN IPv4 of this machine for the install command. Skips loopback,
    /// APIPA, Tailscale (surfaced separately) and virtual adapters (WSL/Hyper-V/VPN),
    /// and prefers real private ranges so the address is reachable from a peer on the
    /// same network. Returns null if nothing suitable is found.
    /// </summary>
    public static string? DetectLanIp()
    {
        try
        {
            var candidates = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                    && ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    && !LooksVirtual(ni.Description) && !LooksVirtual(ni.Name))
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Where(ip => !ip.StartsWith("127.") && !ip.StartsWith("169.254.") && !ip.StartsWith("100."))
                .ToList();

            return candidates.FirstOrDefault(ip => ip.StartsWith("192.168."))
                ?? candidates.FirstOrDefault(ip => ip.StartsWith("10."))
                ?? candidates.FirstOrDefault(ip => ip.StartsWith("172."))
                ?? candidates.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksVirtual(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        name = name.ToLowerInvariant();
        return name.Contains("vethernet") || name.Contains("wsl") || name.Contains("hyper-v")
            || name.Contains("virtualbox") || name.Contains("vmware") || name.Contains("loopback")
            || name.Contains("docker") || name.Contains("tailscale") || name.Contains("vpn");
    }
}
