namespace LViz.Core.Persistence;

/// <summary>
/// Platform-provided storage for small secret strings (OAuth tokens), keyed
/// by a stable name such as <c>"github"</c>. Implementations back this with
/// the OS credential store (macOS Keychain, Windows Credential Manager,
/// Secret Service on Linux).
///
/// <para>
/// Contract for implementations: they <b>never throw</b> for platform or
/// store failures — a store that is unavailable or a failed operation is
/// reported through the return values ("not available"), with a warning via
/// <c>DiagnosticLog.Warn("Secrets", …)</c> that never includes the stored
/// value. Secret values must never be logged, at any level, by an
/// implementation or its callers. The only exception allowed to surface is
/// <see cref="OperationCanceledException"/> when <c>ct</c> is cancelled.
/// </para>
/// </summary>
public interface ISecretStore
{
    /// <summary>Reads the secret stored under key; null when missing or the store is unavailable.</summary>
    Task<string?> ReadAsync(string key, CancellationToken ct = default);

    /// <summary>Stores value under key, replacing any old value; false when the store is unavailable or the write failed.</summary>
    Task<bool> WriteAsync(string key, string value, CancellationToken ct = default);

    /// <summary>Removes key; no-op when missing or unavailable.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
}
