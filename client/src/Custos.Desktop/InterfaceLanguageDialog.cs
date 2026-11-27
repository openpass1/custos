using Custos.Gui;

namespace Custos.Desktop;

internal sealed class InterfaceLanguageDialog : FormDialog
{
    private static readonly string?[] Languages = [null, "en", "ru"];
    private readonly UiState _ui;
    private readonly ChoiceItem _language;

    public InterfaceLanguageDialog(UiState ui) : base(DesktopStrings.InterfaceLanguage, 520)
    {
        _ui = ui;
        _language = new ChoiceItem(DesktopStrings.InterfaceLanguage, [Strings.LanguageAuto, "English", "Русский"],
            Math.Max(0, Array.IndexOf(Languages, ui.InterfaceLanguage)), 340);
        Items.Add(_language);
        Items.Add(new NoteItem(DesktopStrings.InterfaceLanguageRestart));
    }

    protected override Task<bool> OkAsync()
    {
        _ui.InterfaceLanguage = Languages[Math.Max(0, _language.Selected)];
        _ui.Save();
        return Task.FromResult(true);
    }
}
