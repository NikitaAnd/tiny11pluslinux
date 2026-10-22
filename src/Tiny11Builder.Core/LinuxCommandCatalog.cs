using System.Reflection;
using System.Text;

namespace Tiny11Builder.Core;

public sealed record LinuxComponent(string Id, string Title, string Description, string[] Commands);

/// <summary>
/// Linux-style commands for Command Prompt. Each command is a small .cmd file in <see cref="DirectoryName"/>
/// under the Windows folder, which is added to the system PATH. Batch commands run in the calling
/// Command Prompt; the rest are handled by one bundled Windows PowerShell script.
/// </summary>
public static class LinuxCommandCatalog
{
    public const string DirectoryName = "Tiny11Linux";
    public const string PathEntry = @"%SystemRoot%\" + DirectoryName;
    public const string ScriptName = "tiny11linux.ps1";
    // Registry value used by Windows 11 24H2+ "Sudo for Windows": 3 runs commands inline in the same window.
    public const string SudoKey = @"Microsoft\Windows\CurrentVersion\Sudo";
    public const string SudoValueName = "Enabled";
    public const int SudoInline = 3;

    public static IReadOnlyList<LinuxComponent> Components { get; } =
    [
        new("sudo", "sudo", "Enable Windows' built-in sudo inline in the same window on builds that include it. Older builds get a sudo command that opens an elevated Command Prompt.",
            ["sudo"]),
        new("files", "File commands", "ls, cat, cp, mv, rm, touch, ln and pwd. Wildcards such as *.txt work.",
            ["ls", "cat", "cp", "mv", "rm", "touch", "ln", "pwd"]),
        new("text", "Text commands", "grep, head, tail and wc for files and pipes, for example: dir | grep -i log.",
            ["grep", "head", "tail", "wc"]),
        new("system", "System commands", "ps, kill, killall, uname, df, free, uptime, ifconfig, which, clear, env, export, history and sleep.",
            ["ps", "kill", "killall", "uname", "df", "free", "uptime", "ifconfig", "which", "clear", "env", "export", "history", "sleep"]),
        new(WslPackage.ComponentId, "Real Linux (WSL2 + Alpine)", "Windows Subsystem for Linux with Alpine Linux. linux opens a Linux shell; linux COMMAND runs any Linux program; apk installs more.",
            ["linux", "apk", "sh", "sed", "awk", "vi", "gzip", "gunzip", "xargs", "cut", "tr", "uniq", "diff", "less", "du", "wget"])
    ];

    private const string LinuxRegistered =
        "reg query \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Lxss\" /s /f " + WslPackage.Distribution + " /d /e >nul 2>&1";
    private const string FindWsl = "set \"WSL=%ProgramFiles%\\WSL\\wsl.exe\"\r\nif not exist \"%WSL%\" set \"WSL=wsl.exe\"";

