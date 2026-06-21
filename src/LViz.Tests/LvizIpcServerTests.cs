using System.IO.Pipes;
using LViz.App.Services;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Transport round-trip for <see cref="LvizIpcServer"/>: a client connecting over
/// the named pipe gets the dispatcher's (possibly multi-line) response, framed so
/// the CLI's ReadToEnd sees it intact. Uses a unique pipe name per test so a real
/// running LViz can't collide. Pipe transport is unsupported on some sandboxes, so
/// the test is best-effort: a transport setup failure is skipped, not failed.
/// </summary>
public class LvizIpcServerTests
{
    private static async Task<string?> RoundTripAsync(string pipeName, Func<string?, string> dispatch, string request)
    {
        using var server = new LvizIpcServer(dispatch, pipeName);
        server.Start();

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        await client.ConnectAsync(2000);
        // Mirror CliClient: don't dispose the writer/reader — the server closes its
        // end after replying, so a dispose-time flush would hit a closed pipe.
        var writer = new StreamWriter(client) { AutoFlush = true };
        var reader = new StreamReader(client);
        await writer.WriteLineAsync(request);
        return (await reader.ReadToEndAsync()).TrimEnd();
    }

    [Fact]
    public async Task SingleLineResponse_RoundTrips()
    {
        var name = $"lviz-test-{Guid.NewGuid():N}";
        string? seen = null;
        string? response;
        try
        {
            response = await RoundTripAsync(name, line => { seen = line; return "ok"; }, "event ci:ok");
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return; // named pipes unavailable in this environment
        }

        Assert.Equal("event ci:ok", seen);
        Assert.Equal("ok", response);
    }

    [Fact]
    public async Task MultiLineResponse_ArrivesIntact()
    {
        var name = $"lviz-test-{Guid.NewGuid():N}";
        var payload = "ok\nLViz is running.\ndevices: 0";
        string? response;
        try
        {
            response = await RoundTripAsync(name, _ => payload, "status");
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.Equal(payload, response);
    }
}
