using System.IO;
using System.Windows;
using EleFEL.Core.Models;
using EleFEL.Core.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace EleFEL.App;

public partial class App : Application
{
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private EleFelEngine? _engine;
    private ConfigService? _configService;
    private LogService? _logService;
    private LicenseService? _licenseService;
    private System.Threading.Timer? _licenseRecheckTimer;
    private bool _nitWindowOpen;

    /// <summary>
    /// Returns the directory where EleFEL.App.exe is located (not the temp extraction dir).
    /// Critical for single-file publish: AppDirectory points to temp dir.
    /// </summary>
    private static string AppDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Load configuration - use exe directory, not temp extraction dir
        var configPath = Path.Combine(AppDirectory, "config.json");
        _configService = new ConfigService(configPath);
        var needsSetup = !File.Exists(configPath);

        var config = _configService.Load();

        // Check if config exists but emitter is not configured
        if (!needsSetup && string.IsNullOrWhiteSpace(config.Emitter.Nit))
            needsSetup = true;

        // Initialize logging first — needed by the license service
        var logDir = Path.Combine(AppDirectory, config.System.LogDirectory);
        _logService = new LogService(logDir);
        _logService.LogInfo("EleFEL application starting");

        // License verification — must pass before setup wizard or engine
        var licensePath = Path.Combine(AppDirectory, "license.json");
        _licenseService = new LicenseService(licensePath, _logService);
        if (!await EnsureLicenseAsync())
        {
            Shutdown();
            return;
        }
        StartLicenseRecheckTimer();

        // Show setup wizard if needed (only after license is validated)
        if (needsSetup)
        {
            var wizard = new Views.SetupWizardWindow(configPath, needsSetup ? null : config);
            wizard.ShowDialog();

            if (!wizard.ConfigSaved)
            {
                Shutdown();
                return;
            }

            // Reload config after wizard saved it
            config = _configService.Load();
        }

        _logService.LogInfo($"Config loaded: TaxpayerType={config.Emitter.TaxpayerType}, Frases.Count={config.Emitter.Frases.Count}, CertUrl={config.Infile.CertificationUrl}");
        foreach (var fr in config.Emitter.Frases)
            _logService.LogInfo($"  Frase: TipoFrase={fr.TipoFrase}, CodigoEscenario={fr.CodigoEscenario}");

        // Initialize services
        var dataDir = Path.Combine(AppDirectory, config.System.DataDirectory);
        var invoiceDir = Path.Combine(AppDirectory, config.System.InvoiceDirectory);

        var db = new LocalDatabaseService(dataDir);
        var polling = new EleventaPollingService(config.Eleventa, _logService);
        var xmlGenerator = new XmlDteGenerator(config.Emitter);
        var certifier = new InfileCertifier(config.Infile, config.Emitter, _logService);
        var fileService = new InvoiceFileService(invoiceDir, _logService, config.Emitter);
        var queue = new InvoiceQueueService(db, certifier, fileService, _logService, config.System.MaxRetryAttempts);
        var printer = new ThermalPrinterService(config.Printer, _logService);

        _engine = new EleFelEngine(config, polling, db, xmlGenerator, queue, printer, _logService, certifier);

        // Setup system tray
        SetupTrayIcon();

        // Wire up events
        _engine.OnStatusChanged += status => UpdateTrayIcon(status);
        _engine.OnNewSaleRequiresNit += sale => ShowNitWindow(sale);
        _engine.OnPendingCountChanged += count => UpdatePendingCount(count);
        _engine.OnError += msg => _logService.LogError(msg);

        // Start engine
        try
        {
            await _engine.StartAsync();
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to start engine", ex);
            MessageBox.Show(
                $"Error starting EleFEL:\n{ex.Message}\n\nCheck the log files for details.",
                "EleFEL - Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SetupTrayIcon()
    {
        // Load icon from Assets folder
        var iconPath = System.IO.Path.Combine(AppDirectory, "Assets", "EleFEL.ico");
        System.Drawing.Icon? appIcon = null;
        if (System.IO.File.Exists(iconPath))
        {
            appIcon = new System.Drawing.Icon(iconPath);
        }
        else
        {
            // Fallback: use default application icon
            appIcon = System.Drawing.SystemIcons.Application;
        }

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "EleFEL - Conector FEL Guatemala",
            Icon = appIcon,
            Visible = true
        };

        // Create context menu
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Estado: Iniciando...", null, null);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Ventas Pendientes de Facturar", null, (_, _) => ShowPostponedList());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Abrir Carpeta de Facturas", null, (_, _) => OpenInvoicesFolder());
        menu.Items.Add("Abrir Carpeta de Logs", null, (_, _) => OpenLogsFolder());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Configuración", null, (_, _) => ShowSetupWizard());
        menu.Items.Add("Licencia", null, (_, _) => ShowLicenseWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => ExitApplication());

        _trayIcon.ContextMenuStrip = menu;
        UpdateTrayIcon(EngineStatus.Stopped);
    }

    private void UpdateTrayIcon(EngineStatus status)
    {
        if (_trayIcon == null) return;

        Dispatcher.Invoke(() =>
        {
            switch (status)
            {
                case EngineStatus.Running:
                    _trayIcon.Text = "EleFEL - Activo";
                    // Green icon would be set here with actual .ico file
                    break;
                case EngineStatus.Error:
                    _trayIcon.Text = "EleFEL - Error";
                    break;
                case EngineStatus.Stopped:
                    _trayIcon.Text = "EleFEL - Detenido";
                    break;
            }

            if (_trayIcon.ContextMenuStrip?.Items.Count > 0)
            {
                _trayIcon.ContextMenuStrip.Items[0].Text = $"Estado: {status}";
            }
        });
    }

