namespace Custos.Core.Ipc;

/// <summary>
/// The local channel between the interface (Custos.exe) and the service (section 3.2 of the client spec).
/// </summary>
/// <remarks>
/// Transport: the named pipe <c>\\.\pipe\custos</c>. Every message is a frame: a 4-byte little-endian length
/// followed by that many bytes of UTF-8 JSON (<see cref="PipeMessage"/>). The interface sends requests, the
/// service answers each with a response carrying the same id and may send events at any time.
/// </remarks>
public static class PipeProtocol
{
    /// <summary>Pipe name; the full Windows path is <c>\\.\pipe\custos</c>.</summary>
    public const string PipeName = "custos";

    /// <summary>Version of this local protocol. The interface and the service are always installed together,
    /// so a mismatch means a broken installation.</summary>
    public const int Version = 1;

    /// <summary>Largest message the service accepts from an interface.</summary>
    public const int MaxRequestBytes = 1024 * 1024;

    /// <summary>Largest message the interface accepts from the service.</summary>
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>Request methods.</summary>
    public static class Methods
    {
        /// <summary>First request on every connection: <see cref="HelloRequest"/> → <see cref="HelloResult"/>.</summary>
        public const string Hello = "hello";
    }

    /// <summary>Event names.</summary>
    public static class Events
    {
        /// <summary>The service state changed: <see cref="ServiceStateEvent"/>.</summary>
        public const string ServiceState = "service.state";
    }

    /// <summary>Values of <see cref="HelloResult.State"/> and <see cref="ServiceStateEvent.State"/>.</summary>
    public static class ServiceStates
    {
        public const string Running = "running";
        public const string Stopping = "stopping";
    }

    /// <summary>Values of <see cref="PipeError.Code"/>.</summary>
    public static class Errors
    {
        public const string BadRequest = "bad_request";
        public const string UnknownMethod = "unknown_method";
        public const string ProtocolMismatch = "protocol_mismatch";
        public const string Internal = "internal";
    }
}
