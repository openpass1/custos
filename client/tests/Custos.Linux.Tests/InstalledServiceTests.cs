using System.Diagnostics;
using Custos.Core;

namespace Custos.Linux.Tests;

/// <summary>
/// The program as install.sh puts it on a computer: the systemd service custos runs, answers on /run/custos.sock with the
/// version just installed, and the command custos talks to it. Run only with CUSTOS_INSTALLED_SERVICE=1 (CI, after
/// install.sh): then the other tests of the collection use the installed service too. CUSTOS_EXPECTED_VERSION is the
/// version that must answer.
/// </summary>
[Collection(ServiceGroup.Name)]
public sealed class InstalledServiceTests(ServiceFixture service)
{
    private static readonly string? Expected = Environment.GetEnvironmentVariable("CUSTOS_EXPECTED_VERSION");

    [Fact]
    public void The_installed_service_answers_with_the_installed_version()
    {
        Assert.SkipUnless(TestPlace.Installed, "Only after install.sh (CUSTOS_INSTALLED_SERVICE=1).");
        Assert.Equal(Expected ?? CustosInfo.Version, service.Hello.ServiceVersion);
        Assert.True(File.Exists("/opt/custos/custos"));
        Assert.Equal("active", Run("/usr/bin/systemctl", "is-active", "custos").Output.Trim());
        Assert.Equal("enabled", Run("/usr/bin/systemctl", "is-enabled", "custos").Output.Trim());
    }

    [Fact]
    public void The_command_custos_talks_to_the_service()
    {
        Assert.SkipUnless(TestPlace.Installed, "Only after install.sh (CUSTOS_INSTALLED_SERVICE=1).");

        var version = Run("/usr/local/bin/custos", "version");
        var status = Run("/usr/local/bin/custos", "status");
        var help = Run("/usr/local/bin/custos", "help");

        Assert.Equal(0, version.Code);
        Assert.Contains(Expected ?? CustosInfo.Version, version.Output, StringComparison.Ordinal);
        Assert.True(status.Code == 0, status.Output);
        Assert.Contains("CUSTOS " + (Expected ?? CustosInfo.Version), status.Output, StringComparison.Ordinal);
        Assert.Equal(0, help.Code);
        Assert.Contains("custos snapshots", help.Output, StringComparison.Ordinal);
    }

    /// <summary>Exit code and output (both streams) of a program, in English.</summary>
    private static (int Code, string Output) Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["LANG"] = "C.UTF-8";
        start.Environment.Remove("LC_ALL");
        start.Environment.Remove("LC_MESSAGES");
        start.Environment.Remove("CUSTOS_SOCKET");
        start.Environment.Remove("CUSTOS_DATA_DIR");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), program + " did not end");
        process.WaitForExit();
        return (process.ExitCode, output.Result + errors.Result);
    }
}
