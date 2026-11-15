using Custos.Core;
using Custos.Core.Ipc;

namespace Custos.Gui.Tests;

/// <summary>New versions of the program on GitHub as the windows show them (computers without a monitoring site).</summary>
public sealed class ProgramUpdateTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_main_window_shows_a_new_version_while_there_is_one(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = Samples.Monitor();
        using var form = new MainForm(monitor);
        form.Show();
        Sta.Pump();

        string banner = Strings.Format(Strings.UpdateBanner, "99.0.0", CustosInfo.Version);
        Assert.False(Sta.Shows(form, banner));

        var snapshot = Samples.Snapshot(2);
        snapshot.ProgramUpdate = new ProgramUpdateInfo
        {
            Checking = true,
            CheckedAt = DateTimeOffset.Now,
            Latest = "99.0.0",
            Available = true,
            PageUrl = $"https://github.com/{CustosInfo.GitHubRepository}/releases/tag/client-v99.0.0",
        };
        monitor.SimulateSnapshot(snapshot);
        Sta.Pump();

        Assert.True(Sta.Shows(form, banner));
        Assert.Contains(Sta.Find<LinkLabel>(form), l => l.Visible && l.Text == Strings.UpdateWhatsNew);

        // Not connected, so not an administrator: the button is there, but only an administrator may press it.
        var install = Assert.Single(Sta.Find<Button>(form), b => b.Text == Strings.UpdateInstall);
        Assert.True(install.Visible);
        Assert.False(install.Enabled);

        // Installed (or the setting turned off): the line goes away.
        var after = Samples.Snapshot(3);
        after.ProgramUpdate = new ProgramUpdateInfo { Checking = true, CheckedAt = DateTimeOffset.Now, Latest = "99.0.0" };
        monitor.SimulateSnapshot(after);
        Sta.Pump();
        Assert.False(Sta.Shows(form, banner));
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_settings_show_the_daily_check_and_what_it_found(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = Samples.Monitor();
        var snapshot = Samples.Snapshot(2);
        snapshot.ProgramUpdate = new ProgramUpdateInfo
        {
            Checking = true,
            CheckedAt = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero),
            Latest = "99.0.0",
            Available = true,
        };
        monitor.SimulateSnapshot(snapshot);
        using (var settings = new SettingsForm(monitor))
        {
            settings.Show();
            Sta.Pump();

            var check = Assert.Single(Sta.Find<CheckBox>(settings), c => c.Text == Strings.CheckUpdates);
            Assert.True(check.Checked);
            Assert.False(check.Enabled);
            Assert.True(Sta.ShowsPart(settings, Strings.Format(Strings.UpdatesAvailable, "99.0.0")));
            Assert.False(Assert.Single(Sta.Find<Button>(settings), b => b.Text == Strings.UpdatesCheckNow).Enabled);
        }

        // Turned off: the last check is not shown as if it still went on.
        var off = Samples.Snapshot(3);
        off.Settings.CheckUpdates = false;
        off.ProgramUpdate = new ProgramUpdateInfo { CheckedAt = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero), Latest = "99.0.0" };
        monitor.SimulateSnapshot(off);
        using (var settings = new SettingsForm(monitor))
        {
            settings.Show();
            Sta.Pump();

            Assert.False(Assert.Single(Sta.Find<CheckBox>(settings), c => c.Text == Strings.CheckUpdates).Checked);
            Assert.True(Sta.Shows(settings, Strings.UpdatesOff));
            Assert.False(Assert.Single(Sta.Find<Button>(settings), b => b.Text == Strings.UpdatesCheckNow).Enabled);
        }

        // Connected to a monitoring site: the site decides, GitHub is not asked.
        var managed = Samples.Snapshot(4);
        managed.Settings.CheckUpdates = false;
        managed.ProgramUpdate = new ProgramUpdateInfo { ManagedBySite = true };
        monitor.SimulateSnapshot(managed);
        using (var settings = new SettingsForm(monitor))
        {
            settings.Show();
            Sta.Pump();

            Assert.False(Assert.Single(Sta.Find<CheckBox>(settings), c => c.Text == Strings.CheckUpdates).Checked);
            Assert.True(Sta.Shows(settings, Strings.UpdatesManagedBySite));
        }
    });
}
