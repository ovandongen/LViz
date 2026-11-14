using HidApi;
using LViz.Core.Diagnostics;
using LViz.Core.Firmware;
using ZmkHidProtocol.Transport;

namespace LViz.App.Services.Flashing;

/// <summary>
/// How the connected keyboard is attached, as far as the flash flow cares:
/// the bootloader command is refused over Bluetooth, and flashing itself only
/// works when the board is on USB.
/// </summary>
public enum KeyboardBus
{
    /// <summary>No keyboard is connected.</summary>
    None,

    /// <summary>Connected over USB — flashing is possible.</summary>
    Usb,

    /// <summary>Connected over Bluetooth — the firmware refuses <c>0xB0</c>.</summary>
    Bluetooth,

    /// <summary>Connected, but hidapi can't tell (or found no entry for the open path).</summary>
    Unknown,
}

/// <summary>
/// Everything the flash flow may ask the keyboard LViz currently has open
/// before it sends the <c>0xB0 core.system.bootloader</c> command: whether a
/// board is connected at all, whether it is on USB, whether its firmware
/// advertises the command and for how many peripheral slots, and which config
/// ID it reports. The bus check lives here (not in the transport submodule) so
/// the flash logic can run against this interface with a fake.
/// </summary>
public interface IKeyboardFlashAccess
{
    /// <summary>True while the HID pipeline holds an open keyboard handle.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Transport of the connected keyboard; <see cref="KeyboardBus.None"/> when
    /// not connected. Re-evaluated on every read — the board may be replugged
    /// or switch to Bluetooth between two reads.
    /// </summary>
    KeyboardBus Bus { get; }

    /// <summary>
    /// Peripheral bootloader slots the connected keyboard's firmware advertises
    /// as <c>core.system.bootloader/&lt;n&gt;</c> in its manifest — null when the
    /// board isn't connected, has no manifest yet, or doesn't list the capability
    /// (firmware without <c>CONFIG_HID_VIZ_SYSTEM</c>).
    /// </summary>
    int? BootloaderPeripheralSlots { get; }

    /// <summary>
    /// Reboot the central (<see cref="BootloaderCommand.CentralTarget"/>) or
    /// peripheral slot <paramref name="targetHalf"/> into its UF2 bootloader.
    /// Fails with <c>"keyboard not connected"</c> without sending when no board
    /// is open; otherwise the confirm-flagged result of the capability control
    /// layer (Confirmed / TimedOut / Failed).
    /// </summary>
    Task<ControlResult> EnterBootloaderAsync(byte targetHalf, CancellationToken ct = default);

    /// <summary>
    /// The keyboard's config ID (the <c>0xFA</c> answer, 1 s budget), or null
    /// when it doesn't answer; failures other than caller cancellation are
    /// logged and reported as null.
    /// </summary>
    Task<string?> QueryConfigIdAsync(CancellationToken ct = default);
}

/// <summary>
/// <see cref="IKeyboardFlashAccess"/> over the live stack: connection and config
/// ID from the <see cref="IHidPipeline"/>, the bootloader capability from the
/// <see cref="ICapabilityRouter"/> inventory entry flagged
/// <see cref="RoutedDeviceInfo.IsKeyboard"/>, and the command itself through
/// <see cref="ICapabilityControl"/> onto a fresh
/// <see cref="KeyboardCapabilityDevice"/> adapter.
///
/// <para>The bus lookup enumerates hidapi devices (read-only — it never opens a
/// handle, the keyboard must never be opened twice) under
/// <see cref="HidGlobalLock.Gate"/>, the gate the transport's discovery loops
/// hold: two concurrent <see cref="Hid.Enumerate()"/> calls abort the process
/// on macOS.</para>
/// </summary>
public sealed class KeyboardFlashAccess(
    IHidPipeline hid,
    ICapabilityRouter router,
    ICapabilityControl control,
    Func<string, KeyboardBus>? busOfPath = null) : IKeyboardFlashAccess
{
    public bool IsConnected => hid.IsConnected;

    public KeyboardBus Bus
        => hid.IsConnected && hid.OpenDevicePath is { } path ? (busOfPath ?? LookUpBus)(path) : KeyboardBus.None;

    public int? BootloaderPeripheralSlots
        => hid.IsConnected && router.Devices.FirstOrDefault(d => d.IsKeyboard)?.Manifest is { } manifest
            ? BootloaderCommand.PeripheralSlots(manifest.Capabilities.Select(c => c.Id))
            : null;

    public Task<ControlResult> EnterBootloaderAsync(byte targetHalf, CancellationToken ct = default)
        => hid.IsConnected
            ? control.EnterBootloaderAsync(new KeyboardCapabilityDevice(hid), targetHalf, ct)
            : Task.FromResult(new ControlResult(ControlOutcome.Failed, "keyboard not connected"));

    public async Task<string?> QueryConfigIdAsync(CancellationToken ct = default)
    {
        try
        {
            return await hid.QueryConfigIdAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancelled — propagate, not a query failure
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Flash", $"config-id query failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// hidapi bus of the device whose path is <paramref name="path"/>: first
    /// matching enumerate entry, USB / Bluetooth mapped, everything else
    /// (I2C/Spi, no entry, a failed enumerate) → <see cref="KeyboardBus.Unknown"/>.
    /// </summary>
    private static KeyboardBus LookUpBus(string path)
    {
        try
        {
            List<DeviceInfo> devices;
            lock (HidGlobalLock.Gate) devices = Hid.Enumerate().ToList();
            return devices.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.Ordinal))?.BusType switch
            {
                BusType.Usb => KeyboardBus.Usb,
                BusType.Bluetooth => KeyboardBus.Bluetooth,
                _ => KeyboardBus.Unknown,
            };
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Flash", $"hidapi bus lookup failed: {ex.Message}");
            return KeyboardBus.Unknown;
        }
    }
}
