using System.Security.Cryptography;

namespace Tiny11Builder.Core;

public sealed record WslAsset(string FileName, string Url, string Sha256, long Size);

/// <summary>
/// Real Linux for the image: the Windows Subsystem for Linux MSI from Microsoft's GitHub releases and the Alpine Linux
/// minirootfs. Both are pinned by SHA-256 and copied into Windows\Tiny11Linux\wsl; Alpine is imported at first sign-in.
/// </summary>
public static class WslPackage
{
    public const string ComponentId = "wsl";
    public const string Distribution = "Alpine";
    public const string Directory = "wsl";
    public const string MsiName = "wsl.msi";
    public const string RootfsName = "alpine.tar.gz";
    public const string SetupScript = "wsl-setup.cmd";
    public const string WslVersion = "3.0.3";
    public const string AlpineVersion = "3.24.3";

    public static readonly string[] Features = ["Microsoft-Windows-Subsystem-Linux", "VirtualMachinePlatform"];

    internal static readonly string[] AllowedHosts =
        ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com", "dl-cdn.alpinelinux.org"];

    /// <summary>The MSI and the rootfs for an image architecture, in that order.</summary>
    public static IReadOnlyList<WslAsset> Assets(string architecture) => architecture switch
    {
        "x64" =>
        [
            new(MsiName, $"https://github.com/microsoft/WSL/releases/download/{WslVersion}/wsl.{WslVersion}.0.x64.msi",
                "AE977286974255BD26BF925F00F40F5692C6B2084155CBC0563C09AEA091F211", 367575040),
            new(RootfsName, $"https://dl-cdn.alpinelinux.org/alpine/v3.24/releases/x86_64/alpine-minirootfs-{AlpineVersion}-x86_64.tar.gz",
                "F8E1CAFBA9B1B283B5D30D06F9885C8D2BC161FFE9083ADE8E2E157813A53407", 3704080)
        ],
        "arm64" =>
        [
            new(MsiName, $"https://github.com/microsoft/WSL/releases/download/{WslVersion}/wsl.{WslVersion}.0.arm64.msi",
                "2937C7CE840BB35EC6E97F052189BDFFF27F8C3463BFDD231DF9A958212787A3", 341086208),
            new(RootfsName, $"https://dl-cdn.alpinelinux.org/alpine/v3.24/releases/aarch64/alpine-minirootfs-{AlpineVersion}-aarch64.tar.gz",
                "4D3F80AFAEBD447C5591A63A73BDE380A149FE8B2D85488CAFABB2FF97551F50", 4029999)
        ],
        _ => throw new NotSupportedException($"WSL with Alpine is not available for {architecture} images.")
    };

    /// <summary>Checks that a downloaded package directory has both files before the image is serviced.</summary>
    public static void RequireFiles(string packageDirectory)
    {
        if (string.IsNullOrEmpty(packageDirectory))
            throw new FileNotFoundException("WSL with Alpine was selected, but its files have not been downloaded.");
        foreach (var name in new[] { MsiName, RootfsName })
            if (!File.Exists(Path.Combine(packageDirectory, name)))
                throw new FileNotFoundException($"The WSL package is missing {name}. Download it again.", Path.Combine(packageDirectory, name));
    }

    /// <summary>Copies the package into Windows\Tiny11Linux\wsl in a mounted image.</summary>
    public static void Install(string mountDirectory, string packageDirectory, Action<string> log)
    {
        RequireFiles(packageDirectory);
        var windows = Path.Combine(PathSafety.Full(mountDirectory), "Windows");
        if (!System.IO.Directory.Exists(windows)) throw new DirectoryNotFoundException("The mounted image has no Windows folder.");
        PathSafety.RejectReparsePoints(windows);
        var target = Path.Combine(windows, LinuxCommandCatalog.DirectoryName, Directory);
        System.IO.Directory.CreateDirectory(target);
        PathSafety.RejectReparsePoints(target);
        foreach (var name in new[] { MsiName, RootfsName })
        {
            var source = Path.Combine(packageDirectory, name);
            var destination = Path.Combine(target, name);
            File.Copy(source, destination, overwrite: true);
            if (new FileInfo(destination).Length != new FileInfo(source).Length) throw new IOException($"WSL package copy failed: {name}");
        }
        log($@"WSL {WslVersion} and Alpine Linux {AlpineVersion} copied to {LinuxCommandCatalog.PathEntry}\{Directory}.");
    }
}

/// <summary>Downloads the pinned WSL package into a cache. Nothing is kept or used unless its SHA-256 matches.</summary>
public sealed class WslAcquirer(HttpClient client, Func<string, IReadOnlyList<WslAsset>>? assets = null)
{
    public async Task<string> EnsureAsync(string cacheDirectory, string architecture, IProgress<string>? progress, CancellationToken token)
    {
        var directory = Path.Combine(PathSafety.Full(cacheDirectory), architecture);
        System.IO.Directory.CreateDirectory(directory);
        PathSafety.RejectReparsePoints(directory);
        foreach (var asset in (assets ?? WslPackage.Assets)(architecture))
        {
            var destination = Path.Combine(directory, asset.FileName);
            if (File.Exists(destination))
            {
                if (await HashAsync(destination, token).ConfigureAwait(false) == asset.Sha256) continue;
                File.Delete(destination);
            }
            progress?.Report($"Downloading {asset.FileName} ({asset.Size / (1024 * 1024)} MB).");
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                await DownloadAsync(asset, temporary, token).ConfigureAwait(false);
                if (await HashAsync(temporary, token).ConfigureAwait(false) != asset.Sha256)
                    throw new InvalidDataException($"The checksum of {asset.FileName} does not match. It has not been saved or used.");
                File.Move(temporary, destination, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        progress?.Report("WSL and Alpine Linux ready.");
        return directory;
    }

    private async Task DownloadAsync(WslAsset asset, string path, CancellationToken token)
    {
        var uri = new Uri(asset.Url);
        for (var redirect = 0; redirect < 5; redirect++)
        {
            RequireApproved(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var next = response.Headers.Location ?? throw new IOException($"{uri.Host} returned an empty redirect.");
                uri = next.IsAbsoluteUri ? next : new(uri, next);
                continue;
            }
            if (response.RequestMessage?.RequestUri is { } final) RequireApproved(final);
            if (!response.IsSuccessStatusCode) throw new IOException($"{uri.Host} answered HTTP {(int)response.StatusCode} for {asset.FileName}.");
            await using var content = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            int count;
            while ((count = await content.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (file.Length + count > asset.Size) throw new InvalidDataException($"{asset.FileName} exceeds its expected size.");
                await file.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            return;
        }
        throw new IOException($"Too many redirects for {asset.FileName}.");
    }

    private static void RequireApproved(Uri uri)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || !WslPackage.AllowedHosts.Contains(uri.IdnHost))
            throw new InvalidDataException("The WSL download left its approved hosts.");
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
}