    // Commands that need no PowerShell; they run directly in the calling Command Prompt.
    private static readonly IReadOnlyDictionary<string, string> BatchCommands = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["pwd"] = "@echo %CD%",
        ["clear"] = "@cls",
        ["which"] = "@where %*",
        ["env"] = "@set",
        // No setlocal: like the shell built-in, export changes the calling Command Prompt's environment.
        ["export"] = "@set %*",
        ["history"] = "@doskey /history",
        ["ifconfig"] = "@if /i \"%~1\"==\"-a\" (ipconfig /all) else (ipconfig %*)",
        ["cat"] = string.Join("\r\n",
            "@echo off",
            "setlocal",
            "if \"%~1\"==\"\" (findstr \"^\" & exit /b)",
            "set \"rc=0\"",
            "for %%F in (%*) do (",
            "  if exist \"%%~F\\\" (echo cat: %%~F: Is a directory 1>&2& set \"rc=1\"",
            "  ) else if exist \"%%~F\" (type \"%%~F\"",
            "  ) else (echo cat: %%~F: No such file or directory 1>&2& set \"rc=1\")",
            ")",
            "exit /b %rc%"),
        ["sudo"] = string.Join("\r\n",
            "@echo off",
            "setlocal",
            "if \"%~1\"==\"\" (echo usage: sudo COMMAND [ARGUMENTS] 1>&2& exit /b 1)",
            "set \"TINY11_SUDO=%*\"",
            Script("sudo", forward: false)),
        // Runs in Alpine under WSL. The first use imports Alpine for the current user if first sign-in did not.
        ["linux"] = string.Join("\r\n",
            "@echo off",
            "setlocal",
            LinuxRegistered + " || call \"%~dp0" + WslPackage.SetupScript + "\" || exit /b 1",
            FindWsl,
            "if \"%~1\"==\"\" (\"%WSL%\" -d " + WslPackage.Distribution + " & exit /b)",
            "\"%WSL%\" -d " + WslPackage.Distribution + " -- %*")
    };

    /// <summary>Installs WSL from the bundled MSI when needed and imports Alpine for the current user.</summary>
    public static string WslSetupScript() => string.Join("\r\n",
        "@echo off",
        "setlocal",
        "set \"PKG=%~dp0" + WslPackage.Directory + "\\\"",
        "if exist \"%ProgramFiles%\\WSL\\wsl.exe\" goto import",
        "echo Installing Windows Subsystem for Linux " + WslPackage.WslVersion + "...",
        "powershell.exe -NoLogo -NoProfile -Command \"$p = Start-Process msiexec.exe -Verb RunAs -Wait -PassThru -ArgumentList ('/i \\\"' + $env:PKG + '" + WslPackage.MsiName + "\\\" /qn /norestart'); exit $p.ExitCode\"",
        "set \"rc=%errorlevel%\"",
        "if not \"%rc%\"==\"0\" if not \"%rc%\"==\"3010\" (echo WSL could not be installed, error %rc%. 1>&2& exit /b 1)",
        ":import",
        FindWsl,
        LinuxRegistered + " && exit /b 0",
        "echo Setting up Alpine Linux " + WslPackage.AlpineVersion + "...",
        "set \"TARGET=%LOCALAPPDATA%\\Tiny11Linux\\" + WslPackage.Distribution + "\"",
        "\"%WSL%\" --import " + WslPackage.Distribution + " \"%TARGET%\" \"%PKG%" + WslPackage.RootfsName + "\" --version 2 || " +
            "\"%WSL%\" --import " + WslPackage.Distribution + " \"%TARGET%\" \"%PKG%" + WslPackage.RootfsName + "\" --version 1 || " +
            "(echo Alpine Linux could not be set up. Turn on virtualization in the firmware or VM settings, then run " + WslPackage.SetupScript + " again. 1>&2& exit /b 1)",
        "\"%WSL%\" --set-default " + WslPackage.Distribution + " >nul",
        "echo Alpine Linux is ready. Type linux to open it.");

    private static readonly HashSet<string> WslCommands =
        new(Components.Single(c => c.Id == WslPackage.ComponentId).Commands.Where(c => c != "linux"), StringComparer.Ordinal);

    public static IEnumerable<string> CommandsFor(IEnumerable<string> ids) =>
        Components.Where(component => ids.Contains(component.Id, StringComparer.Ordinal)).SelectMany(component => component.Commands);

    public static bool IsKnown(string id) => Components.Any(component => component.Id == id);

    /// <summary>The content of every file to write into the image's command directory, with CRLF line endings.</summary>
    public static IReadOnlyDictionary<string, string> Files(IEnumerable<string> ids)
    {
        var files = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var selected = ids.ToArray();
        var usesScript = false;
        foreach (var command in CommandsFor(selected))
        {
            if (BatchCommands.TryGetValue(command, out var body)) files[command + ".cmd"] = Crlf(body);
            else if (selected.Contains(WslPackage.ComponentId, StringComparer.Ordinal) && WslCommands.Contains(command))
                files[command + ".cmd"] = Crlf($"@call \"%~dp0linux.cmd\" {command} %*");
            else { files[command + ".cmd"] = Crlf(Script(command, forward: true)); usesScript = true; }
        }
        if (usesScript || files.ContainsKey("sudo.cmd")) files[ScriptName] = Crlf(DispatcherScript());
        if (selected.Contains(WslPackage.ComponentId, StringComparer.Ordinal)) files[WslPackage.SetupScript] = Crlf(WslSetupScript());
        return files;
    }

    public static string DispatcherScript()
    {
        using var stream = typeof(LinuxCommandCatalog).Assembly.GetManifestResourceStream("Tiny11Builder.Core.LinuxCommands." + ScriptName)
            ?? throw new InvalidOperationException("The Linux command script is missing from the build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Appends the command directory to a system PATH value, keeping it unexpanded and avoiding duplicates.</summary>
    public static string AddToPath(string? path)
    {
        var entries = (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!entries.Any(entry => entry.TrimEnd('\\').Equals(PathEntry, StringComparison.OrdinalIgnoreCase)))
            entries.Add(PathEntry);
        return string.Join(';', entries);
    }

    /// <summary>Writes the selected commands to Windows\Tiny11Linux in a mounted image.</summary>
    public static void Install(string mountDirectory, IReadOnlyCollection<string> ids, Action<string> log)
    {
        var files = Files(ids);
        if (files.Count == 0) return;
        var windows = Path.Combine(PathSafety.Full(mountDirectory), "Windows");
        if (!Directory.Exists(windows)) throw new DirectoryNotFoundException("The mounted image has no Windows folder.");
        var target = Path.Combine(windows, DirectoryName);
        PathSafety.RejectReparsePoints(windows);
        if (Directory.Exists(target)) PathSafety.RejectReparsePoints(target);
        Directory.CreateDirectory(target);
        foreach (var (name, content) in files)
        {
            var file = Path.Combine(target, name);
            File.WriteAllText(file, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.ReadAllText(file) != content) throw new IOException($"Linux command verification failed: {name}");
        }
        log($"Linux commands written to {PathEntry}: {string.Join(", ", files.Keys.Where(name => name.EndsWith(".cmd", StringComparison.Ordinal)).Select(Path.GetFileNameWithoutExtension))}");
    }

    private static string Script(string command, bool forward) =>
        $"@powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"%~dp0{ScriptName}\" {command}{(forward ? " %*" : "")}";

    private static string Crlf(string text) => text.ReplaceLineEndings("\r\n").TrimEnd() + "\r\n";
}
