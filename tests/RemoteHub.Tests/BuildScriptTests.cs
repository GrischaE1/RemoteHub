using System.Diagnostics;

namespace RemoteHub.Tests;

/// <summary>Pieces of the build scripts in build\ that can run without signing anything.</summary>
public class BuildScriptTests
{
    private static string Build => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\build"));

    /// <summary>Java 11 or later (single-file programs): CI's runners have it under JAVA_HOME_*_X64.</summary>
    private static string? Java()
    {
        foreach (var name in new[] { "JAVA_HOME_21_X64", "JAVA_HOME_17_X64", "JAVA_HOME_11_X64", "JAVA_HOME" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } home && File.Exists(Path.Combine(home, "bin", "java.exe")))
                return Path.Combine(home, "bin", "java.exe");
        return null;
    }

    private static (int Exit, string Output) PowerShell(string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // the PowerShell 7 SDK in this process changes PSModulePath; Windows PowerShell needs its own
        start.Environment.Remove("PSModulePath");
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    [SkippableFact]
    public void The_eSigner_credentials_reach_CodeSignTool_unchanged_through_an_argument_file()
    {
        var java = Java();
        Skip.If(java is null, "no Java 11 or later (JAVA_HOME_*_X64) on this machine");
        var dir = Directory.CreateTempSubdirectory("rh-argfile-").FullName;
        try
        {
            // stands for CodeSignTool: prints each argument on a line of its own
            var program = Path.Combine(dir, "Echo.java");
            File.WriteAllText(program, "public class Echo { public static void main(String[] a) { for (String s : a) System.out.println(\"[\" + s + \"]\"); } }");
            string[] args = [@"-password=p\a ss""w#rd'$x", "-totp_secret=JBSWY3DPEHPK3PXP", @"-input_file_path=D:\a\Remote Hub\RemoteHub.exe", "#comment-like", "-override=true"];
            var file = Path.Combine(dir, "test.args");
            var list = string.Join(", ", new[] { program }.Concat(args).Select(a => "'" + a.Replace("'", "''") + "'"));
            var (exit, output) = PowerShell($". '{Path.Combine(Build, "JavaArgFile.ps1")}'; Write-JavaArgFile -Path '{file}' -Arguments {list}");
            Assert.True(exit == 0, output);

            var start = new ProcessStartInfo(java!, "@" + file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(start)!;
            var printed = p.StandardOutput.ReadToEnd();
            var errors = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, errors);
            Assert.Equal(args.Select(a => $"[{a}]"), printed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Assert.DoesNotContain("JBSWY3DPEHPK3PXP", start.Arguments); // the command line carries only the file's name
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_argument_the_launcher_would_garble_is_refused()
    {
        var file = Path.Combine(Path.GetTempPath(), "rh-argfile-" + Guid.NewGuid().ToString("N") + ".args");
        try
        {
            var (exit, output) = PowerShell($"$ErrorActionPreference = 'Stop'; . '{Path.Combine(Build, "JavaArgFile.ps1")}'; Write-JavaArgFile -Path '{file}' -Arguments '-password=Grüße'");
            Assert.NotEqual(0, exit);
            Assert.Contains("printable ASCII", output);
            Assert.False(File.Exists(file));
        }
        finally { File.Delete(file); }
    }
}
