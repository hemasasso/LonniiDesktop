using System.Windows;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>Every recorded movement for one product, newest first.</summary>
public partial class StockHistoryDialog : Window
{
    private readonly AppSession _session;
    private readonly ProductDto _product;

    public StockHistoryDialog(AppSession session, ProductDto product)
    {
        _session = session;
        _product = product;
        InitializeComponent();

        ProductName.Text = product.Name;
        Subtitle.Text = $"Stock actuel : {product.Quantity}";

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var history = await _session.Api.GetProductHistoryAsync(_product.Id);
            HistoryGrid.ItemsSource = history;
            EmptyPanel.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Subtitle.Text = $"Stock actuel : {_product.Quantity}  •  {history.Count} mouvement(s)";
        }
        catch (ApiException ex)
        {
            MessageBox.Show(this, ex.Message, "Historique", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }
}
