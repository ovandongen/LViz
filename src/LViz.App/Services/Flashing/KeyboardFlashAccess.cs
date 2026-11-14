using HidApi;
using LViz.Core.Diagnostics;
using LViz.Core.Firmware;
using ZmkHidProtocol.Transport;

namespace LViz.App.Services.Flashing;

/// <summary>
/// How the connected keyboard (the central) is attached, as far as the flash
/// flow cares. The firmware accepts the bootloader command over either
/// transport but refuses it unless the central's USB HID is up, so what
/// counts is whether the central is plugged in with USB, not which interface
/// LViz happens to talk through.
/// </summary>
public enum KeyboardBus
{
    /// <summary>No keyboard is connected.</summary>
    None,

    /// <summary>
    /// The central is on USB: LViz talks to it over USB, or over Bluetooth
    /// while the same keyboard also shows a USB endpoint.
    /// </summary>
    Usb,

    /// <summary>Only on Bluetooth (no USB endpoint of this keyboard) — the firmware refuses <c>0xB0</c>.</summary>
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
    /// Whether the connected keyboard is on USB (see <see cref="KeyboardBus"/>:
    /// a central plugged in with USB counts even while LViz talks to it over
    /// Bluetooth); <see cref="KeyboardBus.None"/> when not connected.
    /// Re-evaluated on every read — the cable may come or go between two reads.
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
    /// The bus of the keyboard whose open hidapi path is <paramref name="path"/>,
    /// from one enumeration under <see cref="HidGlobalLock.Gate"/>; a failed
    /// enumeration is <see cref="KeyboardBus.Unknown"/>. See <see cref="BusFor"/>.
    /// </summary>
    private static KeyboardBus LookUpBus(string path)
    {
        try
        {
            List<HidEntry> entries;
            lock (HidGlobalLock.Gate)
            {
                entries = Hid.Enumerate()
                    .Select(d => new HidEntry(d.Path, d.VendorId, d.ProductId, d.ProductString, d.BusType))
                    .ToList();
            }

            return BusFor(path, entries);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Flash", $"hidapi bus lookup failed: {ex.Message}");
            return KeyboardBus.Unknown;
        }
    }

    /// <summary>One hidapi enumeration entry, as much as <see cref="BusFor"/> reads.</summary>
    internal sealed record HidEntry(string Path, ushort VendorId, ushort ProductId, string? Product, BusType Bus);

    /// <summary>
    /// The keyboard's bus from an enumeration: the open path's own entry on USB
    /// is <see cref="KeyboardBus.Usb"/>. On Bluetooth it is still USB when the
    /// same keyboard (same vendor and product id and product name) also shows a
    /// USB entry: a central that is paired and plugged in at once appears twice,
    /// and LViz may well talk to it over the Bluetooth one, while the firmware
    /// only asks whether its USB HID is up. No entry for the path, or another
    /// bus, is <see cref="KeyboardBus.Unknown"/>.
    /// </summary>
    internal static KeyboardBus BusFor(string path, IReadOnlyList<HidEntry> entries)
    {
        var open = entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.Ordinal));
        if (open is null)
            return KeyboardBus.Unknown;
        if (open.Bus == BusType.Usb)
            return KeyboardBus.Usb;

        var usbTwin = entries.Any(e =>
            e.Bus == BusType.Usb
            && e.VendorId == open.VendorId
            && e.ProductId == open.ProductId
            && string.Equals(e.Product, open.Product, StringComparison.Ordinal));
        if (usbTwin)
            return KeyboardBus.Usb;

        return open.Bus == BusType.Bluetooth ? KeyboardBus.Bluetooth : KeyboardBus.Unknown;
    }
}
