using System.Diagnostics;
using System.IO.Pipes;
using LViz.App.Services;

namespace LViz.App.Cli;

/// <summary>
/// The CLI half of the single-binary design (see <c>lviz-host-agent-cli-spec.md</c>
/// §4): when the executable is invoked with a recognized sub-command it acts as a
/// thin client to the already-running LViz app, forwards one line over the
/// <see cref="LvizIpcServer.PipeName"/> pipe, prints the ack, and exits. It never
/// touches HID — the resident app owns the devices.
/// </summary>
public static class CliClient
{
    /// <summary>Verbs that divert a launch into CLI client mode. Anything else runs the app.</summary>
    public static readonly string[] Commands =
        { "event", "run", "status", "devices", "help", "--help", "-h" };

    private const int ConnectTimeoutMs = 1500;

    public static async Task<int> RunAsync(string[] args)
    {
        var verb = args[0];
        switch (verb)
        {
            case "help":
            case "--help":
            case "-h":
                PrintUsage();
                return 0;

            case "run":
                return await RunWrapAsync(args[1..]).ConfigureAwait(false);

            case "event":
                if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
                {
                    Console.Error.WriteLine("usage: lviz event <id>");
                    return 1;
                }
                return await SendAsync($"event {args[1]}").ConfigureAwait(false);

            case "status":
            case "devices":
                return await SendAsync(verb).ConfigureAwait(false);

            default:
                Console.Error.WriteLine($"lviz: unknown command '{verb}'");
                PrintUsage();
                return 1;
        }
    }

    // ── run [--id <name>] -- <cmd> [args…] ─────────────────────────────────────
    // Run the command, then fire <id>:ok / <id>:fail off its exit code so scripts
    // don't hand-write `&& lviz event x:ok || lviz event x:fail`. The id defaults to
    // the command's basename; override with --id.
    private static async Task<int> RunWrapAsync(string[] rest)
    {
        string? id = null;
        var i = 0;
        if (rest.Length >= 2 && rest[0] == "--id")
        {
            id = rest[1];
            i = 2;
        }

        if (i >= rest.Length || rest[i] != "--")
        {
            Console.Error.WriteLine("usage: lviz run [--id <name>] -- <command> [args…]");
            return 1;
        }
        var cmd = rest[(i + 1)..];
        if (cmd.Length == 0)
        {
            Console.Error.WriteLine("usage: lviz run [--id <name>] -- <command> [args…]");
            return 1;
        }

        id ??= Path.GetFileNameWithoutExtension(cmd[0]);

        int exitCode;
        try
        {
            var psi = new ProcessStartInfo { FileName = cmd[0], UseShellExecute = false };
            foreach (var a in cmd[1..]) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("process did not start");
            await proc.WaitForExitAsync().ConfigureAwait(false);
            exitCode = proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"lviz run: could not run '{cmd[0]}': {ex.Message}");
            exitCode = 1;
        }

        // Fire-and-forget the outcome event; the command's own exit code is what the
        // caller cares about, so we surface that, not the ack.
        var eventId = $"{id}:{(exitCode == 0 ? "ok" : "fail")}";
        await SendAsync($"event {eventId}", echoAck: false).ConfigureAwait(false);
        return exitCode;
    }

    // Connect, write one line, print the response, return 0 when the first response
    // line is "ok". On no running app, print the canonical message and exit nonzero.
    private static async Task<int> SendAsync(string line, bool echoAck = true)
    {
        using var pipe = new NamedPipeClientStream(".", LvizIpcServer.PipeName, PipeDirection.InOut);
        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("LViz isn't running.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"LViz isn't reachable: {ex.Message}");
            return 1;
        }

        // Not wrapped in `using`: the server closes its end right after replying, so
        // disposing the StreamWriter here would flush into an already-closed pipe and
        // throw. The pipe `using` owns the only real resource; the writer/reader are
        // thin buffers over it.
        var writer = new StreamWriter(pipe) { AutoFlush = true };
        var reader = new StreamReader(pipe);
        await writer.WriteLineAsync(line).ConfigureAwait(false);
        var response = (await reader.ReadToEndAsync().ConfigureAwait(false)).TrimEnd();

        var ok = response.StartsWith("ok", StringComparison.Ordinal);
        if (echoAck)
        {
            // For ok responses with a payload (status/devices) print the payload, not
            // the leading "ok" line; for a bare "ok"/"err …" print it verbatim.
            var newline = response.IndexOf('\n');
            var toPrint = ok && newline >= 0 ? response[(newline + 1)..] : response;
            var sink = ok ? Console.Out : Console.Error;
            sink.WriteLine(toPrint);
        }
        return ok ? 0 : 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            lviz — control the running LViz app from the command line.

            Usage:
              lviz                         Launch LViz (the resident app).
              lviz event <id>              Fire an opaque host event (e.g. ci:ok); LViz
                                           runs the action pipeline bound to it.
              lviz run [--id <name>] -- <cmd> [args…]
                                           Run a command, then fire <name>:ok / <name>:fail
                                           off its exit code (name defaults to the command).
              lviz status                  Show whether LViz is running and connected devices.
              lviz devices                 List connected devices and their capabilities.
              lviz help                    Show this help.
            """);
    }
}
