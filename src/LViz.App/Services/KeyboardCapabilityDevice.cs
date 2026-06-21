using ZmkHidProtocol.Capabilities;

namespace LViz.App.Services;

/// <summary>
/// Adapts the app's already-open keyboard <see cref="IHidPipeline"/> to the
/// capability layer's <see cref="ICapabilityDevice"/>, so the router can talk to
/// the keyboard uniformly alongside bus devices — without ever opening the
/// keyboard's hidapi handle a second time (the headline coexistence constraint:
/// a second handle on the same physical device corrupts reads).
///
/// <para>I/O + transport identity only: report passthrough and report writes ride
/// the pipeline's existing source/sink, so the adapter follows the keyboard
/// across reconnects and profile switches without re-subscribing. The
/// <em>stable</em> device key used for persistence is derived by the router from
/// the discovered <see cref="DeviceManifest"/>, not here.</para>
/// </summary>
public sealed class KeyboardCapabilityDevice(IHidPipeline pipeline) : ICapabilityDevice
{
    /// <summary>
    /// Stable transport identity for the one keyboard pipeline. A constant, not a
    /// path: unlike a bus device (one short-lived hidapi handle), the pipeline
    /// outlives any single handle and reconnects under the hood, so a path-based
    /// key would churn. The bus already excludes the keyboard, so this never
    /// collides with a bus device's <c>vid:pid:path</c> key.
    /// </summary>
    public string TransportKey => "keyboard";

    public string DisplayName
    {
        get
        {
            var label = pipeline.SourceLabel;
            return string.IsNullOrEmpty(label) ? "Keyboard" : label;
        }
    }

    public event Action<ReadOnlyMemory<byte>>? ReportReceived
    {
        add => pipeline.ReportReceived += value;
        remove => pipeline.ReportReceived -= value;
    }

    public ValueTask SendReportAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
        => pipeline.SendReportAsync(report, cancellationToken);
}
