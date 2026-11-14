using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Core.Agent;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>The program password, the rights and the look of the interface in the settings (section 4, stage 5).</summary>
public sealed class ProgramPasswordTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly ConfigStore _store;

    public ProgramPasswordTests()
    {
        _store = new ConfigStore(_temp.Combine("config"), new XorProtector());
        _store.Load();
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_hash_made_here_takes_its_password_only()
    {
        string hash = ProgramPassword.Hash("Office-2026");

        Assert.True(ConfigStore.IsPasswordHash(hash));
        Assert.StartsWith("pbkdf2-sha256$200000$", hash, StringComparison.Ordinal);
        Assert.True(ProgramPassword.Verify("Office-2026", hash));
        Assert.False(ProgramPassword.Verify("office-2026", hash));
        Assert.False(ProgramPassword.Verify(string.Empty, hash));

        // A new salt every time: the same password never gives the same hash.
        Assert.NotEqual(hash, ProgramPassword.Hash("Office-2026"));
    }

    [Fact]
    public void A_password_set_on_the_site_works_here()
    {
        // Made by the monitoring server (Crypto::programPasswordHash, PHP hash_pbkdf2) with a fixed salt of 16 bytes 0x01.
        const string fromSite = "pbkdf2-sha256$200000$AQEBAQEBAQEBAQEBAQEBAQ==$9KN+Lyxcp7Q9oms0H8j+a7hx528F5G3qDxztgsQP72E=";

        Assert.True(ProgramPassword.Verify("Пароль-1", fromSite));
        Assert.False(ProgramPassword.Verify("Пароль-2", fromSite));
        Assert.False(ProgramPassword.Verify("Пароль-1", "plain-password"));
    }

    [Fact]
    public void A_password_too_short_or_too_long_is_refused()
    {
        Assert.NotNull(ProgramPassword.Problem("abc"));
        Assert.Null(ProgramPassword.Problem("abcd"));
        Assert.NotNull(ProgramPassword.Problem(new string('x', ProgramPassword.MaxLength + 1)));
        Assert.False(ProgramPassword.Verify(new string('x', ProgramPassword.MaxLength + 1), ProgramPassword.Hash("abcd")));
    }

    [Fact]
    public void The_gate_opens_with_the_password_and_closes_after_half_an_hour_or_a_new_password()
    {
        string hash = ProgramPassword.Hash("first");
        var gate = new PasswordGate();

        // Without a program password nothing is closed.
        Assert.True(gate.IsOpen(null, Now));
        Assert.False(gate.IsOpen(hash, Now));

        gate.Open(hash, Now);
        Assert.True(gate.IsOpen(hash, Now.AddMinutes(29)));
        Assert.False(gate.IsOpen(hash, Now.AddMinutes(30)));

        // Every change counts the half hour anew.
        gate.Open(hash, Now.AddMinutes(20));
        Assert.True(gate.IsOpen(hash, Now.AddMinutes(49)));

        // Another password (set here or on the site) needs typing again.
        Assert.False(gate.IsOpen(ProgramPassword.Hash("second"), Now.AddMinutes(21)));

        // Closing the window closes the gate.
        gate.Close();
        Assert.False(gate.IsOpen(hash, Now.AddMinutes(21)));
    }

    [Fact]
    public void The_password_is_kept_as_a_hash_and_a_plain_settings_save_does_not_touch_it()
    {
        string hash = ProgramPassword.Hash("secret-1");
        _store.SetProgramPasswordHash(hash);

        var settings = _store.GetSettings();
        settings.ProgramPasswordHash = null;
        settings.LogRetentionDays = 30;
        _store.SaveSettings(settings);

        Assert.Equal(hash, _store.GetSettings().ProgramPasswordHash);
        Assert.Equal(30, _store.GetSettings().LogRetentionDays);
        string file = File.ReadAllText(_temp.Combine("config", "settings.json"));
        Assert.DoesNotContain("secret-1", file, StringComparison.Ordinal);

        // Read again from the file after a restart of the service.
        var again = new ConfigStore(_temp.Combine("config"), new XorProtector());
        again.Load();
        Assert.Equal(hash, again.GetSettings().ProgramPasswordHash);

        _store.SetProgramPasswordHash(null);
        Assert.Null(_store.GetSettings().ProgramPasswordHash);
        Assert.Throws<ArgumentException>(() => _store.SetProgramPasswordHash("plain-password"));
    }

    [Fact]
    public void Unknown_tray_modes_and_themes_fall_back_to_the_defaults()
    {
        var settings = _store.GetSettings();
        Assert.Equal(TrayModes.Full, settings.TrayMode);
        Assert.Equal(Themes.Auto, settings.Theme);

        settings.TrayMode = TrayModes.Hidden;
        settings.Theme = Themes.Dark;
        settings.AllowAllUsersEdit = true;
        _store.SaveSettings(settings);
        Assert.Equal(TrayModes.Hidden, _store.GetSettings().TrayMode);
        Assert.Equal(Themes.Dark, _store.GetSettings().Theme);
        Assert.True(_store.GetSettings().AllowAllUsersEdit);

        settings.TrayMode = "icon";
        settings.Theme = "purple";
        _store.SaveSettings(settings);
        Assert.Equal(TrayModes.Full, _store.GetSettings().TrayMode);
        Assert.Equal(Themes.Auto, _store.GetSettings().Theme);
    }

    [Fact]
    public void The_site_sets_the_rights_and_the_look_but_not_unknown_values()
    {
        var changes = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["tray_mode"] = "status",
            ["allow_all_users_edit"] = true,
            ["theme"] = "dark",
            ["program_password_hash"] = ProgramPassword.Hash("from-site"),
        }, TestJson.Default.JsonNode);

        _store.UpdateSettingsFromServer(changes);

        var settings = _store.GetSettings();
        Assert.Equal(TrayModes.Status, settings.TrayMode);
        Assert.Equal(Themes.Dark, settings.Theme);
        Assert.True(settings.AllowAllUsersEdit);
        Assert.True(ProgramPassword.Verify("from-site", settings.ProgramPasswordHash!));

        var badMode = JsonSerializer.SerializeToElement(new JsonObject { ["tray_mode"] = "icon" }, TestJson.Default.JsonNode);
        Assert.Throws<ConfigException>(() => _store.UpdateSettingsFromServer(badMode));
        var badTheme = JsonSerializer.SerializeToElement(new JsonObject { ["theme"] = "purple" }, TestJson.Default.JsonNode);
        Assert.Throws<ConfigException>(() => _store.UpdateSettingsFromServer(badTheme));
        Assert.Equal(TrayModes.Status, _store.GetSettings().TrayMode);
    }
}
