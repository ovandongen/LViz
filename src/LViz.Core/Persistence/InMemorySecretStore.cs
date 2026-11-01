namespace LViz.Core.Persistence;

/// <summary>
/// Plain in-memory <see cref="ISecretStore"/>: a dictionary behind a lock,
/// nothing persisted anywhere. Used by tests and as the factory's fallback
/// on platforms without a real secret store, where logins last only for the
/// process. Never throws; writes always succeed.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<string?> ReadAsync(string key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_entries.TryGetValue(key, out var value) ? value : null);
        }
    }

    public Task<bool> WriteAsync(string key, string value, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _entries[key] = value;
        }

        return Task.FromResult(true);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _entries.Remove(key);
        }

        return Task.CompletedTask;
    }
}
