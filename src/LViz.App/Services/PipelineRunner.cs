using LViz.Core.Diagnostics;
using LViz.Core.Settings;
using ZmkHidProtocol.Capabilities;

namespace LViz.App.Services;

/// <summary>
/// Runs an <see cref="ActionPipeline"/>: its <see cref="PipelineStep"/>s in
/// order, awaiting each. Device steps resolve their target through the live
/// <see cref="ICapabilityRouter"/> by stable key and are skipped (logged) when
/// the device is absent — the rest of the pipeline still runs. A keyboard-layer
/// step with no target goes through the injected push delegate (the HID pipeline's
/// overlay-aware layer-push path); with a target it sends a <c>core.layer.*</c>
/// action to that device like the other device steps. Host steps go through
/// <see cref="IHostActionExecutor"/>.
///
/// <para>Runs are serialized through a single-flight gate so rapid back-to-back
/// triggers (e.g. a signal that flashes RGB on → delay → off) can't interleave
/// and scramble device state. The trigger source (signal dispatcher, Test
/// button) owns getting this off any hot read thread.</para>
/// </summary>
public interface IPipelineRunner
{
    Task RunAsync(ActionPipeline pipeline, CancellationToken cancellationToken = default);
}

public sealed class PipelineRunner : IPipelineRunner
{
    // Backstop against runaway nesting; the per-run cycle guard already bounds depth
    // to the number of distinct pipelines, so this only catches pathological graphs.
    private const int MaxDepth = 16;

    private readonly ICapabilityRouter _router;
    private readonly ICapabilityControl _control;
    private readonly IHostActionExecutor _host;
    private readonly Action<int> _pushKeyboardLayer;
    private readonly Func<IReadOnlyList<ActionPipeline>> _getLibrary;

