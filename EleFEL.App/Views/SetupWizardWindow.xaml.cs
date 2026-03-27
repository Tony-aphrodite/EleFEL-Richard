using System.Drawing.Printing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using EleFEL.Core.Models;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace EleFEL.App.Views;

public partial class SetupWizardWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public bool ConfigSaved { get; private set; }

    private readonly string _configPath;

    public SetupWizardWindow(string configPath, AppConfig? existingConfig = null)
    {
        InitializeComponent();
        _configPath = configPath;

        // Set defaults
        cmbTaxpayerType.SelectedIndex = 0; // PEQ
        cmbPaperWidth.SelectedIndex = 0;   // 58mm

        if (existingConfig != null)
            LoadExistingConfig(existingConfig);

        DetectPrinters();
    }

    private void LoadExistingConfig(AppConfig config)
    {
        // Emitter
        txtEmitterNit.Text = config.Emitter.Nit;
        txtEmitterName.Text = config.Emitter.Name;
        txtCommercialName.Text = config.Emitter.CommercialName;
        txtAddress.Text = config.Emitter.Address;
        txtMunicipality.Text = config.Emitter.Municipality;
        txtDepartment.Text = config.Emitter.Department;
        txtZipCode.Text = config.Emitter.ZipCode;

        // Taxpayer type
        cmbTaxpayerType.SelectedIndex = config.Emitter.TaxpayerType == "GEN" ? 1 : 0;

        // Infile
        txtCertUser.Text = config.Infile.CertUser;
        txtCertKey.Text = config.Infile.CertKey;
        txtSigningKey.Text = config.Infile.SigningKey;
        txtEmitterCode.Text = config.Infile.EmitterCode;
        txtCopyEmail.Text = config.Infile.CopyEmail;

        // Eleventa
        txtDatabasePath.Text = config.Eleventa.DatabasePath;

        // Printer
        cmbPrinter.Text = config.Printer.PrinterName;
        cmbPaperWidth.SelectedIndex = config.Printer.PaperWidthMm == 80 ? 1 : 0;
    }

    private void DetectPrinters()
    {
        try
        {
            var currentText = cmbPrinter.Text;
            cmbPrinter.Items.Clear();

            foreach (string printer in PrinterSettings.InstalledPrinters)
            {
                cmbPrinter.Items.Add(printer);
            }

            if (!string.IsNullOrEmpty(currentText))
                cmbPrinter.Text = currentText;
            else if (cmbPrinter.Items.Count > 0)
                cmbPrinter.SelectedIndex = 0;
        }
        catch
        {
            // Printers not available - user can type manually
        }
    }

    private void BtnBrowseDb_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar Base de Datos Eleventa",
            Filter = "Firebird Database (*.FDB)|*.FDB|Todos los archivos (*.*)|*.*",
            InitialDirectory = @"C:\Eleventa\Data"
        };

        if (dialog.ShowDialog() == true)
        {
            txtDatabasePath.Text = dialog.FileName;
        }
    }

    private void BtnDetectPrinters_Click(object sender, RoutedEventArgs e)
    {
        DetectPrinters();
        MessageBox.Show(
            $"Se detectaron {cmbPrinter.Items.Count} impresora(s).",
            "Detección de Impresoras",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        // Validate required fields
        if (string.IsNullOrWhiteSpace(txtEmitterNit.Text))
        {
            MessageBox.Show("El NIT del emisor es obligatorio.", "Validación",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            txtEmitterNit.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(txtEmitterName.Text))
        {
            MessageBox.Show("El nombre legal del emisor es obligatorio.", "Validación",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            txtEmitterName.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(txtDatabasePath.Text))
        {
            MessageBox.Show("La ruta de la base de datos Eleventa es obligatoria.", "Validación",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            txtDatabasePath.Focus();
            return;
        }

        // Get taxpayer type
        var taxpayerType = "PEQ";
        if (cmbTaxpayerType.SelectedItem is System.Windows.Controls.ComboBoxItem taxpayerItem)
            taxpayerType = taxpayerItem.Tag?.ToString() ?? "PEQ";

        // Get paper width
        var paperWidth = 58;
        if (cmbPaperWidth.SelectedItem is System.Windows.Controls.ComboBoxItem paperItem)
            int.TryParse(paperItem.Tag?.ToString(), out paperWidth);

        // Build config
        var config = new AppConfig
        {
            Eleventa = new EleventaConfig
            {
                DatabasePath = txtDatabasePath.Text.Trim(),
                PollingIntervalSeconds = 10
            },
            Infile = new InfileConfig
            {
                SigningKey = txtSigningKey.Text.Trim(),
                CertUser = txtCertUser.Text.Trim(),
                CertKey = txtCertKey.Text.Trim(),
                EmitterCode = txtEmitterCode.Text.Trim(),
                SigningAlias = txtEmitterCode.Text.Trim(),
                CopyEmail = txtCopyEmail.Text.Trim(),
                UseSandbox = true
            },
            Emitter = new EmitterConfig
            {
                Nit = txtEmitterNit.Text.Trim(),
                Name = txtEmitterName.Text.Trim().ToUpperInvariant(),
                CommercialName = string.IsNullOrWhiteSpace(txtCommercialName.Text)
                    ? txtEmitterName.Text.Trim().ToUpperInvariant()
                    : txtCommercialName.Text.Trim().ToUpperInvariant(),
                Address = txtAddress.Text.Trim().ToUpperInvariant(),
                ZipCode = txtZipCode.Text.Trim(),
                Municipality = txtMunicipality.Text.Trim().ToUpperInvariant(),
                Department = txtDepartment.Text.Trim().ToUpperInvariant(),
                Country = "GT",
                TaxpayerType = taxpayerType,
                CurrencyCode = "GTQ",
                EstablishmentCode = "1"
            },
            Printer = new PrinterConfig
            {
                PrinterName = cmbPrinter.Text.Trim(),
                PaperWidthMm = paperWidth,
                Enabled = !string.IsNullOrWhiteSpace(cmbPrinter.Text)
            },
            System = new SystemConfig
            {
                DataDirectory = "Data",
                LogDirectory = "Logs",
                InvoiceDirectory = "Invoices",
                QueueRetryIntervalSeconds = 60,
                MaxRetryAttempts = 10,
                PostponedExpirationDays = 3
            }
        };

        // Add frases based on taxpayer type
        if (taxpayerType == "PEQ")
        {
            config.Emitter.Frases.Add(new FraseConfig { TipoFrase = 4, CodigoEscenario = 1 });
        }
        else
        {
            config.Emitter.Frases.Add(new FraseConfig { TipoFrase = 1, CodigoEscenario = 1 });
        }

        try
        {
            var directory = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(_configPath, json);

            ConfigSaved = true;

            MessageBox.Show(
                "Configuración guardada exitosamente.\nEleFEL se iniciará ahora.",
                "EleFEL",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Error al guardar la configuración:\n{ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Si cancela, EleFEL no podrá funcionar sin configuración.\n¿Desea salir?",
            "Confirmar",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            ConfigSaved = false;
            Close();
        }
    }
}
