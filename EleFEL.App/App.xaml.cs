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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Load configuration
        _configService = new ConfigService();
        var config = _configService.Load();

        // Initialize logging
        var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.System.LogDirectory);
        _logService = new LogService(logDir);
        _logService.LogInfo("EleFEL application starting");

        // Initialize services
        var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.System.DataDirectory);
        var invoiceDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.System.InvoiceDirectory);

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
        var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "EleFEL.ico");
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
        Dispatcher.Invoke(() =>
        {
            var window = new Views.NitInputWindow(sale, _engine!);
            window.ShowDialog();
        });
    }

    private void OpenInvoicesFolder()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            _configService?.Config.System.InvoiceDirectory ?? "Invoices");
        if (Directory.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", path);
    }

    private void OpenLogsFolder()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            _configService?.Config.System.LogDirectory ?? "Logs");
        if (Directory.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", path);
    }

    private async void ExitApplication()
    {
        if (_engine != null)
            await _engine.StopAsync();

        _trayIcon?.Dispose();
        _engine?.Dispose();
        _logService?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _engine?.Dispose();
        _logService?.Dispose();
        base.OnExit(e);
    }
}
