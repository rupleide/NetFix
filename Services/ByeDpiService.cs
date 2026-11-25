using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using NetFix.Models;

namespace NetFix.Services;

public sealed record ByeDpiStartResult(bool Ok, string Message, string Endpoint)
{
    public static ByeDpiStartResult Success(string endpoint, string message = "") => new(true, message, endpoint);
    public static ByeDpiStartResult Fail(string message) => new(false, message, "");
}

public sealed record ByeDpiProbeResult(bool Ok, string Host, string Detail);

public sealed record ByeDpiPickResult(ByeDpiPreset? Preset, string Message);

public static class ByeDpiService
{
    public const int DefaultPort = 1080;
    private const string Repo = "hufrea/byedpi";
    private const string ProxyRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object ProxyLock = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly string[] ProbeHosts = ["discord.com", "www.youtube.com"];

    private static Process? _process;

    private static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetFix", "byedpi");

    private static string BackupPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetFix", "byedpi-proxy-backup.json");

    public static int NormalizePort(int port) => port is >= 1 and <= 65535 ? port : DefaultPort;

    public static bool IsRunning
    {
        get
        {
            var processes = Process.GetProcessesByName("ciadpi");
            try
            {
                return processes.Any(process =>
                {
                    try { return !process.HasExited; }
                    catch { return false; }
                });
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
    }

    public static async Task<ByeDpiStartResult> StartAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            StopCore();

            var binary = await EnsureBinaryAsync(cancellationToken);
            if (binary.Exe is null)
                return ByeDpiStartResult.Fail(binary.Error);

            await AntiCheatBypassService.StopZapretAndWinDivertAsync();

            var port = NormalizePort(settings.ByeDpiPort);
            var preset = ByeDpiPresets.Get(settings.ByeDpiPresetId);
            var endpoint = $"127.0.0.1:{port}";

            var startInfo = new ProcessStartInfo
            {
                FileName = binary.Exe,
                Arguments = $"--ip 127.0.0.1 --port {port} {preset.Arguments}",
                WorkingDirectory = Path.GetDirectoryName(binary.Exe) ?? InstallDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                return ByeDpiStartResult.Fail($"Не удалось запустить ciadpi: {ex.Message}");
            }

            if (process is null)
                return ByeDpiStartResult.Fail("Не удалось запустить ciadpi");

            _process = process;
            await Task.Delay(700, cancellationToken);

            if (process.HasExited)
            {
                var code = process.ExitCode;
                _process = null;
                process.Dispose();
                return ByeDpiStartResult.Fail($"ciadpi завершился сразу после запуска (код {code})");
            }

            if (!await WaitForPortAsync(port, cancellationToken))
            {
                StopCore();
                return ByeDpiStartResult.Fail($"SOCKS5 не открылся на {endpoint}");
            }

            if (!settings.ByeDpiSystemProxy)
                return ByeDpiStartResult.Success(endpoint);

            if (!ApplySystemProxy(port))
                return ByeDpiStartResult.Success(endpoint, "ByeDPI запущен, но системный прокси записать не удалось");

            return ByeDpiStartResult.Success(endpoint);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ByeDpiStartResult.Fail(ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Stop()
    {
        if (!Gate.Wait(TimeSpan.FromSeconds(30)))
            return;

        try
        {
            StopCore();
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<ByeDpiProbeResult> ProbeAsync(int port, CancellationToken cancellationToken = default)
    {
        var proxyPort = NormalizePort(port);
        var failures = new List<string>();

        foreach (var host in ProbeHosts)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                await ProbeHostAsync(host, 443, proxyPort, timeout.Token);
                return new ByeDpiProbeResult(true, host, "");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failures.Add($"{host}: {ShortError(ex)}");
            }
        }

        return new ByeDpiProbeResult(false, "", string.Join("; ", failures));
    }

    public static async Task<ByeDpiPickResult> FindWorkingPresetAsync(
        AppSettings settings,
        Action<string>? onPreset = null,
        CancellationToken cancellationToken = default)
    {
        var originalId = settings.ByeDpiPresetId;
        var wasRunning = IsRunning;

        foreach (var preset in ByeDpiPresets.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onPreset?.Invoke(preset.Title);
            settings.ByeDpiPresetId = preset.Id;

            var start = await StartAsync(settings, cancellationToken);
            if (!start.Ok)
            {
                settings.ByeDpiPresetId = originalId;
                if (wasRunning)
                    await StartAsync(settings, cancellationToken);
                return new ByeDpiPickResult(null, start.Message);
            }

            var probe = await ProbeAsync(settings.ByeDpiPort, cancellationToken);
            if (probe.Ok)
                return new ByeDpiPickResult(preset, $"Открылся {probe.Host}");
        }

        settings.ByeDpiPresetId = originalId;
        if (wasRunning)
            await StartAsync(settings, cancellationToken);
        else
            Stop();

        return new ByeDpiPickResult(null, "Ни один пресет не открыл discord.com и youtube.com");
    }

    public static void RecoverOrphanedProxy()
    {
        if (IsRunning || !File.Exists(BackupPath))
            return;

        RestoreSystemProxy();
    }

    public static bool ApplySystemProxy(int port)
    {
        lock (ProxyLock)
        {
            try
            {
                var proxyPort = NormalizePort(port);
                using var key = Registry.CurrentUser.CreateSubKey(ProxyRegistryPath, writable: true);
                if (key is null)
                    return false;

                if (!File.Exists(BackupPath))
                    SaveProxyBackup(key);

                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", $"socks=127.0.0.1:{proxyPort}", RegistryValueKind.String);

                var currentOverride = key.GetValue("ProxyOverride") as string;
                if (string.IsNullOrWhiteSpace(currentOverride))
                    key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);

                NotifyProxyChanged();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void RestoreSystemProxy()
    {
        lock (ProxyLock)
        {
            try
            {
                if (!File.Exists(BackupPath))
                    return;

                var backup = JsonSerializer.Deserialize<ProxyBackup>(File.ReadAllText(BackupPath), JsonOpts);
                if (backup is null)
                    return;

                using var key = Registry.CurrentUser.CreateSubKey(ProxyRegistryPath, writable: true);
                if (key is null)
                    return;

                key.SetValue("ProxyEnable", backup.Enable, RegistryValueKind.DWord);
                WriteOrDelete(key, "ProxyServer", backup.HadServer, backup.Server);
                WriteOrDelete(key, "ProxyOverride", backup.HadOverride, backup.Override);
                File.Delete(BackupPath);
                NotifyProxyChanged();
            }
            catch { }
        }
    }

    private static void StopCore()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { }
        }

        _process?.Dispose();
        _process = null;

        foreach (var process in Process.GetProcessesByName("ciadpi"))
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            finally
            {
                process.Dispose();
            }
        }

        RestoreSystemProxy();
    }

    private static async Task<(string? Exe, string Error)> EnsureBinaryAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(InstallDir);
        var exePath = Path.Combine(InstallDir, "ciadpi.exe");
        var versionPath = Path.Combine(InstallDir, "version.txt");

        WindowsRelease? release = null;
        try
        {
            release = await GetWindowsReleaseAsync(cancellationToken);
        }
        catch { }

        if (release is null)
        {
            return File.Exists(exePath)
                ? (exePath, "")
                : (null, "Не удалось скачать ByeDPI, локальной копии тоже нет");
        }

        var installed = File.Exists(versionPath) ? (await File.ReadAllTextAsync(versionPath, cancellationToken)).Trim() : "";
        if (File.Exists(exePath) && installed == release.Version)
            return (exePath, "");

        var zipPath = Path.Combine(Path.GetTempPath(), $"netfix-byedpi-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadAsync(release.Url, zipPath, cancellationToken);
            ExtractCiadpi(zipPath, exePath);
            await File.WriteAllTextAsync(versionPath, release.Version, cancellationToken);
            return (exePath, "");
        }
        catch (Exception ex)
        {
            if (File.Exists(exePath))
                return (exePath, "");

            return (null, $"Не удалось подготовить ciadpi: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); }
            catch { }
        }
    }

    private static async Task<WindowsRelease?> GetWindowsReleaseAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetFix/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var version = doc.RootElement.GetProperty("tag_name").GetString() ?? "";

        if (!doc.RootElement.TryGetProperty("assets", out var assets))
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith("x86_64-w64.zip", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = asset.GetProperty("browser_download_url").GetString();
            if (string.IsNullOrWhiteSpace(url))
                return null;

            return new WindowsRelease(version, url);
        }

        return null;
    }

    private static async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetFix/1.0");
        var bytes = await http.GetByteArrayAsync(url, cancellationToken);
        await File.WriteAllBytesAsync(destination, bytes, cancellationToken);
    }

