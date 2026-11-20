using System.Runtime.InteropServices;

namespace Custos.Mac.Gui;

/// <summary>
/// The icon of CUSTOS in the Dock: there while the window is open, gone while only the menu bar icon is left (as the
/// window of Windows goes to the tray). Avalonia sets this only once at the start, so it is asked of AppKit directly:
/// <c>[NSApp setActivationPolicy:]</c>, regular (0) or accessory (1).
/// </summary>
internal static partial class MacDock
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const long Regular = 0;
    private const long Accessory = 1;

    public static void Show(bool visible)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            nint app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app == 0)
            {
                return;
            }

            _ = SendLong(app, sel_registerName("setActivationPolicy:"), visible ? Regular : Accessory);
            if (visible)
            {
                // Without this the window may open behind the program in front.
                SendBool(app, sel_registerName("activateIgnoringOtherApps:"), 1);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendLong(nint receiver, nint selector, long value);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendBool(nint receiver, nint selector, byte value);
}
