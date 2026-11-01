using LViz.App.Services.Secrets;
using Xunit;

namespace LViz.Tests.Secrets;

// CA1416: the store type is [SupportedOSPlatform("windows")], but calling it
// from these platform-neutral tests is exactly the point — on non-Windows the
// runtime OS gate inside every method returns the "unavailable" result
// without a native call, and the cancellation check runs before that gate.
#pragma warning disable CA1416

/// <summary>
/// <see cref="WindowsCredentialSecretStore"/> under test on the CI/dev box,
/// which is not Windows: the suite must never touch the real Credential
/// Manager. The non-Windows tests assert the runtime OS gate (null / false /
/// no-op without any native call) and bail out immediately on Windows; the
/// cancellation test relies on the token check running before the OS gate, so
/// it is safe everywhere.
/// </summary>
public class WindowsCredentialSecretStoreTests
{
    [Fact]
    public void TargetFor_PrefixesKeyWithAppName()
    {
        Assert.Equal("LViz:github", WindowsCredentialSecretStore.TargetFor("github"));
    }

    [Fact]
    public void MaxBlobBytes_IsCredentialManagerBlobLimit()
    {
        // A typical GitHub token JSON is far below this; the constant guards
        // against silently accepting a blob CredWrite would reject.
        Assert.Equal(2560, WindowsCredentialSecretStore.MaxBlobBytes);
    }

    [Fact]
    public async Task Read_OnNonWindows_ReturnsNull()
    {
        if (OperatingSystem.IsWindows()) return;

        var store = new WindowsCredentialSecretStore();

        Assert.Null(await store.ReadAsync("github"));
    }

    [Fact]
    public async Task Write_OnNonWindows_ReturnsFalse()
    {
        if (OperatingSystem.IsWindows()) return;

        var store = new WindowsCredentialSecretStore();

        Assert.False(await store.WriteAsync("github", "x"));
    }

    [Fact]
    public async Task Delete_OnNonWindows_CompletesWithoutThrowing()
    {
        if (OperatingSystem.IsWindows()) return;

        var store = new WindowsCredentialSecretStore();

        await store.DeleteAsync("github");
    }

    [Fact]
    public async Task Read_CancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var store = new WindowsCredentialSecretStore();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.ReadAsync("github", cts.Token));
    }
}

#pragma warning restore CA1416
