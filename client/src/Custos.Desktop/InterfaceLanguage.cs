using System.Globalization;
using Custos.Core;
using Custos.Gui;

namespace Custos.Desktop;

internal static class InterfaceLanguage
{
    private static readonly string?[] Languages = [null, "en", "ru"];

    public static ChoiceItem Choice(string? language) => new(DesktopStrings.InterfaceLanguage,
        [Strings.LanguageAuto, "English", "Русский"], Math.Max(0, Array.IndexOf(Languages, language)), 340);

    public static string? Selected(ChoiceItem choice) => Languages[Math.Max(0, choice.Selected)];

    public static void Apply(string? language)
    {
        // Avalonia callbacks can run under an earlier culture captured by the dispatcher. Use an explicit choice
        // for the interface resources and Core descriptions in this process; the service has its own culture.
        Strings.Culture = Messages.Culture = language is "en" or "ru" ? CultureInfo.GetCultureInfo(language) : null;
    }
}
