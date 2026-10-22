using System.Runtime.Versioning;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class LinuxCommandTests
{
    [Fact]
    public void Only_selected_commands_are_written_with_the_shared_script()
    {
        var files = LinuxCommandCatalog.Files(["sudo", "text"]);
        Assert.Equal(["grep.cmd", "head.cmd", "sudo.cmd", "tail.cmd", LinuxCommandCatalog.ScriptName, "wc.cmd"], files.Keys.ToArray());
        Assert.All(files.Values, content => Assert.DoesNotContain("\n", content.Replace("\r\n", "")));
        Assert.All(files.Values, content => Assert.EndsWith("\r\n", content));
        Assert.Contains("tiny11linux.ps1\" grep %*", files["grep.cmd"]);
        Assert.Contains("TINY11_SUDO=%*", files["sudo.cmd"]);
        Assert.Contains("'sudo' { Invoke-Sudo }", files[LinuxCommandCatalog.ScriptName]);
        Assert.Empty(LinuxCommandCatalog.Files([]));
    }

    [Fact]
    public void Every_script_backed_command_has_a_handler_and_names_are_unique()
    {
        var all = LinuxCommandCatalog.Components.SelectMany(c => c.Commands).ToArray();
        Assert.Equal(all.Length, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var files = LinuxCommandCatalog.Files(LinuxCommandCatalog.Components.Select(c => c.Id));
        var script = files[LinuxCommandCatalog.ScriptName];
        foreach (var command in all.Where(command => files[command + ".cmd"].Contains(LinuxCommandCatalog.ScriptName)))
            Assert.Contains($"'{command}' {{", script);
    }

    [Theory]
    [InlineData(@"%SystemRoot%\system32;%SystemRoot%", @"%SystemRoot%\system32;%SystemRoot%;%SystemRoot%\Tiny11Linux")]
    [InlineData(@"C:\Tools;%SYSTEMROOT%\TINY11LINUX\;", @"C:\Tools;%SYSTEMROOT%\TINY11LINUX\")]
    [InlineData("", @"%SystemRoot%\Tiny11Linux")]
    public void The_command_directory_is_appended_to_path_once(string path, string expected) =>
        Assert.Equal(expected, LinuxCommandCatalog.AddToPath(path));

    [Theory]
    [InlineData("bash")]
    [InlineData("sudo", "sudo")]
    public void Unknown_or_duplicate_choices_are_refused(params string[] ids) =>
        Assert.Throws<InvalidDataException>(() => new BuildRecipe { LinuxComponents = ids }.ValidateOptions());

    [Fact]
    public void Recipes_keep_the_selected_commands()
    {
        var recipe = new BuildRecipe { LinuxComponents = ["sudo", "files"] };
        recipe.ValidateOptions();
        var restored = System.Text.Json.JsonSerializer.Deserialize<BuildRecipe>(
            System.Text.Json.JsonSerializer.Serialize(recipe, JsonDefaults.Options), JsonDefaults.Options)!;
        Assert.Equal(recipe.LinuxComponents, restored.LinuxComponents);
        Assert.Empty(new BuildRecipe().LinuxComponents);
    }

    [Fact]
    public async Task Build_writes_the_commands_into_the_offline_image_before_commit()
    {
        using var fixture = new BuildFixture();
        string[] written = [];
        fixture.Runner.OnCommand = args =>
        {
            var mount = args.FirstOrDefault(a => a.StartsWith("/MountDir:", StringComparison.Ordinal))?["/MountDir:".Length..];
            if (mount is null) return;
            var target = Path.Combine(mount, "Windows", LinuxCommandCatalog.DirectoryName);
            if (args.Contains("/Mount-Image") && !args.Any(a => a.EndsWith("boot.wim", StringComparison.OrdinalIgnoreCase)))
                Directory.CreateDirectory(Path.Combine(mount, "Windows"));
            if (args.Contains("/Unmount-Image") && args.Contains("/Commit") && Directory.Exists(target))
            {
                written = Directory.GetFiles(target).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
                Directory.Delete(target, true);
            }
        };
        var result = await fixture.Engine.BuildAsync(fixture.Recipe with { LinuxComponents = ["sudo"] }, null, CancellationToken.None);
        Assert.Equal(["sudo.cmd", LinuxCommandCatalog.ScriptName], written);
        Assert.Equal(["sudo"], fixture.Registry.LastRecipe!.LinuxComponents);
        Assert.Equal("Completed", JobScenarios.StageStates(result.WorkDirectory)["Linux commands"]);
        Assert.Contains("Linux commands written", File.ReadAllText(Path.Combine(result.WorkDirectory, "build.log")));
    }

    [Fact]
    public void The_stage_belongs_to_servicing_between_settings_and_payloads()
    {
        Assert.Equal(2, BuildProgressText.Phase("Linux commands"));
        Assert.True(BuildProgressText.Range("Windows settings").End <= BuildProgressText.Range("Linux commands").Start);
        Assert.True(BuildProgressText.Range("Linux commands").End <= BuildProgressText.Range("Advanced payloads").Start);
    }
}
