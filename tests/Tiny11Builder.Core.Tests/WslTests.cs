using System.Net;
using System.Security.Cryptography;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class WslTests
{
    [Fact]
    public void Wsl_commands_forward_to_alpine_and_ship_the_setup_script()
    {
        var files = LinuxCommandCatalog.Files([WslPackage.ComponentId]);
        Assert.Contains("linux.cmd", files.Keys);
        Assert.Contains(WslPackage.SetupScript, files.Keys);
        Assert.DoesNotContain(LinuxCommandCatalog.ScriptName, files.Keys);
        Assert.Equal("@call \"%~dp0linux.cmd\" sed %*\r\n", files["sed.cmd"]);
        Assert.Contains("-d Alpine -- %*", files["linux.cmd"]);
        Assert.Contains("call \"%~dp0wsl-setup.cmd\"", files["linux.cmd"]);
        var setup = files[WslPackage.SetupScript];
        Assert.Contains("--import Alpine", setup);
        Assert.Contains("wsl\\alpine.tar.gz", setup.Replace("%PKG%", "wsl\\"));
        Assert.Contains("msiexec.exe", setup);
        Assert.Contains("--version 1", setup);
        Assert.DoesNotContain("\n", setup.Replace("\r\n", ""));
    }

    [Fact]
    public void Wsl_with_other_groups_keeps_the_powershell_script()
    {
        var files = LinuxCommandCatalog.Files([WslPackage.ComponentId, "files"]);
        Assert.Contains(LinuxCommandCatalog.ScriptName, files.Keys);
        Assert.Contains("ls.cmd", files.Keys);
        Assert.Contains("vi.cmd", files.Keys);
    }

    [Fact]
    public void Wsl_is_refused_for_core()
    {
        new BuildRecipe { LinuxComponents = [WslPackage.ComponentId] }.ValidateOptions();
        Assert.Throws<InvalidDataException>(() =>
            new BuildRecipe { Flavor = BuildFlavor.Core, CoreAcknowledged = true, LinuxComponents = [WslPackage.ComponentId] }.ValidateOptions());
    }

    [Theory]
    [InlineData("x64", "x64.msi", "x86_64")]
    [InlineData("arm64", "arm64.msi", "aarch64")]
    public void Assets_are_pinned_per_architecture(string architecture, string msi, string alpine)
    {
        var assets = WslPackage.Assets(architecture);
        Assert.Equal([WslPackage.MsiName, WslPackage.RootfsName], assets.Select(a => a.FileName));
        Assert.EndsWith(msi, assets[0].Url);
        Assert.Contains(alpine, assets[1].Url);
        Assert.All(assets, a =>
        {
            Assert.Matches("^[0-9A-F]{64}$", a.Sha256);
            Assert.StartsWith("https://", a.Url);
            Assert.Contains(new Uri(a.Url).Host, WslPackage.AllowedHosts);
        });
        Assert.Throws<NotSupportedException>(() => WslPackage.Assets("x86"));
    }

    [Fact]
    public void Unattend_runs_setup_at_first_sign_in_only_when_selected()
    {
        var image = new WindowsImage(1, "Windows 11 Pro", "", "x64", new(10, 0, 26100, 1), "Professional", "Client", 1);
        var with = UnattendBuilder.Create(image, new() { LinuxComponents = [WslPackage.ComponentId] });
        Assert.Contains("<FirstLogonCommands>", with);
        Assert.Contains(@"%SystemRoot%\Tiny11Linux\wsl-setup.cmd", with);
        Assert.True(with.IndexOf("FirstLogonCommands", StringComparison.Ordinal) < with.IndexOf("<OOBE>", StringComparison.Ordinal));
        Assert.DoesNotContain("FirstLogonCommands", UnattendBuilder.Create(image, new()));
    }

    [Fact]
    public async Task Acquirer_keeps_only_verified_files_and_reuses_the_cache()
    {
        var root = Path.Combine(Path.GetTempPath(), "tiny11-wsl-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] msi = [1, 2, 3], rootfs = [4, 5];
            WslAsset[] assets =
            [
                new(WslPackage.MsiName, "https://github.com/microsoft/WSL/releases/download/x/wsl.msi", Hash(msi), msi.Length),
                new(WslPackage.RootfsName, "https://dl-cdn.alpinelinux.org/alpine/rootfs.tar.gz", Hash(rootfs), rootfs.Length)
            ];
            var handler = new Handler(uri => uri.Host == "github.com" ? Redirect("https://release-assets.githubusercontent.com/wsl.msi") :
                uri.AbsolutePath.EndsWith(".msi") ? Ok(msi) : Ok(rootfs));
            using var client = new HttpClient(handler);
            var directory = await new WslAcquirer(client, _ => assets).EnsureAsync(root, "x64", null, CancellationToken.None);
            Assert.Equal(msi, File.ReadAllBytes(Path.Combine(directory, WslPackage.MsiName)));
            Assert.Equal(rootfs, File.ReadAllBytes(Path.Combine(directory, WslPackage.RootfsName)));
            WslPackage.RequireFiles(directory);
            var requests = handler.Requests;
            await new WslAcquirer(client, _ => assets).EnsureAsync(root, "x64", null, CancellationToken.None);
            Assert.Equal(requests, handler.Requests);

            var bad = new[] { assets[0] with { Sha256 = new string('0', 64) } };
            File.Delete(Path.Combine(directory, WslPackage.MsiName));
            await Assert.ThrowsAsync<InvalidDataException>(() => new WslAcquirer(client, _ => bad).EnsureAsync(root, "x64", null, CancellationToken.None));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Acquirer_refuses_unapproved_redirects()
    {
        var root = Path.Combine(Path.GetTempPath(), "tiny11-wsl-" + Guid.NewGuid().ToString("N"));
        try
        {
            WslAsset[] assets = [new(WslPackage.MsiName, "https://github.com/microsoft/WSL/x.msi", new string('0', 64), 1)];
            using var client = new HttpClient(new Handler(_ => Redirect("https://untrusted.example/x.msi")));
            await Assert.ThrowsAsync<InvalidDataException>(() => new WslAcquirer(client, _ => assets).EnsureAsync(root, "x64", null, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "x64")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Build_refuses_wsl_without_downloaded_files()
    {
        using var fixture = new BuildFixture();
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            fixture.Engine.BuildAsync(fixture.Recipe with { LinuxComponents = [WslPackage.ComponentId] }, null, CancellationToken.None));
        Assert.Empty(fixture.Runner.Calls);
    }

    [Fact]
    public async Task Build_enables_wsl_features_and_copies_alpine_before_commit()
    {
        using var fixture = new BuildFixture();
        var package = Path.Combine(fixture.Root, "wsl-package");
        Directory.CreateDirectory(package);
        File.WriteAllBytes(Path.Combine(package, WslPackage.MsiName), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(package, WslPackage.RootfsName), [4, 5]);
        string[] copied = [];
        fixture.Runner.OnCommand = args =>
        {
            var mount = args.FirstOrDefault(a => a.StartsWith("/MountDir:", StringComparison.Ordinal))?["/MountDir:".Length..];
            if (mount is null) return;
            var target = Path.Combine(mount, "Windows", LinuxCommandCatalog.DirectoryName);
            if (args.Contains("/Mount-Image") && !args.Any(a => a.EndsWith("boot.wim", StringComparison.OrdinalIgnoreCase)))
                Directory.CreateDirectory(Path.Combine(mount, "Windows"));
            if (args.Contains("/Unmount-Image") && args.Contains("/Commit") && Directory.Exists(target))
            {
                copied = Directory.GetFiles(target, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(target, f)).Order(StringComparer.Ordinal).ToArray();
                Directory.Delete(target, true);
            }
        };
        var result = await fixture.Engine.BuildAsync(fixture.Recipe with
        {
            LinuxComponents = [WslPackage.ComponentId], WslPackagePath = package
        }, null, CancellationToken.None);
        foreach (var feature in WslPackage.Features)
            Assert.Contains(fixture.Runner.Calls, c => c.Contains("/Enable-Feature") && c.Contains("/FeatureName:" + feature));
        Assert.Contains(Path.Combine(WslPackage.Directory, WslPackage.MsiName), copied);
        Assert.Contains(Path.Combine(WslPackage.Directory, WslPackage.RootfsName), copied);
        Assert.Contains("linux.cmd", copied);
        Assert.Contains(WslPackage.SetupScript, copied);
        Assert.Equal("Completed", JobScenarios.StageStates(result.WorkDirectory)["WSL"]);
    }

    [Fact]
    public void The_wsl_stage_is_servicing_before_settings()
    {
        Assert.Equal(2, BuildProgressText.Phase("WSL"));
        Assert.True(BuildProgressText.Range(".NET 3.5").End <= BuildProgressText.Range("WSL").Start);
        Assert.True(BuildProgressText.Range("WSL").End <= BuildProgressText.Range("Windows settings").Start);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new(location);
        return response;
    }

    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; private set; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests = [.. Requests, request.RequestUri!.AbsoluteUri];
            var response = respond(request.RequestUri);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
