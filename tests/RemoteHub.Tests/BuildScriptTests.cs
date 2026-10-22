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
        // Both at once: Windows PowerShell writes its module-loading progress to stderr, and a full stderr pipe would
        // stall it while this waited for the end of stdout.
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000), "Windows PowerShell didn't end within 2 minutes");
        return (p.ExitCode, stdout.Result + stderr.Result);
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

    private static (int Exit, string Output) Script(string name, string arguments) =>
        PowerShell($"& '{Path.Combine(Build, name)}' {arguments}; exit $LASTEXITCODE");

    [Fact]
    public void The_build_refuses_a_package_below_its_minimum_fixed_version()
    {
        var dir = Directory.CreateTempSubdirectory("rh-depgate-").FullName;
        try
        {
            var project = Path.Combine(dir, "src", "App", "App.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            void Write(string version) => File.WriteAllText(project,
                $"""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="FluentFTP" Version="{version}" /><PackageReference Include="Other" Version="1.0.0" /></ItemGroup></Project>""");

            Write("55.0.0");
            var (exit, output) = Script("Test-DependencyVersions.ps1", $"-Root '{dir}'");
            Assert.Equal(1, exit);
            Assert.Contains("FluentFTP 55.0.0 is below 55.1.0", output);

            Write("55.1.0-beta.1"); // a pre-release of the fixed version isn't the fix
            Assert.Equal(1, Script("Test-DependencyVersions.ps1", $"-Root '{dir}'").Exit);

            Write("55.1.0");
            (exit, output) = Script("Test-DependencyVersions.ps1", $"-Root '{dir}'");
            Assert.True(exit == 0, output);

            // the repository itself passes (the CI step)
            (exit, output) = Script("Test-DependencyVersions.ps1", "");
            Assert.True(exit == 0, output);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Workflows_keep_github_values_out_of_scripts_and_pin_every_action()
    {
        var dir = Directory.CreateTempSubdirectory("rh-workflows-").FullName;
        const string pinned = "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1";
        try
        {
            File.WriteAllText(Path.Combine(dir, "good.yml"), $$$"""
                on: push
                jobs:
                  a:
                    runs-on: ubuntu-latest
                    steps:
                      - uses: {{{pinned}}}
                      - name: Version
                        env:
                          TAG: ${{ github.ref_name }}
                        run: |
                          echo "$TAG ${{ steps.v.outputs.version }}"
                      - run: echo done
                """);
            var (exit, output) = Script("Test-Workflows.ps1", $"-Path '{dir}'");
            Assert.True(exit == 0, output);

            File.WriteAllText(Path.Combine(dir, "bad.yml"), """
                on: pull_request_target
                jobs:
                  a:
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v4
                      - uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68
                      - run: echo "${{ github.event.pull_request.title }}"
                      - name: Block
                        run: |
                          echo start
                          echo "${{ inputs.name }}"
                """);
            (exit, output) = Script("Test-Workflows.ps1", $"-Path '{dir}'");
            Assert.Equal(1, exit);
            Assert.Contains("bad.yml:6: actions/checkout@v4 is not pinned to a full commit SHA", output);
            Assert.Contains("bad.yml:7: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 has no version comment", output);
            Assert.Contains("bad.yml:8: ${{ github.* }} in a run: script", output);
            Assert.Contains("bad.yml:12: ${{ inputs.* }} in a run: script", output);
            Assert.Contains("bad.yml: pull_request_target with actions/checkout", output);
            Assert.DoesNotContain("good.yml", output);

            // the repository's own workflows pass (the CI step)
            (exit, output) = Script("Test-Workflows.ps1", "");
            Assert.True(exit == 0, output);
        }
        finally { Directory.Delete(dir, true); }
    }
}
