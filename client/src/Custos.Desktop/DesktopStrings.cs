using System.Resources;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// Texts of the new windows only, from Resources\DesktopStrings.tsv (English and Russian); the properties are generated
/// by tools/make-strings.py into DesktopStrings.g.cs. The texts the new windows share with Custos.exe are in
/// <see cref="Gui.Strings"/>.
/// </summary>
internal static partial class DesktopStrings
{
    private static readonly ResourceManager Resources = new("Custos.Desktop.Resources.DesktopStrings", typeof(DesktopStrings).Assembly);

    private static string Get(string name) => Resources.GetString(name, Strings.UiCulture) ?? name;
}
