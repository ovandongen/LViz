using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.Services.GitHub;
using LViz.App.Services.Hotkeys;
using LViz.App.ViewModels;
using LViz.App.Views;
using LViz.Core.Diagnostics;
using LViz.Core.Layout;
using LViz.Core.Settings;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;
// App.axaml.cs also imports Avalonia.Input, whose own KeyBinding (gesture)
// type collides; only the Core binding model is meant here.
using KeyBinding = LViz.Core.Models.KeyBinding;

namespace LViz.App;

public partial class App : Application
{
    private IGlobalHotkeyService? _hotkeyService;

    public override void Initialize()
    {
        // Register Font Awesome icon provider before XAML is loaded so any
        // <i:Icon> controls inside templates resolve fa-* identifiers.
        IconProvider.Current.Register<FontAwesomeIconProvider>();

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                DiagnosticLog.Error("Unhandled", $"AppDomain unhandled: {e.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                DiagnosticLog.Error("Unhandled", $"Unobserved task: {e.Exception}");
                e.SetObserved();
            };
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                DiagnosticLog.Error("Unhandled", $"UI dispatcher: {e.Exception}");
                e.Handled = true;
            };

            // Composition root. AppServices owns construction of every
            // service that the container can manage; per-service Exit
            // disposal handlers are gone — the provider disposes its
            // IDisposable singletons on the single Exit hook below.
            var services = AppServices.BuildProvider();
            desktop.Exit += (_, _) => services.Dispose();

            var settingsService = services.GetRequiredService<ISettingsService>();
            var initialSettings = settingsService.Load();
            Loc.Instance.SetCulture(initialSettings.Language);

            var envLevel = Environment.GetEnvironmentVariable("LVIZ_LOG_LEVEL");
            if (!string.IsNullOrEmpty(envLevel) && Enum.TryParse<LogLevel>(envLevel, true, out var envLogLevel))
                DiagnosticLog.SetMinimumLevel(envLogLevel);
            else if (Enum.TryParse<LogLevel>(initialSettings.LogLevel, true, out var logLevel))
                DiagnosticLog.SetMinimumLevel(logLevel);

            // User layouts must be in the registry before any view-model
            // resolves a saved keyboard id or builds a picker list.
            var userLayouts = UserLayoutStore.LoadAll(UserLayoutStore.DefaultDirectory);
            KeyboardProfileRegistry.SetUserProfiles(userLayouts.Select(f => new UserLayoutProfile(f)));
            DiagnosticLog.Info("Layouts", $"Loaded {userLayouts.Count} user layout(s)");

            DiagnosticLog.Info("Startup", "Creating MainWindowViewModel...");
            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            var mainWindow = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = mainWindow;

