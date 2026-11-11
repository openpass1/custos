using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Custos.Gui;

/// <summary>Interface texts from Resources\Strings.resx (English) and Strings.ru.resx (Russian).</summary>
internal static class Strings
{
    private static readonly ResourceManager Resources = new("Custos.Gui.Resources.Strings", typeof(Strings).Assembly);

    public static string AppTitle => Get();

    public static string MenuOpen => Get();

    public static string MenuClose => Get();

    public static string MenuCloseHint => Get();

    public static string TrayTooltip => Get();

    public static string StateConnecting => Get();

    public static string StateRunning => Get();

    public static string StateStopping => Get();

    public static string StateUnavailable => Get();

    public static string ProblemNotRunning => Get();

    public static string ProblemAccessDenied => Get();

    public static string ProblemVersionMismatch => Get();

    public static string ProblemConnectionLost => Get();

    public static string ServerNotConnected => Get();

    public static string RightsAdmin => Get();

    public static string RightsUser => Get();

    public static string UserWithRights => Get();

    public static string RunningSince => Get();

    public static string LabelService => Get();

    public static string LabelServiceVersion => Get();

    public static string LabelInterfaceVersion => Get();

    public static string LabelUser => Get();

    public static string LabelSession => Get();

    public static string LabelProblem => Get();

    public static string Format(string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, format, args);

    private static string Get([CallerMemberName] string name = "") =>
        Resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;
}
