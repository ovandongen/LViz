using System.IO.Pipes;
using LViz.Core.Diagnostics;

namespace LViz.App.Services;

/// <summary>
/// The local IPC surface the resident LViz app exposes to host software (the CLI,
/// build scripts, app-focus watchers — see <c>lviz-host-agent-cli-spec.md</c> §5).
/// A <see cref="NamedPipeServerStream"/> named <see cref="PipeName"/>: cross-platform
/// in .NET (a Unix domain socket on macOS/Linux, a named pipe on Windows), scoped to
/// the user session by filesystem permissions, with no network exposure.
///
/// <para>One connection carries one line-based command; the server replies with the
/// dispatcher's response text and closes. The accept loop hands each client off so a
/// slow command can't block the next connection, then loops to accept again. Runs for
/// the app's lifetime — <see cref="Start"/> at launch, <see cref="Stop"/> on exit.</para>
/// </summary>
public sealed class LvizIpcServer : IDisposable
{
    public const string PipeName = "lviz";

    private readonly Func<string?, string> _dispatch;
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    /// <param name="dispatch">Maps a request line to a response string. Must not throw.</param>
    /// <param name="pipeName">Pipe name override (tests use a unique name for isolation); defaults to <see cref="PipeName"/>.</param>
    public LvizIpcServer(Func<string?, string> dispatch, string? pipeName = null)
    {
        _dispatch = dispatch;
        _pipeName = pipeName ?? PipeName;
    }

    public void Start()
    {
        if (_loop is not null || _disposed) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        DiagnosticLog.Info("Ipc", $"listening on pipe '{_pipeName}'");
    }

    public void Stop()
    {
        if (_loop is null) return;
        _cts!.Cancel();
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            catch (Exception ex)
            {
                // Pipe creation can fail transiently (e.g. instance limit); back off
                // and retry rather than killing the listener.
                DiagnosticLog.Warn("Ipc", $"pipe create failed: {ex.Message}");
                try { await Task.Delay(250, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn("Ipc", $"accept failed: {ex.Message}");
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = HandleClientAsync(pipe);   // hand off, loop to accept the next
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            var response = _dispatch(line);
            await writer.WriteLineAsync(response).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Ipc", $"client handling failed: {ex.Message}");
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }
}