            // Restore saved window position/size (or center on first launch)
            if (initialSettings.WindowX.HasValue && initialSettings.WindowY.HasValue)
            {
                mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                mainWindow.Position = new PixelPoint((int)initialSettings.WindowX.Value, (int)initialSettings.WindowY.Value);
            }
            else
            {
                mainWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            if (initialSettings.WindowWidth.HasValue)
                mainWindow.Width = initialSettings.WindowWidth.Value;
            if (initialSettings.WindowHeight.HasValue)
                mainWindow.Height = initialSettings.WindowHeight.Value;

            DiagnosticLog.Info("Startup", $"Window position: {mainWindow.Position.X},{mainWindow.Position.Y} size: {mainWindow.Width}x{mainWindow.Height}");

            // Validate restored position is on a visible screen
            mainWindow.Opened += (_, _) =>
            {
                if (mainWindow.Screens.ScreenFromWindow(mainWindow) is null)
                    mainWindow.Position = new PixelPoint(0, 0);
            };

            void SaveWindowState()
            {
                if (mainWindow.WindowState != WindowState.Minimized)
                {
                    var s = settingsService.Load();
                    settingsService.Save(s with
                    {
                        WindowX = mainWindow.Position.X,
                        WindowY = mainWindow.Position.Y,
                        WindowWidth = mainWindow.Width,
                        WindowHeight = mainWindow.Height,
                    });
                }
            }

            var closingHandled = false;

            // Quit guard: unsaved keymap edits are confirmed before anything
            // shuts down. With no edits both quit paths below run exactly as
            // before (save window state, shut down, close/exit).
            async Task<bool> ConfirmDiscardEditsAsync()
            {
                if (viewModel.UnsavedEditCount == 0) return true;
                // The global hotkey can hide the window while the user keeps
                // editing, and a modal dialog on a hidden owner never shows.
                mainWindow.Show();
                mainWindow.Activate();
                var dlg = new ChoiceDialog
                {
                    DataContext = ChoiceDialogViewModel.QuitWithUnsavedEdits(viewModel.UnsavedEditCount),
                };
                var result = await dlg.ShowDialog<string?>(mainWindow);
                return result == "quit";
            }

            mainWindow.Closing += (_, e) =>
            {
                if (closingHandled) return;
                e.Cancel = true;
                Dispatcher.UIThread.Post(async () =>
                {
                    if (!await ConfirmDiscardEditsAsync()) return;
                    closingHandled = true;
                    SaveWindowState();
                    viewModel.Shutdown();
                    mainWindow.Close();
                });
            };

            viewModel.QuitRequested = () => Dispatcher.UIThread.Post(async () =>
            {
                if (!await ConfirmDiscardEditsAsync()) return;
                SaveWindowState();
                viewModel.Shutdown();
                Environment.Exit(0);
            });

            // Tray icon: localize menu headers + pipe clicks to the VM
            var trayIcons = TrayIcon.GetIcons(this);
            if (trayIcons?.Count > 0)
            {
                var trayIcon = trayIcons[0];
                var trayTinter = new TrayIconTinter();

                void ApplyTrayIcon()
                {
                    var icon = viewModel.ColorTrayIconByActiveLayer
                        ? trayTinter.GetTinted(viewModel.ActiveLayerTintColor)
                        : trayTinter.GetOriginal();
                    trayIcon.Icon = icon;
                    // Mirror onto the main window so the Windows taskbar entry
                    // tracks too. macOS Dock ignores Window.Icon (bundle-bound).
                    mainWindow.Icon = icon;
                }

                ApplyTrayIcon();
                viewModel.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(MainWindowViewModel.ActiveLayerTintColor)
                        or nameof(MainWindowViewModel.ColorTrayIconByActiveLayer)
                        or nameof(MainWindowViewModel.SelectedKeyboard))
                    {
                        Dispatcher.UIThread.Post(ApplyTrayIcon);
                    }
                };

