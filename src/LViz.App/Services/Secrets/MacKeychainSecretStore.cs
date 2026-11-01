using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using LViz.Core.Diagnostics;
using LViz.Core.Persistence;

namespace LViz.App.Services.Secrets;

/// <summary>
/// <see cref="ISecretStore"/> backed by the macOS login keychain, using the
/// older generic-password Security API (simple C signatures, still supported
/// on current macOS) rather than <c>SecItem*</c> with CFDictionaries.
/// Every item is identified by service <see cref="ServiceName"/> and
/// account = the <c>key</c> argument.
///
/// <para>
/// Platform failures never throw: Read reports null, Write reports false,
/// Delete no-ops, each with a <c>DiagnosticLog.Warn("Secrets", …)</c> that
/// never includes the stored value. On non-macOS hosts every method returns
/// the "unavailable" result without a native call and without a log line.
/// Only a cancelled <c>ct</c> surfaces (as <see cref="OperationCanceledException"/>).
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacKeychainSecretStore : ISecretStore
{
    /// <summary>Keychain service ("where") under which every LViz secret is stored.</summary>
    public const string ServiceName = "LViz";

    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;

    public Task<string?> ReadAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) return Task.FromResult<string?>(null);

        var service = Encoding.UTF8.GetBytes(ServiceName);
        var account = Encoding.UTF8.GetBytes(key);
        var passwordData = IntPtr.Zero;
        var itemRef = IntPtr.Zero;
        try
        {
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)service.Length, service,
                (uint)account.Length, account,
                out var passwordLength, out passwordData, out itemRef);
            if (status == ErrSecItemNotFound) return Task.FromResult<string?>(null);
            if (status != ErrSecSuccess)
            {
                DiagnosticLog.Warn("Secrets", $"Keychain read failed ({status})");
                return Task.FromResult<string?>(null);
            }

            var buffer = new byte[passwordLength];
            Marshal.Copy(passwordData, buffer, 0, (int)passwordLength);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(buffer));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            DiagnosticLog.Warn("Secrets", $"Keychain unavailable ({ex.GetType().Name})");
            return Task.FromResult<string?>(null);
        }
        finally
        {
            ReleaseFound(passwordData, itemRef);
        }
    }

    public Task<bool> WriteAsync(string key, string value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) return Task.FromResult(false);

        var service = Encoding.UTF8.GetBytes(ServiceName);
        var account = Encoding.UTF8.GetBytes(key);
        var secret = Encoding.UTF8.GetBytes(value);
        var passwordData = IntPtr.Zero;
        var itemRef = IntPtr.Zero;
        try
        {
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)service.Length, service,
                (uint)account.Length, account,
                out var passwordLength, out passwordData, out itemRef);
            if (status == ErrSecSuccess)
            {
                // Existing item: replace its data in place (attrList null =
                // no attribute changes).
                status = SecKeychainItemModifyAttributesAndData(
                    itemRef, IntPtr.Zero, (uint)secret.Length, secret);
            }
            else if (status == ErrSecItemNotFound)
            {
                status = SecKeychainAddGenericPassword(
                    IntPtr.Zero,
                    (uint)service.Length, service,
                    (uint)account.Length, account,
                    (uint)secret.Length, secret,
                    IntPtr.Zero);
            }

            if (status == ErrSecSuccess) return Task.FromResult(true);
            DiagnosticLog.Warn("Secrets", $"Keychain write failed ({status})");
            return Task.FromResult(false);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            DiagnosticLog.Warn("Secrets", $"Keychain unavailable ({ex.GetType().Name})");
            return Task.FromResult(false);
        }
        finally
        {
            ReleaseFound(passwordData, itemRef);
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) return Task.CompletedTask;

        var service = Encoding.UTF8.GetBytes(ServiceName);
        var account = Encoding.UTF8.GetBytes(key);
        var passwordData = IntPtr.Zero;
        var itemRef = IntPtr.Zero;
        try
        {
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)service.Length, service,
                (uint)account.Length, account,
                out var passwordLength, out passwordData, out itemRef);
            if (status == ErrSecSuccess)
            {
                status = SecKeychainItemDelete(itemRef);
                if (status != ErrSecSuccess)
                    DiagnosticLog.Warn("Secrets", $"Keychain delete failed ({status})");
            }
            else if (status != ErrSecItemNotFound)
            {
                // Missing is a no-op; any other status is a failure.
                DiagnosticLog.Warn("Secrets", $"Keychain delete failed ({status})");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            DiagnosticLog.Warn("Secrets", $"Keychain unavailable ({ex.GetType().Name})");
        }
        finally
        {
            ReleaseFound(passwordData, itemRef);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Frees the buffer and releases the item returned by a successful
    /// SecKeychainFindGenericPassword. Failures here cannot be reported
    /// (the caller is already returning its result); the primary call would
    /// only be missing if the primary Find had already thrown and warned.
    /// </summary>
    private static void ReleaseFound(IntPtr passwordData, IntPtr itemRef)
    {
        try
        {
            if (passwordData != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
            if (itemRef != IntPtr.Zero) CFRelease(itemRef);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Nothing to clean up if the library itself is unreachable.
        }
    }

    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // The names are passed as UTF-8 byte arrays with their byte length —
    // the C API takes (length, char*) pairs, so no NUL terminator is needed.
    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName,
        out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName,
        uint passwordLength, byte[] passwordData,
        IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}
