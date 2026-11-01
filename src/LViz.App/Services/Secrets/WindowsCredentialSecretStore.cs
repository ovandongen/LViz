using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using LViz.Core.Diagnostics;
using LViz.Core.Persistence;

namespace LViz.App.Services.Secrets;

/// <summary>
/// <see cref="ISecretStore"/> backed by the Windows Credential Manager, using
/// the classic <c>Cred*</c> advapi32 API. Every secret is a generic credential
/// whose target name is <see cref="TargetFor"/> and whose user name is
/// "LViz"; the value is stored UTF-8-encoded in the credential blob.
///
/// <para>
/// Platform failures never throw: Read reports null, Write reports false,
/// Delete no-ops, each with a <c>DiagnosticLog.Warn("Secrets", …)</c> that
/// never includes the stored value. On non-Windows hosts every method returns
/// the "unavailable" result without a native call and without a log line.
/// Only a cancelled <c>ct</c> surfaces (as <see cref="OperationCanceledException"/>).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCredentialSecretStore : ISecretStore
{
    /// <summary>The credential target name under which a key is stored.</summary>
    internal static string TargetFor(string key) => $"LViz:{key}";

    /// <summary>CredWrite's blob cap (CRED_MAX_CREDENTIAL_BLOB_SIZE).</summary>
    internal const int MaxBlobBytes = 2560;

    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    public Task<string?> ReadAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Task.FromResult<string?>(null);

        var credentialPtr = IntPtr.Zero;
        try
        {
            if (!CredRead(TargetFor(key), CRED_TYPE_GENERIC, 0, out credentialPtr))
            {
                // Missing is a null read, not a failure worth a log line.
                var error = Marshal.GetLastWin32Error();
                if (error != ERROR_NOT_FOUND)
                    DiagnosticLog.Warn("Secrets", $"Credential read failed (Win32 error {error})");
                return Task.FromResult<string?>(null);
            }

            var cred = Marshal.PtrToStructure<Credential>(credentialPtr);
            if (cred.CredentialBlob == IntPtr.Zero && cred.CredentialBlobSize > 0)
            {
                // Malformed credential — don't hand a bogus pointer to Marshal.Copy.
                DiagnosticLog.Warn("Secrets", "Credential read returned a blob size with no blob");
                return Task.FromResult<string?>(null);
            }

            var size = (int)cred.CredentialBlobSize;
            var buffer = new byte[size];
            if (size > 0) Marshal.Copy(cred.CredentialBlob, buffer, 0, size);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(buffer));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            DiagnosticLog.Warn("Secrets", $"Credential store unavailable ({ex.GetType().Name})");
            return Task.FromResult<string?>(null);
        }
        finally
        {
            FreeCredential(credentialPtr);
        }
    }

    public Task<bool> WriteAsync(string key, string value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Task.FromResult(false);

        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxBlobBytes)
        {
            DiagnosticLog.Warn("Secrets", $"Credential too large ({bytes.Length} bytes)");
            return Task.FromResult(false);
        }

        var targetPtr = IntPtr.Zero;
        var userNamePtr = IntPtr.Zero;
        var blobPtr = IntPtr.Zero;
        try
        {
            targetPtr = Marshal.StringToHGlobalUni(TargetFor(key));
            userNamePtr = Marshal.StringToHGlobalUni("LViz");
            blobPtr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blobPtr, bytes.Length);

            var cred = new Credential
            {
                Flags = 0,
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                Comment = IntPtr.Zero,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = IntPtr.Zero,
                UserName = userNamePtr,
            };

            // CredWrite replaces an existing credential with the same target.
            if (CredWrite(ref cred, 0)) return Task.FromResult(true);

            var error = Marshal.GetLastWin32Error();
            DiagnosticLog.Warn("Secrets", $"Credential write failed (Win32 error {error})");
            return Task.FromResult(false);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            DiagnosticLog.Warn("Secrets", $"Credential store unavailable ({ex.GetType().Name})");
            return Task.FromResult(false);
        }
        finally
        {
            if (blobPtr != IntPtr.Zero)
            {
                // Scrub the secret from the native buffer before releasing it.
                Marshal.Copy(new byte[bytes.Length], 0, blobPtr, bytes.Length);
                Marshal.FreeHGlobal(blobPtr);
            }
            if (userNamePtr != IntPtr.Zero) Marshal.FreeHGlobal(userNamePtr);
            if (targetPtr != IntPtr.Zero) Marshal.FreeHGlobal(targetPtr);
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;

        try
        {
            if (!CredDelete(TargetFor(key), CRED_TYPE_GENERIC, 0))
            {
                // Missing is a no-op; any other error deserves a log line.
                var error = Marshal.GetLastWin32Error();
                if (error != ERROR_NOT_FOUND)
                    DiagnosticLog.Warn("Secrets", $"Credential delete failed (Win32 error {error})");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            DiagnosticLog.Warn("Secrets", $"Credential store unavailable ({ex.GetType().Name})");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Frees the buffer a successful CredRead allocated. A failure here cannot
    /// be reported (the caller is already returning its result); the library
    /// could only be unreachable if the primary CredRead had already thrown
    /// and warned.
    /// </summary>
    private static void FreeCredential(IntPtr credentialPtr)
    {
        if (credentialPtr == IntPtr.Zero) return;
        try
        {
            CredFree(credentialPtr);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Nothing to clean up if the library itself is unreachable.
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}
