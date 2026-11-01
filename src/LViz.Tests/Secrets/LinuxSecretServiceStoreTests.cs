using LViz.App.Services.Secrets;
using Xunit;

namespace LViz.Tests.Secrets;

/// <summary>
/// <see cref="LinuxSecretServiceStore"/> is plain socket code with no OS
/// gate, so these tests run on every OS. They point the store at a bus
/// address that can never connect — nothing here touches a real Secret
/// Service; the "unavailable" contract (null / false / no-op, never a throw)
/// is what is under test. Every awaited call gets a 10-second timeout so a
/// connect that hangs instead of failing fast shows up as a test failure.
/// </summary>
public class LinuxSecretServiceStoreTests
{
    private const string UnreachableBusAddress = "unix:path=/nonexistent/lviz-test-bus";

    [Fact]
    public async Task Read_UnreachableBus_ReturnsNull()
    {
        var store = new LinuxSecretServiceStore(UnreachableBusAddress);

        Assert.Null(await WithTimeout(store.ReadAsync("github")));
    }

    [Fact]
    public async Task Write_UnreachableBus_ReturnsFalse()
    {
        var store = new LinuxSecretServiceStore(UnreachableBusAddress);

        Assert.False(await WithTimeout(store.WriteAsync("github", "x")));
    }

    [Fact]
    public async Task Delete_UnreachableBus_CompletesWithoutThrowing()
    {
        var store = new LinuxSecretServiceStore(UnreachableBusAddress);

        await WithTimeout(store.DeleteAsync("github"));
    }

    [Fact]
    public async Task Read_CancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var store = new LinuxSecretServiceStore(UnreachableBusAddress);

        // The token is checked before connecting, so this throws even though
        // the bus address is bogus.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.ReadAsync("github", cts.Token));
    }

    private static async Task<T> WithTimeout<T>(Task<T> operation)
    {
        Task completed = await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(ReferenceEquals(completed, operation), "Secret store call did not finish within 10 seconds");
        return await operation;
    }

    private static async Task WithTimeout(Task operation)
    {
        Task completed = await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(ReferenceEquals(completed, operation), "Secret store call did not finish within 10 seconds");
        await operation;
    }
}
