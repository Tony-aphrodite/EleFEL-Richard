using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using EleFEL.Core.Models;
using EleFEL.Core.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace EleFEL.App.Views;

public partial class NitInputWindow : Window
{
    private readonly EleventaSale _sale;
    private readonly EleFelEngine _engine;
    private Customer? _selectedCustomer;
    private readonly DispatcherTimer _searchDebounce;
    private bool _actionTaken;

    public NitInputWindow(EleventaSale sale, EleFelEngine engine)
    {
        InitializeComponent();
        _sale = sale;
        _engine = engine;
        _actionTaken = false;

        // Debounce timer to avoid searching on every keystroke
        _searchDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _searchDebounce.Tick += SearchDebounce_Tick;

        // When user closes window with X button, auto-postpone
        Closing += NitInputWindow_Closing;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        txtSaleInfo.Text = $"Venta #{_sale.SaleId}  |  Total: Q{_sale.Total:N2}  |  {_sale.SaleDate:dd/MM/yyyy HH:mm}";
        txtNit.Focus();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (btnConfirm.IsEnabled)
                    BtnConfirm_Click(sender, e);
                break;
            case Key.Escape:
                BtnCancel_Click(sender, e);
                break;
            case Key.F2:
                BtnCF_Click(sender, e);
                break;
            case Key.F3:
                BtnPostpone_Click(sender, e);
                break;
        }
    }

    private void TxtNit_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var nit = txtNit.Text.Trim();
        _selectedCustomer = null;

        if (string.IsNullOrEmpty(nit))
        {
            txtCustomerName.Text = string.Empty;
            txtStatus.Text = "Ingrese NIT o presione F2 para Consumidor Final";
            txtStatus.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("TextSecondaryBrush");
            lstCustomers.Visibility = Visibility.Collapsed;
            btnConfirm.IsEnabled = false;
            return;
        }

        // Validate NIT format
        if (!IsValidNitFormat(nit))
        {
            txtStatus.Text = "Formato de NIT no valido";
            txtStatus.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("ErrorBrush");
            lstCustomers.Visibility = Visibility.Collapsed;
            btnConfirm.IsEnabled = false;
            return;
        }

        btnConfirm.IsEnabled = true;

        // Restart debounce timer for customer search
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private async void SearchDebounce_Tick(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();

        var nit = txtNit.Text.Trim();
        if (string.IsNullOrEmpty(nit) || nit.Length < 2) return;

        try
        {
            // First try exact match
            var exactMatch = await _engine.GetCustomerByNitAsync(nit);
            if (exactMatch != null)
            {
                _selectedCustomer = exactMatch;
                txtCustomerName.Text = exactMatch.Name;
                lstCustomers.Visibility = Visibility.Collapsed;
                txtStatus.Text = "Cliente encontrado";
                txtStatus.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("SuccessBrush");
                return;
            }

            // Then try partial search
            var results = await _engine.SearchCustomersAsync(nit);
            if (results.Count > 0)
            {
                lstCustomers.ItemsSource = results;
                lstCustomers.Visibility = Visibility.Visible;
                txtStatus.Text = $"{results.Count} cliente(s) encontrado(s)";
                txtStatus.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("TextSecondaryBrush");
            }
            else
            {
                lstCustomers.Visibility = Visibility.Collapsed;
                txtStatus.Text = "Cliente nuevo - ingrese el nombre";
                txtStatus.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("TextSecondaryBrush");
                txtCustomerName.Text = string.Empty;
            }
        }
        catch
        {
            lstCustomers.Visibility = Visibility.Collapsed;
            txtStatus.Text = string.Empty;
        }
    }

    private void LstCustomers_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (lstCustomers.SelectedItem is Customer customer)
        {
            _selectedCustomer = customer;
            txtNit.TextChanged -= TxtNit_TextChanged; // Prevent re-triggering search
            txtNit.Text = customer.Nit;
            txtNit.TextChanged += TxtNit_TextChanged;
            txtCustomerName.Text = customer.Name;
            lstCustomers.Visibility = Visibility.Collapsed;
            txtStatus.Text = "Cliente seleccionado";
            txtStatus.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("SuccessBrush");
            btnConfirm.IsEnabled = true;
        }
    }

    private async void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        _actionTaken = true;
        var nit = txtNit.Text.Trim();
        var name = txtCustomerName.Text.Trim();

        if (string.IsNullOrEmpty(nit))
        {
            txtStatus.Text = "Ingrese el NIT del cliente";
            txtStatus.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("ErrorBrush");
            txtNit.Focus();
            return;
        }

        if (string.IsNullOrEmpty(name))
        {
            txtStatus.Text = "Ingrese el nombre del cliente";
            txtStatus.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("ErrorBrush");
            txtCustomerName.Focus();
            return;
        }

        // Validate NIT check digit for non-CF (warning only, does not block)
        if (!nit.Equals("CF", StringComparison.OrdinalIgnoreCase) && !ValidateNitCheckDigit(nit))
        {
            txtStatus.Text = "Advertencia: dígito verificador puede ser incorrecto";
            txtStatus.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("WarningBrush");
            // Continue with invoicing - Infile will validate the NIT
        }

        var customer = _selectedCustomer ?? new Customer { Nit = nit, Name = name };

        // Disable UI while processing
        btnConfirm.IsEnabled = false;
        txtStatus.Text = "Procesando factura...";
        txtStatus.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("PrimaryBrush");

        try
        {
            await _engine.ProcessSaleWithNitAsync(_sale, customer);
            Close();
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Error: {ex.Message}";
            txtStatus.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("ErrorBrush");
            btnConfirm.IsEnabled = true;
        }
    }

    private async void BtnCF_Click(object sender, RoutedEventArgs e)
    {
        _actionTaken = true;
        var customer = Customer.ConsumidorFinal;

        btnConfirm.IsEnabled = false;
        txtStatus.Text = "Procesando como Consumidor Final...";
        txtStatus.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("PrimaryBrush");

        try
        {
            await _engine.ProcessSaleWithNitAsync(_sale, customer);
            Close();
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Error: {ex.Message}";
            txtStatus.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("ErrorBrush");
            btnConfirm.IsEnabled = true;
        }
    }

    private async void BtnPostpone_Click(object sender, RoutedEventArgs e)
    {
        _actionTaken = true;

        txtStatus.Text = "Guardando como pendiente...";
        txtStatus.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("PrimaryBrush");

        try
        {
            await _engine.PostponeSaleAsync(_sale);
            Close();
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Error: {ex.Message}";
            txtStatus.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("ErrorBrush");
        }
    }

    private async void NitInputWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // If no action was taken (user closed with X), auto-postpone
        if (!_actionTaken)
        {
            _actionTaken = true;
            try
            {
                await _engine.PostponeSaleAsync(_sale);
            }
            catch
            {
                // Silently fail - the sale will be detected again on next poll
            }
        }
    }

    private async void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _actionTaken = true;
        try
        {
            await _engine.PostponeSaleAsync(_sale);
        }
        catch { }
        Close();
    }

    /// <summary>
    /// Basic Guatemala NIT format validation
    /// </summary>
    private static bool IsValidNitFormat(string nit)
    {
        if (string.IsNullOrWhiteSpace(nit)) return false;
        if (nit.Equals("CF", StringComparison.OrdinalIgnoreCase)) return true;

        var clean = nit.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (clean.Length < 2) return false;

        for (int i = 0; i < clean.Length - 1; i++)
        {
            if (!char.IsDigit(clean[i])) return false;
        }

        var last = clean[^1];
        return char.IsDigit(last) || last == 'K';
    }

    /// <summary>
    /// Guatemala SAT NIT check digit validation algorithm.
    /// The NIT format is: digits + check digit (last character).
    /// Algorithm: multiply each digit by its position factor (from right, starting at 2),
    /// sum all products, compute modulo 11. The check digit is (11 - remainder),
    /// where 10 = 'K' and 11 = 0.
    /// </summary>
    private static bool ValidateNitCheckDigit(string nit)
    {
        var clean = nit.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (clean.Length < 2) return false;

        // Separate body digits from check digit
        var body = clean[..^1];
        var checkChar = clean[^1];

        // All body characters must be digits
        if (!body.All(char.IsDigit)) return false;

        // Calculate expected check digit
        int sum = 0;
        int factor = 2;
        for (int i = body.Length - 1; i >= 0; i--)
        {
            sum += (body[i] - '0') * factor;
            factor++;
        }

        int remainder = sum % 11;
        int expected = 11 - remainder;

        char expectedChar;
        if (expected == 11)
            expectedChar = '0';
        else if (expected == 10)
            expectedChar = 'K';
        else
            expectedChar = (char)('0' + expected);

        return checkChar == expectedChar;
    }
}
