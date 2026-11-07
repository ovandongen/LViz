using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using LViz.App.Services.Secrets;
using Xunit;

namespace LViz.Tests.Secrets;

/// <summary>
/// <see cref="LinuxSecretServiceStore"/> is plain socket code with no OS
/// gate, so these tests run on every OS. They point the store at a bus
/// address that can never connect, or at a "silent bus" (a Unix socket that
/// accepts connections and never answers) to prove the per-call timeout: a
/// silent bus must produce the failure result within the timeout instead of
/// hanging. Nothing here touches a real Secret Service; the "unavailable"
/// contract (null / false / no-op, never a throw) is what is under test.
/// Every awaited call gets a 10-second outer guard so a call that ignores
/// its timeout shows up as a test failure.
/// </summary>
public class LinuxSecretServiceStoreTests
{
    private const string UnreachableBusAddress = "unix:path=/nonexistent/lviz-test-bus";

    /// <summary>The per-call timeout the silent-bus tests set.</summary>
    private static readonly TimeSpan ShortCallTimeout = TimeSpan.FromMilliseconds(200);

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

    [Fact]
    public void CallTimeout_DefaultsToTenSeconds()
    {
        var store = new LinuxSecretServiceStore(UnreachableBusAddress);

        Assert.Equal(TimeSpan.FromSeconds(10), store.CallTimeout);
    }

    [Fact]
    public async Task Read_BusNeverAnswers_ReturnsNullAfterTimeout()
    {
        if (OperatingSystem.IsWindows())
            return; // AF_UNIX sockets are what a silent bus is made of

        using SilentBus bus = new();
        var store = new LinuxSecretServiceStore(bus.Address, ShortCallTimeout);

        var stopwatch = Stopwatch.StartNew();
        string? secret = await WithTimeout(store.ReadAsync("github"));
        stopwatch.Stop();

        Assert.Null(secret);
        AssertWaitedForTheTimeout(stopwatch);
    }

    [Fact]
    public async Task Write_BusNeverAnswers_ReturnsFalseAfterTimeout()
    {
        if (OperatingSystem.IsWindows())
            return;

        using SilentBus bus = new();
        var store = new LinuxSecretServiceStore(bus.Address, ShortCallTimeout);

        var stopwatch = Stopwatch.StartNew();
        bool written = await WithTimeout(store.WriteAsync("github", "x"));
        stopwatch.Stop();

        Assert.False(written);
        AssertWaitedForTheTimeout(stopwatch);
    }

    [Fact]
    public async Task Delete_BusNeverAnswers_CompletesAfterTimeout()
    {
        if (OperatingSystem.IsWindows())
            return;

        using SilentBus bus = new();
        var store = new LinuxSecretServiceStore(bus.Address, ShortCallTimeout);

        var stopwatch = Stopwatch.StartNew();
        await WithTimeout(store.DeleteAsync("github"));
        stopwatch.Stop();

        AssertWaitedForTheTimeout(stopwatch);
    }

    [Fact]
    public async Task Read_CancelledWhileWaiting_ThrowsOperationCanceled()
    {
        if (OperatingSystem.IsWindows())
            return;

        using SilentBus bus = new();
        // Far longer than the cancel delay: only the token can end this call.
        var store = new LinuxSecretServiceStore(bus.Address, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(ShortCallTimeout);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WithTimeout(store.ReadAsync("github", cts.Token)));
    }

    /// <summary>
    /// A silent-bus call has a 200 ms timeout: finishing well before it means
    /// the store failed fast instead of timing out (which would not prove the
    /// timeout path); going past 5 seconds means the timeout did not bound it.
    /// </summary>
    private static void AssertWaitedForTheTimeout(Stopwatch stopwatch) =>
        Assert.True(
            stopwatch.ElapsedMilliseconds >= ShortCallTimeout.TotalMilliseconds / 2
            && stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"call took {stopwatch.ElapsedMilliseconds} ms — expected it to wait for the "
            + $"{ShortCallTimeout.TotalMilliseconds} ms timeout and stay within 5 seconds");

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

    /// <summary>
    /// A fake D-Bus: a Unix socket that accepts connections and then never
    /// writes a single byte, so the SASL handshake (and with it every
    /// connection call) hangs. Dispose closes the listener and clients and
    /// deletes the socket file.
    /// </summary>
    private sealed class SilentBus : IDisposable
    {
        private readonly Socket _listener;
        private readonly string _socketPath;
        private readonly ConcurrentBag<Socket> _clients = new();

        /// <summary>Store address for this bus, e.g. "unix:path=/tmp/lviz-bus-….sock".</summary>
        public string Address { get; }

        public SilentBus()
        {
            // Keep the path short: macOS limits Unix socket paths to 104 characters.
            _socketPath = Path.Combine(Path.GetTempPath(), $"lviz-bus-{Guid.NewGuid():N}.sock");
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            _listener.Listen(8);
            Address = $"unix:path={_socketPath}";
            _ = Task.Run(AcceptAndNeverAnswerAsync);
        }

        private async Task AcceptAndNeverAnswerAsync()
        {
            while (true)
            {
                Socket client;
                try
                {
                    client = await _listener.AcceptAsync();
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
                {
                    return; // listener disposed by Dispose() — the loop is done
                }

                // Accept, hold open, never answer.
                _clients.Add(client);
            }
        }

        public void Dispose()
        {
            _listener.Dispose();
            while (_clients.TryTake(out Socket? client))
                client.Dispose();
            try
            {
                File.Delete(_socketPath);
            }
            catch (IOException)
            {
                // A socket file we cannot delete at teardown is harmless here.
            }
        }
    }
}
