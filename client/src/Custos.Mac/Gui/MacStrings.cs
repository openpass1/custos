using System.Globalization;
using System.Resources;

namespace Custos.Mac.Gui;

/// <summary>
/// Texts of the macOS window only (Resources/MacStrings.tsv, English and Russian); the others are those of Custos.exe
/// (<see cref="Custos.Gui.Strings"/>). The properties are generated into MacStrings.g.cs by tools/make-strings.py.
/// </summary>
internal static partial class MacStrings
{
    private static readonly ResourceManager Resources = new("Custos.Mac.Resources.MacStrings", typeof(MacStrings).Assembly);

    private static string Get(string name) => Resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;
}
