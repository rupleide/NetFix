#nullable enable

using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetFix.Services;

public class ReserveComponentMetadata
{
    public string Version { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public class ReserveManifest
{
    public ReserveComponentMetadata Zapret { get; set; } = new();
    public ReserveComponentMetadata TgWsProxy { get; set; } = new();
}

public static class ReserveComponentProvider
{
    private const string ManifestResourceName = "NetFix.Assets.Fallback.metadata.json";
    private const string ZapretResourceName = "NetFix.Assets.Fallback.zapret-fallback.zip";
    private const string TgWsProxyResourceName = "NetFix.Assets.Fallback.tgwsproxy-fallback.exe";

    private static ReserveManifest? _cachedManifest;

    public static ReserveManifest? GetManifest()
    {
        if (_cachedManifest is not null)
        {
            return _cachedManifest;
        }

        try
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using Stream? stream = assembly.GetManifestResourceStream(ManifestResourceName);
            if (stream is null)
            {
                return null;
            }

            using StreamReader reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            _cachedManifest = JsonSerializer.Deserialize<ReserveManifest>(json);
            return _cachedManifest;
        }
        catch
        {
            return null;
        }
    }

    public static Stream? GetZapretStream()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceStream(ZapretResourceName);
    }

    public static Stream? GetTgWsProxyStream()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceStream(TgWsProxyResourceName);
    }

    public static async Task<bool> VerifyFileHashAsync(string filePath, string expectedHash, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using SHA256 sha256 = SHA256.Create();
            using FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            byte[] hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken);
            string actualHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> VerifyStreamHashAsync(Stream stream, string expectedHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            using SHA256 sha256 = SHA256.Create();
            byte[] hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken);
            string actualHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