    // Serializes whole pipeline runs — see the interface remarks. Nested sub-pipeline
    // runs reuse the holder's lease (RunPipelineAsync is gate-free), so the whole
    // tree runs as one atomic unit and can't self-deadlock.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PipelineRunner(
        ICapabilityRouter router,
        ICapabilityControl control,
        IHostActionExecutor host,
        Action<int> pushKeyboardLayer,
        Func<IReadOnlyList<ActionPipeline>> getLibrary)
    {
        _router = router;
        _control = control;
        _host = host;
        _pushKeyboardLayer = pushKeyboardLayer;
        _getLibrary = getLibrary;
    }

    public async Task RunAsync(ActionPipeline pipeline, CancellationToken cancellationToken = default)
    {
        if (pipeline.Steps.Count == 0) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Snapshot the library once so a mid-run edit can't change what a nested
            // sub-pipeline ref resolves to.
            var library = new Dictionary<string, ActionPipeline>(StringComparer.Ordinal);
            foreach (var p in _getLibrary()) library[p.Name] = p;

            await RunPipelineAsync(pipeline, library,
                new HashSet<string>(StringComparer.Ordinal), 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Runs one pipeline's steps. The active-chain set holds the pipelines currently on
    // the call stack; a sub-pipeline that's already in it is a cycle and is skipped.
    private async Task RunPipelineAsync(
        ActionPipeline pipeline, IReadOnlyDictionary<string, ActionPipeline> library,
        HashSet<string> activeChain, int depth, CancellationToken ct)
    {
        if (!activeChain.Add(pipeline.Name))
        {
            DiagnosticLog.Warn("Pipeline", $"cycle: '{pipeline.Name}' already running; skipping");
            return;
        }
        try
        {
            DiagnosticLog.Info("Pipeline", $"run '{pipeline.Name}' ({pipeline.Steps.Count} step(s))");
            for (var i = 0; i < pipeline.Steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await RunStepAsync(pipeline.Name, i, pipeline.Steps[i], library, activeChain, depth, ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            activeChain.Remove(pipeline.Name);
        }
    }

    private async Task RunSubPipelineAsync(
        PipelineStep step, IReadOnlyDictionary<string, ActionPipeline> library,
        HashSet<string> activeChain, int depth, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(step.PipelineRef)) return;
        if (depth + 1 > MaxDepth)
        {
            DiagnosticLog.Warn("Pipeline", $"max nesting depth reached at '{step.PipelineRef}'; skipping");
            return;
        }
        if (!library.TryGetValue(step.PipelineRef, out var sub))
        {
            DiagnosticLog.Info("Pipeline", $"sub-pipeline '{step.PipelineRef}' not found; skipping");
            return;
        }
        if (activeChain.Contains(sub.Name))
        {
            DiagnosticLog.Warn("Pipeline", $"cycle: '{sub.Name}' already running; skipping");
            return;
        }
        await RunPipelineAsync(sub, library, activeChain, depth + 1, ct).ConfigureAwait(false);
    }

    private async Task RunStepAsync(
        string pipelineName, int index, PipelineStep step,
        IReadOnlyDictionary<string, ActionPipeline> library,
        HashSet<string> activeChain, int depth, CancellationToken ct)
    {
        try
        {
            switch (step.Kind)
            {
                case PipelineStepKind.KeyboardLayer:
                    if (step.LayerIndex is { } layer)
                    {
                        if (string.IsNullOrWhiteSpace(step.TargetDeviceKey))
                            // Visualized keyboard: the overlay-aware SetLayerState push.
                            _pushKeyboardLayer(layer);
                        else
                            // A bus device that advertises a core.layer.* handle.
                            await RunDeviceStepAsync(step, $"layer.{step.LayerAction ?? PipelineLayerAction.SetBase}",
                                (device, c) => SendLayerAsync(device, step.LayerAction ?? PipelineLayerAction.SetBase, (byte)layer, c), ct)
                                .ConfigureAwait(false);
                    }
                    break;

                case PipelineStepKind.Rgb:
                    await RunDeviceStepAsync(step, "rgb.set",
                        (device, c) => _control.SendRgbAsync(device, step.Rgb ?? default, c), ct).ConfigureAwait(false);
                    break;

                case PipelineStepKind.Pointing:
                    if (step.PointingActionByte is { } actionByte)
                        await RunDeviceStepAsync(step, "pointing",
                            (device, c) => _control.SendPointingActionAsync(device, actionByte, step.PointingValue ?? 0u, c), ct)
                            .ConfigureAwait(false);
                    break;

                case PipelineStepKind.Launch:
                    if (!string.IsNullOrWhiteSpace(step.LaunchTarget))
                        _host.Launch(step.LaunchTarget);
                    break;

                case PipelineStepKind.Shell:
                    if (!string.IsNullOrWhiteSpace(step.ShellCommand))
                        _host.Shell(step.ShellCommand);
                    break;

                case PipelineStepKind.Delay:
                    if (step.DelayMs is { } ms && ms > 0)
                        await Task.Delay(ms, ct).ConfigureAwait(false);
                    break;

                case PipelineStepKind.Pipeline:
                    await RunSubPipelineAsync(step, library, activeChain, depth, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // run cancelled — stop the pipeline
        }
        catch (Exception ex)
        {
            // One bad step (range error, dead handle) shouldn't abort the rest.
            DiagnosticLog.Warn("Pipeline", $"'{pipelineName}' step {index} ({step.Kind}) failed: {ex.Message}");
        }
    }

    private Task<ControlResult> SendLayerAsync(ICapabilityDevice device, PipelineLayerAction action, byte layer, CancellationToken ct)
        => action switch
        {
            PipelineLayerAction.Activate => _control.ActivateLayerAsync(device, layer, ct),
            PipelineLayerAction.Deactivate => _control.DeactivateLayerAsync(device, layer, ct),
            _ => _control.SetLayerBaseAsync(device, layer, ct),
        };

    private async Task RunDeviceStepAsync(
        PipelineStep step, string what,
        Func<ICapabilityDevice, CancellationToken, Task<ControlResult>> send, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(step.TargetDeviceKey))
        {
            DiagnosticLog.Warn("Pipeline", $"{what} step has no target device; skipping");
            return;
        }

        var device = _router.FindDevice(step.TargetDeviceKey);
        if (device is null)
        {
            DiagnosticLog.Info("Pipeline", $"{what} target '{step.TargetDeviceKey}' absent; skipping step");
            return;
        }

        var result = await send(device, ct).ConfigureAwait(false);
        if (!result.Success)
            DiagnosticLog.Warn("Pipeline", $"{what} → {device.DisplayName}: {result.Outcome} {result.Detail}");
    }
}
