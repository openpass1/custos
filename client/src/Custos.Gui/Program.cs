namespace Custos.Gui;

internal static class Program
{
    // Local\ objects exist once per Windows session, so every session gets its own interface (section 3.1).
    private const string InstanceMutexName = @"Local\CUSTOS.Gui";
    private const string ActivateEventName = @"Local\CUSTOS.Gui.Activate";

    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(initiallyOwned: false, InstanceMutexName, out bool firstInSession);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        if (!firstInSession)
        {
            // Already running in this session: ask that copy to show its window and quit.
            activate.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var tray = new TrayContext(activate);
        Application.Run(tray);
    }
}