    private static void ExtractCiadpi(string zipPath, string exePath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(item =>
            item.Name.Equals("ciadpi.exe", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            throw new InvalidDataException("В архиве ByeDPI нет ciadpi.exe");

        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        entry.ExtractToFile(exePath, overwrite: true);
    }

    private static async Task<bool> WaitForPortAsync(int port, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(400));
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (SocketException)
            {
            }

            await Task.Delay(200, cancellationToken);
        }

        return false;
    }

    private static async Task ProbeHostAsync(string host, int port, int proxyPort, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, cancellationToken);
        var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken);
        var greeting = new byte[2];
        await ReadExactAsync(stream, greeting, cancellationToken);
        if (greeting[0] != 0x05 || greeting[1] != 0x00)
            throw new IOException("прокси отказал в подключении");

        var hostBytes = Encoding.ASCII.GetBytes(host);
        var request = new byte[7 + hostBytes.Length];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = 0x03;
        request[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(request, 5);
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)(port & 0xff);
        await stream.WriteAsync(request, cancellationToken);

        var reply = new byte[4];
        await ReadExactAsync(stream, reply, cancellationToken);
        if (reply[1] != 0x00)
            throw new IOException($"SOCKS {reply[1]}");

        var addressLength = reply[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => await ReadAddressLengthAsync(stream, cancellationToken),
            _ => 0,
        };
        if (addressLength > 0)
            await ReadExactAsync(stream, new byte[addressLength + 2], cancellationToken);

        using var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, cancellationToken);
    }

    private static async Task<int> ReadAddressLengthAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var length = new byte[1];
        await ReadExactAsync(stream, length, cancellationToken);
        return length[0];
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
                throw new IOException("соединение закрыто");
            offset += read;
        }
    }

    private static void SaveProxyBackup(RegistryKey key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        var server = key.GetValue("ProxyServer") as string;
        var proxyOverride = key.GetValue("ProxyOverride") as string;
        var backup = new ProxyBackup
        {
            Enable = key.GetValue("ProxyEnable") is int enable ? enable : 0,
            Server = server,
            Override = proxyOverride,
            HadServer = server is not null,
            HadOverride = proxyOverride is not null,
        };
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup, JsonOpts));
    }

    private static void WriteOrDelete(RegistryKey key, string name, bool hadValue, string? value)
    {
        if (hadValue)
            key.SetValue(name, value ?? "", RegistryValueKind.String);
        else
            key.DeleteValue(name, throwOnMissingValue: false);
    }

    private static void NotifyProxyChanged()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }

    private static string ShortError(Exception exception)
    {
        var message = exception.Message.ReplaceLineEndings(" ").Trim();
        return message.Length <= 120 ? message : message[..120];
    }

    [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private sealed record WindowsRelease(string Version, string Url);

    private sealed class ProxyBackup
    {
        public int Enable { get; set; }
        public string? Server { get; set; }
        public string? Override { get; set; }
        public bool HadServer { get; set; }
        public bool HadOverride { get; set; }
    }
}