    private void UpdatePendingCount(int count)
    {
        Dispatcher.Invoke(() =>
        {
            if (_trayIcon == null) return;

            if (count > 0)
            {
                _trayIcon.Text = $"EleFEL - {count} factura(s) pendiente(s)";
                _trayIcon.ShowBalloonTip(3000, "EleFEL",
                    $"{count} factura(s) pendiente(s) de certificación",
                    System.Windows.Forms.ToolTipIcon.Warning);
            }
            else
            {
                _trayIcon.Text = "EleFEL - Activo";
            }
        });
    }

    private void ShowPostponedList()
    {
        Dispatcher.Invoke(() =>
        {
            var window = new Views.PostponedListWindow(_engine!);
            window.ShowDialog();
        });
    }

    private void ShowNitWindow(EleventaSale sale)
    {
        // Set flag BEFORE BeginInvoke to prevent race condition
        // where polling fires multiple events before UI thread processes the first
        if (_nitWindowOpen) return;
        _nitWindowOpen = true;

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var window = new Views.NitInputWindow(sale, _engine!);
                window.Closed += (_, _) => _nitWindowOpen = false;
                window.ShowDialog();
            }
            catch
            {
                _nitWindowOpen = false;
            }
        });
    }

    private async Task<bool> EnsureLicenseAsync()
    {
        if (_licenseService == null) return false;

        var result = await _licenseService.CheckOnStartupAsync();

        if (result.IsAllowed)
        {
            if (result.Status == LicenseStatus.OfflineGrace)
            {
                _logService?.LogWarning("License: running in offline grace period.");
            }
            else
            {
                _logService?.LogInfo($"License active: {result.ClientName} (expira {result.ExpiresDisplay})");
            }
            return true;
        }

        // Not allowed — show activation dialog so the user can enter / re-enter a key.
        // The loop gives them a chance to retry or cancel explicitly.
        while (true)
        {
            var preMessage = result.Message;
            if (!string.IsNullOrEmpty(preMessage))
                _logService?.LogWarning($"License: {preMessage}");

            var window = new Views.LicenseActivationWindow(_licenseService);
            if (!string.IsNullOrEmpty(preMessage))
            {
                MessageBox.Show(
                    preMessage,
                    "EleFEL - Licencia",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            window.ShowDialog();

            if (window.LicenseAccepted)
                return true;

            // User closed the dialog without activating. Confirm exit.
            var choice = MessageBox.Show(
                "EleFEL no puede iniciarse sin una licencia activa.\n\n¿Desea intentar de nuevo?",
                "EleFEL - Licencia requerida",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (choice != MessageBoxResult.Yes)
                return false;

            // Re-evaluate in case the user fixed connectivity — next loop iteration tries again.
            result = await _licenseService.CheckOnStartupAsync();
            if (result.IsAllowed)
                return true;
        }
    }

    private void StartLicenseRecheckTimer()
    {
        // Silently re-verify once per 24h so the cache stays fresh.
        _licenseRecheckTimer = new System.Threading.Timer(async _ =>
        {
            if (_licenseService == null) return;
            try
            {
                var result = await _licenseService.CheckOnStartupAsync();
                if (!result.IsAllowed)
                {
                    _logService?.LogWarning($"License recheck failed: {result.Status} - {result.Message}");
                    Dispatcher.Invoke(() =>
                    {
                        _trayIcon?.ShowBalloonTip(
                            5000,
                            "EleFEL - Licencia",
                            result.Message ?? "La licencia requiere atención.",
                            System.Windows.Forms.ToolTipIcon.Warning);
                    });
                }
            }
            catch (Exception ex)
            {
                _logService?.LogError("License recheck error", ex);
            }
        }, null, TimeSpan.FromHours(24), TimeSpan.FromHours(24));
    }

    private void ShowLicenseWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (_licenseService == null) return;
            var window = new Views.LicenseActivationWindow(_licenseService);
            window.ShowDialog();
        });
    }

    private void ShowSetupWizard()
    {
        Dispatcher.Invoke(() =>
        {
            var configPath = Path.Combine(AppDirectory, "config.json");
            var wizard = new Views.SetupWizardWindow(configPath, _configService?.Config);
            wizard.ShowDialog();

            if (wizard.ConfigSaved)
            {
                MessageBox.Show(
                    "La configuración se ha actualizado.\nReinicie EleFEL para aplicar los cambios.",
                    "EleFEL",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        });
    }

    private void OpenInvoicesFolder()
    {
        var path = Path.Combine(AppDirectory,
            _configService?.Config.System.InvoiceDirectory ?? "Invoices");
        if (Directory.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", path);
    }

    private void OpenLogsFolder()
    {
        var path = Path.Combine(AppDirectory,
            _configService?.Config.System.LogDirectory ?? "Logs");
        if (Directory.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", path);
    }

    private async void ExitApplication()
    {
        if (_engine != null)
            await _engine.StopAsync();

        _licenseRecheckTimer?.Dispose();
        _trayIcon?.Dispose();
        _engine?.Dispose();
        _logService?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _licenseRecheckTimer?.Dispose();
        _trayIcon?.Dispose();
        _engine?.Dispose();
        _logService?.Dispose();
        base.OnExit(e);
    }
}
