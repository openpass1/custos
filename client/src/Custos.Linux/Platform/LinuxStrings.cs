using System.Resources;
using Custos.Core;

namespace Custos.Service.Platform;

/// <summary>
/// Texts of CUSTOS for Linux only (Resources/LinuxStrings.tsv, English and Russian), in the language of the program
/// (<see cref="Messages.Culture"/>). The properties are generated into LinuxStrings.g.cs by tools/make-strings.py.
/// </summary>
internal static partial class LinuxStrings
{
    private static readonly ResourceManager Resources = new("Custos.Linux.Resources.LinuxStrings", typeof(LinuxStrings).Assembly);

    public static string Format(string format, params object?[] args) => string.Format(Messages.FormatCulture, format, args);

    private static string Get(string name) => Resources.GetString(name, Messages.UiCulture) ?? name;
}
