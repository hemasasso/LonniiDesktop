using System.Windows;
using System.Windows.Controls;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// Moves stock in or out, recording why.
///
/// The user types a positive quantity and picks a movement type; the sign comes from the
/// type. That avoids the classic till mistake of typing a negative number for a movement
/// that is already an outgoing one and adding stock by accident.
/// </summary>
public partial class StockAdjustDialog : Window
{
    private readonly ProductDto _product;

    /// <summary>A movement type, its label, and whether it adds to or removes from stock.</summary>
    private sealed record MovementOption(string Value, string Label, int Sign, string Hint);

    private static readonly MovementOption[] Movements =
    [
        new("ajout", "Entrée en stock (réapprovisionnement)", +1, "Ajoute la quantité à l'inventaire."),
        new("retour", "Retour client", +1, "Le client a rendu l'article; il revient en stock."),
        new("vente", "Sortie pour vente", -1, "Retire la quantité vendue de l'inventaire."),
        new("damaged", "Casse ou perte", -1, "Retire des articles abîmés ou perdus."),
        new("expired", "Périmé", -1, "Retire des articles dont la date est dépassée."),
        new("transfer", "Transfert sortant", -1, "Retire des articles envoyés ailleurs."),
        new("adjustment", "Correction d'inventaire", +1, "Corrige un écart constaté lors d'un comptage."),
    ];

    /// <summary>The request to send, once the dialog has been accepted.</summary>
    public AdjustStockRequest? Result { get; private set; }

    public StockAdjustDialog(ProductDto product)
    {
        _product = product;
        InitializeComponent();

        ProductName.Text = product.Name;
        CurrentStock.Text = $"Stock actuel : {Money.FormatPlain(product.Quantity)}  •  " +
                             $"seuil d'alerte : {Money.FormatPlain(product.MinimumThreshold)}";

        MovementBox.ItemsSource = Movements;
        MovementBox.SelectedIndex = 0;

        Loaded += (_, _) => QuantityBox.Focus();
        UpdatePreview();
    }

    private MovementOption Selected => (MovementOption)MovementBox.SelectedItem;

    private void Movement_Changed(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void Quantity_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void Quantity_LostFocus(object sender, RoutedEventArgs e)
    {
        if (Money.TryParse(QuantityBox.Text, out int quantity) && quantity > 0)
            QuantityBox.Text = Money.FormatPlain(quantity);
    }

    /// <summary>Shows the resulting stock level before the user commits.</summary>
    private void UpdatePreview()
    {
        if (!IsLoaded && PreviewText is null) return;
        if (MovementBox.SelectedItem is null) return;

        var movement = Selected;
        PreviewHint.Text = movement.Hint;

        if (!Money.TryParse(QuantityBox.Text, out int quantity) || quantity <= 0)
        {
            PreviewText.Text = $"Stock : {Money.FormatPlain(_product.Quantity)}  →  —";
            return;
        }

        var change = movement.Sign * quantity;
        var result = _product.Quantity + change;
        var changeSign = change > 0 ? "+" : "";

        PreviewText.Text = $"Stock : {Money.FormatPlain(_product.Quantity)}  →  {Money.FormatPlain(result)}   " +
                            $"({changeSign}{Money.FormatPlain(change)})";

        if (result < 0)
        {
            ErrorText.Text = "Le stock ne peut pas devenir négatif.";
        }
        else
        {
            ErrorText.Text = string.Empty;
            if (result <= _product.MinimumThreshold)
                PreviewHint.Text = movement.Hint + "  Attention : le produit passera en stock bas.";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Money.TryParse(QuantityBox.Text, out int quantity) || quantity <= 0)
        {
            ErrorText.Text = "Indiquez une quantité supérieure à zéro.";
            QuantityBox.Focus();
            return;
        }

        var movement = Selected;
        var change = movement.Sign * quantity;

        if (_product.Quantity + change < 0)
        {
            ErrorText.Text = $"Stock insuffisant : {Money.FormatPlain(_product.Quantity)} disponible(s).";
            QuantityBox.Focus();
            return;
        }

        Result = new AdjustStockRequest(
            QuantityChanged: change,
            MovementType: movement.Value,
            Reason: string.IsNullOrWhiteSpace(ReasonBox.Text) ? null : ReasonBox.Text.Trim());

        DialogResult = true;
    }
}
