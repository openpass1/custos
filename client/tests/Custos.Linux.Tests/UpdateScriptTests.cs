using System.Diagnostics;
using System.Text.Json;
using Custos.Core.Monitor;
using Custos.Service.Agent;

namespace Custos.Linux.Tests;

/// <summary>
/// The script that installs an update (A.10) on a program folder of its own, with systemctl and sleep stood in for by
/// small scripts that write down what they were asked: the folders are swapped with the service stopped, the unit of
/// the new version is put in place, and the old version comes back when the new one does not stay up.
/// </summary>
public sealed class UpdateScriptTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_update_swaps_the_program_and_puts_the_old_one_back_when_the_new_one_does_not_stay_up(bool staysUp)
    {
        var place = new Place(staysUp);
        place.Package();

        var result = place.Run();

        Assert.Equal(staysUp, result.Ok);
        Assert.Equal(!staysUp, result.RolledBack);
        Assert.Equal("1.0.0", result.FromVersion);
        Assert.Equal("1.0.1", result.ToVersion);
        string wanted = staysUp ? "new" : "old";
        Assert.Contains(wanted, File.ReadAllText(Path.Combine(place.Program, "custos")), StringComparison.Ordinal);
        Assert.Equal(wanted + " unit", File.ReadAllText(Path.Combine(place.Units, "custos.service")).Trim());
        Assert.False(Directory.Exists(place.Program + ".old"));
        Assert.False(Directory.Exists(place.Program + ".new"));
        Assert.True((File.GetUnixFileMode(Path.Combine(place.Program, "custos")) & UnixFileMode.UserExecute) != 0);
        var calls = place.Calls();
        Assert.Equal("stop custos", calls[0]);
        Assert.Contains("start custos", calls);
        Assert.Contains("daemon-reload", calls);
        Assert.Contains("is-active --quiet custos", calls);
    }

    [Fact]
    public void A_package_that_cannot_be_unpacked_leaves_the_program_and_the_service_alone()
    {
        var place = new Place(staysUp: true);
        File.WriteAllText(place.Archive, "not an archive");

        var result = place.Run();

        Assert.False(result.Ok);
        Assert.False(result.RolledBack);
        Assert.Contains("unpacked", result.Message, StringComparison.Ordinal);
        Assert.Contains("old", File.ReadAllText(Path.Combine(place.Program, "custos")), StringComparison.Ordinal);
        Assert.False(Directory.Exists(place.Program + ".new"));
        Assert.Empty(place.Calls());
    }

    /// <summary>A program folder with version "old" of the program, the stand-ins and the files of the script.</summary>
    private sealed class Place
    {
        private readonly string _root = TestPlace.NewFolder("update");

        public Place(bool staysUp)
        {
            Program = Path.Combine(_root, "opt", "custos");
            Units = Path.Combine(_root, "units");
            Archive = Path.Combine(_root, "custos-linux-x64.tar.gz");
            Directory.CreateDirectory(Units);
            WriteProgram(Program, "old");
            File.WriteAllText(Path.Combine(Units, "custos.service"), "old unit\n");

            string bin = Path.Combine(_root, "bin");
            Directory.CreateDirectory(bin);
            Executable(Path.Combine(bin, "systemctl"), $"""
                #!/bin/sh
                echo "$*" >> '{CallsFile}'
                case "$1" in is-active) exit {(staysUp ? 0 : 3)} ;; esac
                exit 0
                """);
            Executable(Path.Combine(bin, "sleep"), "#!/bin/sh\nexit 0\n");
            Executable(Path.Combine(_root, "update.sh"), AgentService.UpdateScript.ReplaceLineEndings("\n"));
        }

        public string Program { get; }

        public string Units { get; }

        public string Archive { get; }

        private string CallsFile => Path.Combine(_root, "systemctl.txt");

        /// <summary>The archive of version "new", as build-linux.sh makes it: the program and its unit at the top.</summary>
        public void Package()
        {
            string folder = Path.Combine(_root, "package");
            WriteProgram(folder, "new");
            Disks.Run("tar", "-czf", Archive, "-C", folder, ".");
        }

        public UpdaterResult Run()
        {
            string result = Path.Combine(_root, "update-result.json");
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            foreach (string argument in new[] { Path.Combine(_root, "update.sh"), Archive, result, "1.0.0", "1.0.1", "custos", Program, Path.Combine(_root, "update.log"), Units })
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["PATH"] = Path.Combine(_root, "bin") + ":/usr/sbin:/usr/bin:/sbin:/bin";
            using var process = Process.Start(start)!;
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "update.sh did not end");
            Assert.Equal(0, process.ExitCode);
            return JsonSerializer.Deserialize(File.ReadAllText(result), MonitorJsonContext.Default.UpdaterResult)!;
        }

        public List<string> Calls() => File.Exists(CallsFile) ? [.. File.ReadAllLines(CallsFile)] : [];

        private static void WriteProgram(string folder, string version)
        {
            Directory.CreateDirectory(folder);
            Executable(Path.Combine(folder, "custos"), $"#!/bin/sh\necho {version}\n");
            File.WriteAllText(Path.Combine(folder, "custos.service"), version + " unit\n");
        }

        private static void Executable(string path, string text)
        {
            File.WriteAllText(path, text);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
    }
}
