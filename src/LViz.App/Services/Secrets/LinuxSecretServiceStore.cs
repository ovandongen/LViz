using System.Text;
using LViz.Core.Diagnostics;
using LViz.Core.Persistence;
using Tmds.DBus.Protocol;

namespace LViz.App.Services.Secrets;

/// <summary>
/// <see cref="ISecretStore"/> backed by the freedesktop Secret Service
/// (GNOME Keyring, KWallet) over the session D-Bus, using the low-level
/// Tmds.DBus.Protocol API. Items are identified by the attributes
/// <c>application=lviz, account=&lt;key&gt;</c>; one session per call keeps
/// the code simple — calls are rare. The "plain" session algorithm is used:
/// the local session bus is already the trust boundary, so the spec allows
/// transferring the secret unencrypted.
///
/// <para>
/// Platform failures never throw: Read reports null, Write reports false,
/// Delete no-ops, each with a <c>DiagnosticLog.Warn("Secrets", …)</c> that
/// never includes the stored value. Only a cancelled <c>ct</c> surfaces
/// (as <see cref="OperationCanceledException"/>), and it is checked before
/// any connection is attempted. Locked items and unlock prompts are
/// reported as failures too — v1 does not drive prompts.
/// </para>
/// </summary>
internal sealed class LinuxSecretServiceStore : ISecretStore
{
    private const string Destination = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string ItemInterface = "org.freedesktop.Secret.Item";
    private const string CollectionInterface = "org.freedesktop.Secret.Collection";
    private const string ContentType = "text/plain; charset=utf8";

    /// <summary>The D-Bus root path: used for "no object" (no default collection, no prompt).</summary>
    private const string RootPath = "/";

    private readonly string? _busAddress;

    /// <param name="busAddress">D-Bus address to use; null means the session
    /// bus address from the environment (<see cref="Address.Session"/>).</param>
    public LinuxSecretServiceStore(string? busAddress = null)
        => _busAddress = busAddress;

    /// <summary>Item label for a key, e.g. "LViz github".</summary>
    internal static string LabelFor(string key) => $"LViz {key}";

    /// <summary>The attribute pair set every LViz item carries.</summary>
    internal static Dictionary<string, string> AttributesFor(string key) => new()
    {
        ["application"] = "lviz",
        ["account"] = key,
    };

    public async Task<string?> ReadAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using Connection? connection = TryOpenConnection();
            if (connection is null)
            {
                DiagnosticLog.Warn("Secrets", "Secret Service unavailable (no session bus address)");
                return null;
            }
            await connection.ConnectAsync().ConfigureAwait(false);

            string session = await OpenSessionAsync(connection).ConfigureAwait(false);
            (ObjectPath[] unlocked, ObjectPath[] locked) = await SearchItemsAsync(connection, key)
                .ConfigureAwait(false);
            if (unlocked.Length == 0)
            {
                // Missing is a null read without a log line; locked is a failure to read.
                if (locked.Length > 0)
                    DiagnosticLog.Warn("Secrets", "Secret Service item is locked");
                return null;
            }

            return await GetSecretAsync(connection, unlocked[0].ToString(), session).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Secrets", $"Secret Service read failed ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    public async Task<bool> WriteAsync(string key, string value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using Connection? connection = TryOpenConnection();
            if (connection is null)
            {
                DiagnosticLog.Warn("Secrets", "Secret Service unavailable (no session bus address)");
                return false;
            }
            await connection.ConnectAsync().ConfigureAwait(false);

            string session = await OpenSessionAsync(connection).ConfigureAwait(false);
            string collection = await ReadAliasAsync(connection, "default").ConfigureAwait(false);
            if (collection == RootPath)
            {
                DiagnosticLog.Warn("Secrets", "Secret Service has no default collection");
                return false;
            }

            string prompt = await CreateItemAsync(connection, collection, key, value, session)
                .ConfigureAwait(false);
            if (prompt != RootPath)
            {
                // The collection is locked and wants the user prompted; v1 doesn't drive prompts.
                DiagnosticLog.Warn("Secrets", "Secret Service needs an unlock prompt");
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Secrets", $"Secret Service write failed ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using Connection? connection = TryOpenConnection();
            if (connection is null)
            {
                DiagnosticLog.Warn("Secrets", "Secret Service unavailable (no session bus address)");
                return;
            }
            await connection.ConnectAsync().ConfigureAwait(false);

            // No session needed: Delete takes no secret struct.
            (ObjectPath[] unlocked, ObjectPath[] locked) = await SearchItemsAsync(connection, key)
                .ConfigureAwait(false);
            if (locked.Length > 0)
            {
                // A locked item cannot be deleted; saying so beats a silent no-op.
                DiagnosticLog.Warn("Secrets", "Secret Service item is locked");
            }
            foreach (ObjectPath item in unlocked)
                await DeleteItemAsync(connection, item.ToString()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Secrets", $"Secret Service delete failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// The address is resolved per call (a later run may have a session bus);
    /// null means no session bus address is known.
    /// </summary>
    private Connection? TryOpenConnection()
    {
        string? address = _busAddress ?? Address.Session;
        return address is null ? null : new Connection(address);
    }

    /// <summary>
    /// OpenSession("plain", "") → session path. The plain algorithm returns
    /// an empty output; the path is what the secret structs must reference.
    /// </summary>
    private static async Task<string> OpenSessionAsync(Connection connection)
    {
        MessageBuffer message;
        using (MessageWriter writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Destination, ServicePath, ServiceInterface, "OpenSession", "sv");
            writer.WriteString("plain");
            writer.WriteVariantString(string.Empty);
            message = writer.CreateMessage();
        }

        return await connection.CallMethodAsync(message, (Message m, object? state) =>
        {
            Reader reader = m.GetBodyReader();
            reader.ReadVariantValue();          // output — unused for "plain"
            return reader.ReadObjectPathAsString(); // session
        }).ConfigureAwait(false);
    }

    private static async Task<(ObjectPath[] Unlocked, ObjectPath[] Locked)> SearchItemsAsync(
        Connection connection, string key)
    {
        MessageBuffer message;
        using (MessageWriter writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Destination, ServicePath, ServiceInterface, "SearchItems", "a{ss}");
            ArrayStart attributes = writer.WriteDictionaryStart();
            foreach ((string name, string value) in AttributesFor(key))
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString(name);
                writer.WriteString(value);
            }
            writer.WriteDictionaryEnd(attributes);
            message = writer.CreateMessage();
        }

        return await connection.CallMethodAsync(message, (Message m, object? state) =>
        {
            Reader reader = m.GetBodyReader();
            ObjectPath[] unlocked = reader.ReadArrayOfObjectPath();
            ObjectPath[] locked = reader.ReadArrayOfObjectPath();
            return (unlocked, locked);
        }).ConfigureAwait(false);
    }

