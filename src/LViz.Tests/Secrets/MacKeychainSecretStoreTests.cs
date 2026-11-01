using LViz.App.Services.Secrets;
using Xunit;

namespace LViz.Tests.Secrets;

// CA1416: the store type is [SupportedOSPlatform("macos")], but calling it
// from these platform-neutral tests is exactly the point — on non-macOS the
// runtime OS gate inside every method returns the "unavailable" result
// without a native call, and the cancellation check runs before that gate.
#pragma warning disable CA1416

/// <summary>
/// <see cref="MacKeychainSecretStore"/> under test on the CI/dev box, which
/// is not a Mac: the suite must never touch a real Keychain. The non-macOS
/// tests assert the runtime OS gate (null / false / no-op without any native
/// call) and bail out immediately on macOS; the cancellation test relies on
/// the token check running before the OS gate, so it is safe everywhere.
/// </summary>
public class MacKeychainSecretStoreTests
{
    [Fact]
    public async Task Read_OnNonMacOS_ReturnsNull()
    {
        if (OperatingSystem.IsMacOS()) return;

        var store = new MacKeychainSecretStore();

        Assert.Null(await store.ReadAsync("github"));
    }

    [Fact]
    public async Task Write_OnNonMacOS_ReturnsFalse()
    {
        if (OperatingSystem.IsMacOS()) return;

        var store = new MacKeychainSecretStore();

        Assert.False(await store.WriteAsync("github", "x"));
    }

    [Fact]
    public async Task Delete_OnNonMacOS_CompletesWithoutThrowing()
    {
        if (OperatingSystem.IsMacOS()) return;

        var store = new MacKeychainSecretStore();

        await store.DeleteAsync("github");
    }

    [Fact]
    public async Task Read_CancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var store = new MacKeychainSecretStore();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.ReadAsync("github", cts.Token));
    }
}

#pragma warning restore CA1416
