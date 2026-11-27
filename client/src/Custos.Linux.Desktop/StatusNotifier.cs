using Tmds.DBus.Protocol;

namespace Custos.Linux.Desktop;

internal static class StatusNotifier
{
    private const string Watcher = "org.kde.StatusNotifierWatcher";

    /// <summary>A session bus or an icon watcher without a panel host is insufficient to show the tray menu.</summary>
    public static async Task<bool> HostRegisteredAsync()
    {
        if (DBusAddress.Session is not { Length: > 0 } address)
        {
            return false;
        }

        try
        {
            using var connection = new DBusConnection(address);
            return await QueryAsync(connection).WaitAsync(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DBusExceptionBase or TimeoutException or IOException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> QueryAsync(DBusConnection connection)
    {
        await connection.ConnectAsync().ConfigureAwait(false);
        return await connection.CallMethodAsync(CreateQuery(connection), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            return reader.ReadVariantValue().GetBool();
        }).ConfigureAwait(false);
    }

    private static MessageBuffer CreateQuery(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Watcher, path: "/StatusNotifierWatcher",
            @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss", flags: MessageFlags.NoAutoStart);
        writer.WriteString(Watcher);
        writer.WriteString("IsStatusNotifierHostRegistered");
        return writer.CreateMessage();
    }
}
