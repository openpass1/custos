#if CUSTOS_LEGACY
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Custos.Core.Ipc;

/// <summary>
/// The pipe between the service and the interface in the legacy edition (.NET 6). .NET 6 can neither create the first
/// instance of a pipe only (<c>PipeOptions.FirstPipeInstance</c> came in .NET 8) nor open a pipe asking for exactly the
/// rights the pipe grants users (<see cref="PipeAccessRights.ReadWrite"/> without the right to create instances: the
/// constructor came in .NET 7), so both ends are opened through Windows directly and handed to the .NET pipe classes.
/// The rules stay those of <see cref="ServicePipe"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LegacyPipes
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const int PipeUnlimitedInstances = 255;
    private const uint OpenExisting = 3;
    private const uint SecuritySqosPresent = 0x00100000;
    private const uint SecurityIdentification = 0x00010000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;
    private const int ErrorPipeBusy = 231;

    /// <summary>The server end, as <c>NamedPipeServerStreamAcl.Create</c> would make it with FirstPipeInstance.</summary>
    /// <exception cref="UnauthorizedAccessException">Another process owns a pipe with this name.</exception>
    public static NamedPipeServerStream CreateServer(string pipeName, bool firstInstance, PipeSecurity security)
    {
        byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = pinned.AddrOfPinnedObject(),
                InheritHandle = 0,
            };
            uint openMode = PipeAccessDuplex | FileFlagOverlapped | (firstInstance ? FileFlagFirstPipeInstance : 0);
            var handle = CreateNamedPipe(@"\\.\pipe\" + pipeName, openMode, PipeRejectRemoteClients, PipeUnlimitedInstances, 0, 0, 0, ref attributes);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw error == ErrorAccessDenied
                    ? new UnauthorizedAccessException(new Win32Exception(error).Message)
                    : new IOException(new Win32Exception(error).Message, error);
            }

            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>
    /// The client end, connected: asks for <see cref="PipeAccessRights.ReadWrite"/> and lets the service identify the
    /// caller but not act on its behalf, as <see cref="ServiceClient"/> does on .NET 10.
    /// </summary>
    /// <exception cref="TimeoutException">The pipe did not appear in time: the service is not running.</exception>
    /// <exception cref="UnauthorizedAccessException">The pipe refused this user.</exception>
    public static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string path = @"\\.\pipe\" + pipeName;
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = CreateFile(path, (uint)PipeAccessRights.ReadWrite, 0, IntPtr.Zero, OpenExisting,
                FileFlagOverlapped | SecuritySqosPresent | SecurityIdentification, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                return new NamedPipeClientStream(PipeDirection.InOut, isAsync: true, isConnected: true, handle);
            }

            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == ErrorAccessDenied)
            {
                throw new UnauthorizedAccessException(new Win32Exception(error).Message);
            }

            if (error is not (ErrorFileNotFound or ErrorPipeBusy))
            {
                throw new IOException(new Win32Exception(error).Message, error);
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException();
            }

            // Not there yet, or every instance is taken: look again shortly, as NamedPipeClientStream.ConnectAsync does.
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, int maxInstances, int outBufferSize, int inBufferSize, int defaultTimeout, ref SecurityAttributes securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafePipeHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
#endif
