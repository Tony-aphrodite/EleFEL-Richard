using System.Windows;
using EleFEL.Core.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using MessageBox = System.Windows.MessageBox;

namespace EleFEL.App.Views;

public partial class LicenseActivationWindow : Window
{
    private readonly LicenseService _licenseService;

    /// <summary>True if a valid, active license was confirmed during this dialog.</summary>
    public bool LicenseAccepted { get; private set; }

    public LicenseActivationWindow(LicenseService licenseService)
    {
        InitializeComponent();
        _licenseService = licenseService;

        if (_licenseService.HasKey)
        {
            txtLicenseKey.Text = _licenseService.State.Key;

            if (_licenseService.State.LastStatus == "active" &&
                !string.IsNullOrWhiteSpace(_licenseService.State.ClientName))
            {
                pnlCurrent.Visibility = Visibility.Visible;
                txtCurrentName.Text = _licenseService.State.ClientName;
                txtCurrentExpires.Text = $"Expira: {_licenseService.State.ExpiresDisplay}";
            }
        }

        Loaded += (_, _) => txtLicenseKey.Focus();
    }

    private async void BtnActivate_Click(object sender, RoutedEventArgs e)
    {
        await TryActivateAsync();
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await TryActivateAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    private async Task TryActivateAsync()
    {
        var key = txtLicenseKey.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(key))
        {
            ShowStatus("Ingrese una llave de licencia.", isError: true);
            txtLicenseKey.Focus();
            return;
        }

        btnActivate.IsEnabled = false;
        txtLicenseKey.IsEnabled = false;
        ShowStatus("Verificando con el servidor...", isError: false);

        try
        {
            var result = await _licenseService.VerifyOnlineAsync(key);

            switch (result.Status)
            {
                case LicenseStatus.Active:
                    LicenseAccepted = true;
                    MessageBox.Show(
                        $"Licencia activada correctamente.\n\nCliente: {result.ClientName}\nExpira: {result.ExpiresDisplay}",
                        "EleFEL - Licencia activa",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    Close();
                    return;

                case LicenseStatus.Expired:
                    ShowStatus("La licencia ha expirado. Renueve su suscripción mensual para continuar.", isError: true);
                    break;

                case LicenseStatus.Inactive:
                    ShowStatus("La licencia está inactiva. Contacte al proveedor.", isError: true);
                    break;

                case LicenseStatus.NotFound:
                    ShowStatus("Llave no encontrada. Verifique que la haya escrito correctamente.", isError: true);
                    break;

                default:
                    ShowStatus(result.Message ?? "Error al verificar la licencia.", isError: true);
                    break;
            }
        }
        finally
        {
            btnActivate.IsEnabled = true;
            txtLicenseKey.IsEnabled = true;
            txtLicenseKey.Focus();
            txtLicenseKey.SelectAll();
        }
    }

    private void ShowStatus(string message, bool isError)
    {
        pnlStatus.Visibility = Visibility.Visible;
        pnlStatus.Background = new System.Windows.Media.SolidColorBrush(
            isError
                ? System.Windows.Media.Color.FromRgb(0xFE, 0xE2, 0xE2)
                : System.Windows.Media.Color.FromRgb(0xF1, 0xF5, 0xF9));
        txtStatus.Text = message;
    }
}