    /// <summary>GetSecret on an unlocked item; decodes the secret value bytes as UTF-8.</summary>
    private static async Task<string> GetSecretAsync(Connection connection, string itemPath, string session)
    {
        MessageBuffer message;
        using (MessageWriter writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Destination, itemPath, ItemInterface, "GetSecret", "o");
            writer.WriteObjectPath(session);
            message = writer.CreateMessage();
        }

        return await connection.CallMethodAsync(message, (Message m, object? state) =>
        {
            Reader reader = m.GetBodyReader();
            reader.AlignStruct();                  // secret struct (oayays)
            reader.ReadObjectPathAsString();       // session — echo
            reader.ReadArrayOfByte();              // parameters — empty for "plain"
            byte[] value = reader.ReadArrayOfByte();
            reader.ReadString();                   // content type
            return Encoding.UTF8.GetString(value);
        }).ConfigureAwait(false);
    }

    /// <summary>ReadAlias("default") → collection path; "/" when no default collection exists.</summary>
    private static async Task<string> ReadAliasAsync(Connection connection, string name)
    {
        MessageBuffer message;
        using (MessageWriter writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Destination, ServicePath, ServiceInterface, "ReadAlias", "s");
            writer.WriteString(name);
            message = writer.CreateMessage();
        }

        return await connection.CallMethodAsync(message, (Message m, object? state) =>
            m.GetBodyReader().ReadObjectPathAsString()).ConfigureAwait(false);
    }

    /// <summary>
    /// CreateItem on the collection with replace=true; returns the prompt
    /// path ("/" when the write went through without interaction).
    /// </summary>
    private static async Task<string> CreateItemAsync(
        Connection connection, string collectionPath, string key, string value, string session)
    {
        MessageBuffer message;
        using (MessageWriter writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                Destination, collectionPath, CollectionInterface, "CreateItem", "a{sv}(oayays)b");

            // Variant is obsolete in 0.21.3 — the a{sv} entries are built as VariantValue.
            Dictionary<string, VariantValue> properties = new()
            {
                ["org.freedesktop.Secret.Item.Label"] = VariantValue.String(LabelFor(key)),
                ["org.freedesktop.Secret.Item.Attributes"] = new Dict<string, string>(AttributesFor(key)).AsVariantValue(),
            };
            writer.WriteDictionary(properties);

            writer.WriteStructureStart();                      // secret (oayays)
            writer.WriteObjectPath(session);
            writer.WriteArray(Array.Empty<byte>());            // parameters — empty for "plain"
            writer.WriteArray(Encoding.UTF8.GetBytes(value));  // value
            writer.WriteString(ContentType);

            writer.WriteBool(true);                            // replace
            message = writer.CreateMessage();
        }

        return await connection.CallMethodAsync(message, (Message m, object? state) =>
        {
            Reader reader = m.GetBodyReader();
            reader.ReadObjectPathAsString();       // item path
            return reader.ReadObjectPathAsString(); // prompt
        }).ConfigureAwait(false);
    }

    /// <summary>Item Delete; the returned prompt path is ignored (no locked item is deletable).</summary>
    private static async Task DeleteItemAsync(Connection connection, string itemPath)
    {
        MessageBuffer message;
        using (MessageWriter writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Destination, itemPath, ItemInterface, "Delete");
            message = writer.CreateMessage();
        }

        await connection.CallMethodAsync(message).ConfigureAwait(false);
    }
}
