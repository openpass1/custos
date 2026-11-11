namespace Custos.Core;

/// <summary>
/// Event IDs written to the Windows Application log under the source "CUSTOS" (section 6.10 of the client spec).
/// Administrators build monitoring rules on these numbers, so they never change meaning.
/// </summary>
public static class CustosEventIds
{
    public const int ServiceStarted = 1000;
    public const int ServiceStopped = 1001;
    public const int ServerConnectionLost = 1100;
    public const int ServerConnectionRestored = 1101;
    public const int TaskStarted = 2000;
    public const int TaskSucceeded = 2001;
    public const int TaskCompletedWithWarnings = 2002;
    public const int TaskFailed = 2003;
    public const int TaskAborted = 2004;
    public const int CatchUpRun = 2005;
    public const int VssFailed = 3000;
    public const int DestinationFull = 3001;

    /// <summary>7z.dll is missing, not 64-bit or broken: tasks with 7z fail until it is replaced.</summary>
    public const int SevenZipUnavailable = 3002;
    public const int UpdateInstalled = 4000;
    public const int UpdateRejected = 4001;

    /// <summary>Any other error of the service. The details are in the service log file.</summary>
    public const int UnexpectedError = 9000;

    private static readonly HashSet<int> Published =
    [
        ServiceStarted, ServiceStopped, ServerConnectionLost, ServerConnectionRestored,
        TaskStarted, TaskSucceeded, TaskCompletedWithWarnings, TaskFailed, TaskAborted, CatchUpRun,
        VssFailed, DestinationFull, SevenZipUnavailable, UpdateInstalled, UpdateRejected, UnexpectedError,
    ];

    /// <summary>True for the IDs listed in the spec, which always go to the Windows event log.</summary>
    public static bool IsPublished(int eventId) => Published.Contains(eventId);
}
