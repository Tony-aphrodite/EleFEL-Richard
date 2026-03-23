using System.Windows;
using EleFEL.Core.Models;
using EleFEL.Core.Services;
using MessageBox = System.Windows.MessageBox;

namespace EleFEL.App.Views;

public partial class PostponedListWindow : Window
{
    private readonly EleFelEngine _engine;
    private List<PostponedViewModel> _items = new();

    public PostponedListWindow(EleFelEngine engine)
    {
        InitializeComponent();
        _engine = engine;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshListAsync();
    }

    private async Task RefreshListAsync()
    {
        var postponed = await _engine.GetPostponedInvoicesAsync();
        var expirationDays = _engine.GetPostponedExpirationDays();

        _items = postponed.Select(inv => new PostponedViewModel
        {
            Id = inv.Id,
            EleventaSaleId = inv.EleventaSaleId,
            Total = inv.Total,
            CreatedAt = inv.CreatedAt,
            DaysRemaining = Math.Max(0, expirationDays - (DateTime.Now - inv.CreatedAt).Days)
        }).ToList();

        dgPostponed.ItemsSource = _items;
        txtInfo.Text = $"{_items.Count} venta(s) pendiente(s) - Se eliminan automáticamente después de {expirationDays} días";
    }

    private async void BtnInvoiceNow_Click(object sender, RoutedEventArgs e)
    {
        if (dgPostponed.SelectedItem is not PostponedViewModel selected)
        {
            MessageBox.Show("Seleccione una venta de la lista.", "EleFEL",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Re-activate the sale for invoicing by changing status to Pending
        // and show NIT window
        try
        {
            await _engine.ReactivatePostponedAsync(selected.Id, selected.EleventaSaleId);
            await RefreshListAsync();
            MessageBox.Show("La venta ha sido reactivada. La ventana de NIT aparecerá en breve.",
                "EleFEL", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "EleFEL",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (dgPostponed.SelectedItem is not PostponedViewModel selected)
        {
            MessageBox.Show("Seleccione una venta de la lista.", "EleFEL",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"¿Está seguro que desea eliminar la venta #{selected.EleventaSaleId} de la lista de pendientes?\n\nEsta acción no se puede deshacer.",
            "EleFEL - Confirmar eliminación",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                await _engine.DeletePostponedAsync(selected.Id);
                await RefreshListAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "EleFEL",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

public class PostponedViewModel
{
    public int Id { get; set; }
    public long EleventaSaleId { get; set; }
    public decimal Total { get; set; }
    public DateTime CreatedAt { get; set; }
    public int DaysRemaining { get; set; }
}