                LocalizeTrayMenu(trayIcon);
                // Named delegate so we can detach on Exit. Without the -=, every
                // runtime culture switch leaks the prior handler's tray-icon
                // capture and re-localizes already-collected closures.
                Action onCultureChanged = () =>
                    Dispatcher.UIThread.Post(() => LocalizeTrayMenu(trayIcon));
                Loc.CultureChanged += onCultureChanged;
                desktop.Exit += (_, _) => Loc.CultureChanged -= onCultureChanged;
            }

            viewModel.ShowWindowRequested = () =>
            {
                mainWindow.Show();
                mainWindow.Activate();
                if (mainWindow.WindowState == WindowState.Minimized)
                    mainWindow.WindowState = WindowState.Normal;
            };

            viewModel.ToggleWindowRequested = () =>
            {
                if (mainWindow.IsVisible)
                {
                    mainWindow.Hide();
                }
                else
                {
                    mainWindow.Show();
                    mainWindow.Activate();
                    if (mainWindow.WindowState == WindowState.Minimized)
                        mainWindow.WindowState = WindowState.Normal;
                }
            };

            viewModel.LoadLayoutRequested = async () =>
            {
                var file = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = Loc.Instance["LoadDialog_Title"],
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType(Loc.Instance["LoadDialog_FileType"])
                        {
                            // .keymap has no registered UTI on macOS, so a
                            // bare extension filter greys the file out. Tell
                            // the OS to treat it as plain text — the file is.
                            Patterns = ["*.keymap"],
                            MimeTypes = ["text/plain"],
                            AppleUniformTypeIdentifiers = ["public.plain-text"],
                        },
                        // Moergo Layout Editor exports — accepted only on the
                        // Glove80 / GO60 profiles (gated in KeymapStateService).
                        new FilePickerFileType(Loc.Instance["LoadDialog_FileTypeMoergoJson"])
                        {
                            Patterns = ["*.json"],
                            MimeTypes = ["application/json"],
                            AppleUniformTypeIdentifiers = ["public.json"],
                        },
                        FilePickerFileTypes.All,
                    ],
                });
                if (file.Count > 0)
                    viewModel.LoadLayoutFromPath(file[0].Path.LocalPath);
            };

            // Single non-modal Settings window. Re-clicking the toolbar button
            // brings the existing window to front rather than spawning a new one.
            SettingsWindow? settingsWindow = null;
            viewModel.OpenSettingsRequested = () =>
            {
                if (settingsWindow is { } existing && existing.IsVisible)
                {
                    existing.Activate();
                    return;
                }
                var settingsVm = services.GetRequiredService<SettingsViewModel>();
                settingsWindow = new SettingsWindow
                {
                    DataContext = settingsVm,
                    Topmost = mainWindow.Topmost,
                };
                // Keep the settings window above the main window when the
                // user has the main window pinned, otherwise it opens behind
                // and looks lost.
                void SyncTopmost(object? _, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(MainWindowViewModel.IsAlwaysOnTop) && settingsWindow is { } w)
                        w.Topmost = viewModel.IsAlwaysOnTop;
                }
                viewModel.PropertyChanged += SyncTopmost;
                settingsWindow.Closed += (_, _) =>
                {
                    viewModel.PropertyChanged -= SyncTopmost;
                    settingsVm.Dispose();
                    settingsWindow = null;
                };
                settingsWindow.Show(mainWindow);
            };

            viewModel.EditKeyLabelRequested = (keyIndex, layerIndex) =>
            {
                var ctx = viewModel.GetKeyLabelEditContext(layerIndex, keyIndex);
                if (ctx is null) return;
                var dlgVm = new EditKeyLabelDialogViewModel(
                    layerIndex, keyIndex, ctx.Value.DefaultHint, ctx.Value.Existing);
                var dlg = new EditKeyLabelDialog { DataContext = dlgVm };
                Dispatcher.UIThread.Post(async () =>
                {
                    var result = await dlg.ShowDialog<EditKeyLabelDialog.Result>(mainWindow);
                    if (result.Saved)
                        viewModel.SetKeyLabelOverride(layerIndex, keyIndex, result.Value);
                });
            };

            viewModel.EditComboLabelRequested = (keyPositionsKey) =>
            {
                var ctx = viewModel.GetComboLabelEditContext(keyPositionsKey);
                if (ctx is null) return;
                var dlgVm = new EditComboLabelDialogViewModel(
                    ctx.Value.Number, keyPositionsKey,
                    ctx.Value.DefaultLabel, ctx.Value.ParticipatingKeys,
                    ctx.Value.Existing);
                var dlg = new EditComboLabelDialog { DataContext = dlgVm };
                Dispatcher.UIThread.Post(async () =>
                {
                    var result = await dlg.ShowDialog<EditComboLabelDialog.Result>(mainWindow);
                    if (result.Saved)
                        viewModel.SetComboLabelOverride(keyPositionsKey, result.Value);
                });
            };

            // Review-before-save: modal, awaited by the save command, so the
            // save only continues when the user approved the listed changes,
            // with the commit message as the user left it.
            viewModel.ReviewChangesRequested = async request =>
            {
                var dlg = new KeymapReviewDialog { DataContext = new KeymapReviewDialogViewModel(request) };
                return await dlg.ShowDialog<KeymapReviewAnswer?>(mainWindow);
            };

            // Key dialog: modal, awaited by the open-key command, which
            // applies the answered binding as one undo step. A null answer
            // (Esc, Cancel, close) changes nothing.
            viewModel.KeyDialogRequested = async request =>
            {
                var dlg = new KeyDialog { DataContext = new KeyDialogViewModel(request) };
                return await dlg.ShowDialog<KeyBinding?>(mainWindow);
            };

            // Key ring: hosted by the main window as an overlay (a dialog
            // fallback while the window is too small); same lifecycle as the
            // key dialog — it answers the new binding, or null on cancel.
            viewModel.KeyRingRequested = request => mainWindow.ShowKeyRingAsync(request);

            // Save conflict: the target changed underneath the edit. Modal,
            // awaited by the save command, which applies the answer (reload /
            // overwrite / keep editing) directly.
            viewModel.SaveConflictRequested = async request =>
            {
                var dlg = new ChoiceDialog { DataContext = ChoiceDialogViewModel.Conflict(request) };
                return ChoiceDialogViewModel.ToConflictChoice(await dlg.ShowDialog<string?>(mainWindow));
            };

            // Rename layer: modal, awaited by the rename command, which
            // applies the answered name. A null answer (Esc, Cancel, close)
            // keeps the current name.
            viewModel.RenameLayerRequested = async request =>
            {
                var dlg = new RenameLayerDialog { DataContext = new RenameLayerDialogViewModel(request) };
                return await dlg.ShowDialog<string?>(mainWindow);
            };

            // Blocked layer delete: modal notice listing every reason the
            // delete was refused; the user just acknowledges it.
            viewModel.LayerDeleteBlockedRequested = async request =>
            {
                var dlg = new ChoiceDialog { DataContext = ChoiceDialogViewModel.LayerDeleteBlocked(request) };
                await dlg.ShowDialog<string?>(mainWindow);
            };

            // After-save report about layer settings the structural save
            // moved or dropped; the lines are already translated, the user
            // just acknowledges them.
            viewModel.LayerSettingsNoticeRequested = async lines =>
            {
                var dlg = new ChoiceDialog { DataContext = ChoiceDialogViewModel.LayerSettingsNotice(lines) };
                await dlg.ShowDialog<string?>(mainWindow);
            };

            // Layout wizard: modal, closes with null (cancel) or the saved
            // layout result, which is registered and selected right here.
            viewModel.AddOwnBoardRequested = () =>
            {
                var ctx = viewModel.GetLayoutWizardContext();
                var dlgVm = new LayoutWizardViewModel(ctx);
                // A GitHub keymap's repo is scanned straight away.
                _ = dlgVm.StartAsync();
                var dlg = new LayoutWizardWindow
                {
                    DataContext = dlgVm,
                    Topmost = mainWindow.Topmost,
                };
                Dispatcher.UIThread.Post(async () =>
                {
                    var result = await dlg.ShowDialog<LayoutWizardResult?>(mainWindow);
                    if (result is not null)
                        viewModel.ApplyLayoutWizardResult(result);
                });
            };

            // "Open from GitHub…": modal sign-in / repo / keymap picker. The
            // chosen keymap arrives as a cached file plus its github: entry,
            // loaded through the shared LoadLayoutFromPath path.
            viewModel.OpenFromGitHubRequested = () =>
            {
                var ghVm = new GitHubOpenViewModel(services.GetRequiredService<IGitHubKeymapService>())
                {
                    OpenUrl = url => services.GetRequiredService<IHostActionExecutor>().Launch(url),
                };
                var dlg = new GitHubOpenWindow
                {
                    DataContext = ghVm,
                    Topmost = mainWindow.Topmost,
                };
                Dispatcher.UIThread.Post(async () =>
                {
                    var result = await dlg.ShowDialog<GitHubOpenSelection?>(mainWindow);
                    if (result is not null)
                        viewModel.LoadLayoutFromPath(result.LocalPath, result.Keymap.ToString());
                });
            };

            // "Edit layout…": modal, closes with null (cancel) or the edited
            // file, which refreshes the board in place. Nothing to edit when
            // a bundled board is active.
            viewModel.EditLayoutRequested = () =>
            {
                var ctx = viewModel.GetLayoutEditContext();
                if (ctx is null)
                    return;
                var dlgVm = new LayoutEditWindowViewModel(ctx);
                var dlg = new LayoutEditWindow
                {
                    DataContext = dlgVm,
                    Topmost = mainWindow.Topmost,
                };
                Dispatcher.UIThread.Post(async () =>
                {
                    var result = await dlg.ShowDialog<UserLayoutFile?>(mainWindow);
                    if (result is not null)
                        viewModel.ApplyEditedLayout(result);
                });
            };

            viewModel.CopyDiagnosticsRequested = async () =>
            {
                try
                {
                    var report = DiagnosticLog.CollectDiagnosticReport(viewModel.BuildDiagnosticsSnapshot());
                    var clipboard = TopLevel.GetTopLevel(mainWindow)?.Clipboard ?? mainWindow.Clipboard;
                    if (clipboard is not null)
                    {
                        // SetTextAsync silently no-ops on Avalonia 11.3 macOS
                        // when invoked from a borderless transparent window.
                        // The newer SetDataAsync + DataTransfer path writes reliably.
                        var transfer = new DataTransfer();
                        transfer.Add(DataTransferItem.Create(DataFormat.Text, report));
                        await clipboard.SetDataAsync(transfer);
                        viewModel.StatusMessage = Loc.Instance["Status_DiagnosticsCopied"];
                    }
                }
                catch (Exception ex)
                {
                    viewModel.StatusMessage = $"Could not copy diagnostics: {ex.Message}";
                }
            };

            // Global show/hide hotkey. The registry's Linux stub returns
            // failure on TryRegister, so the service no-ops on Linux without
            // a special case here.
            _hotkeyService = services.GetRequiredService<IGlobalHotkeyService>();
            _hotkeyService.HotkeyPressed = () => viewModel.ToggleWindowRequested?.Invoke();
            _hotkeyService.UpdateHotkey(initialSettings.HotkeyKey, initialSettings.HotkeyModifiers);
            _hotkeyService.Start();
            viewModel.HotkeyKeyChanged += newKey =>
                _hotkeyService.UpdateHotkey(newKey, viewModel.HotkeyModifiers);

            // Restore the last-loaded layout (or show a "pick a file" prompt).
            Dispatcher.UIThread.Post(() => viewModel.InitializeAsync(), DispatcherPriority.Background);

            // One-shot background update check ~5s after launch. No-ops when
            // not running as a Velopack install (dev / dotnet run); silently
            // 404s while the source repo is private.
            if (initialSettings.AutoCheckForUpdates)
            {
                var updateService = services.GetRequiredService<UpdateService>();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5));
                        await updateService.CheckForUpdatesAsync();
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Warn("Update", $"Background check threw: {ex.Message}");
                    }
                });
            }

            DataContext = viewModel;
            DiagnosticLog.Info("Startup", "OnFrameworkInitializationCompleted done");
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static readonly (string Key, string ResKey)[] TrayMenuKeys =
    [
        ("Show", "Tray_ShowLayers"),
        ("Quit", "Tray_Quit"),
    ];

    private static void LocalizeTrayMenu(TrayIcon trayIcon)
    {
        trayIcon.ToolTipText = Loc.Instance["Tray_Tooltip"];
        if (trayIcon.Menu is not { } menu) return;

        var menuItems = menu.Items.OfType<NativeMenuItem>().ToList();
        for (var i = 0; i < menuItems.Count && i < TrayMenuKeys.Length; i++)
            menuItems[i].Header = Loc.Instance[TrayMenuKeys[i].ResKey];
    }
}
