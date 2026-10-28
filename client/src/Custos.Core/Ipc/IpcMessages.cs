namespace Custos.Core.Ipc;

/// <summary>Parameters of <see cref="PipeProtocol.Methods.Hello"/>.</summary>
public sealed class HelloRequest
{
    /// <summary><see cref="PipeProtocol.Version"/> of the interface.</summary>
    public required int Protocol { get; init; }

    public required string ClientVersion { get; init; }
}

/// <summary>Result of <see cref="PipeProtocol.Methods.Hello"/>.</summary>
public sealed class HelloResult
{
    public required int Protocol { get; init; }

    public required string ServiceVersion { get; init; }

    /// <summary>One of <see cref="PipeProtocol.ServiceStates"/>.</summary>
    public required string State { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The caller as the service sees it, for example <c>OFFICE\anna</c>. Null if the service could not tell.</summary>
    public string? User { get; init; }

    /// <summary>The caller is a member of BUILTIN\Administrators, even with a UAC-filtered token (section 4).</summary>
    public bool IsAdmin { get; init; }

    /// <summary>Windows session of the caller as the service sees it.</summary>
    public int? SessionId { get; init; }
}

/// <summary>Payload of <see cref="PipeProtocol.Events.ServiceState"/>.</summary>
public sealed class ServiceStateEvent
{
    /// <summary>One of <see cref="PipeProtocol.ServiceStates"/>.</summary>
    public required string State { get; init; }
}
