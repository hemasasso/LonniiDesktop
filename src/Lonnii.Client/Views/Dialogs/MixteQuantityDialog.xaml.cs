using System.Windows;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// Asks how many of each unit a Vente Mixte product's cart line should carry - the bulk unit
/// (e.g. Carton) and the base/detail unit (e.g. unité) at once, since a customer may well buy
/// both in the same sale. Either box may be left at zero.
/// </summary>
public partial class MixteQuantityDialog : Window
{
    private readonly ProductDto _product;
    private readonly int _factor;

    public int QuantiteGros { get; private set; }
    public int QuantiteDetail { get; private set; }

    public MixteQuantityDialog(ProductDto product)
    {
        _product = product;
        _factor = Math.Max(product.FacteurConversion ?? 1, 1);
        InitializeComponent();

        HeaderText.Text = product.Name;
        SubtitleText.Text = $"Disponible : {product.QuantityDisplay}";
        GrosLabel.Text = product.UniteVente ?? "Gros";
        DetailLabel.Text = string.IsNullOrWhiteSpace(product.UniteAffichage) ? "Unité" : product.UniteAffichage;

        Loaded += (_, _) => GrosBox.Focus();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!Money.TryParse(GrosBox.Text is { Length: > 0 } g ? g : "0", out int gros) || gros < 0)
        {
            Fail($"Le nombre de {GrosLabel.Text} doit être un entier positif.", GrosBox);
            return;
        }

        if (!Money.TryParse(DetailBox.Text is { Length: > 0 } d ? d : "0", out int detail) || detail < 0)
        {
            Fail($"Le nombre de {DetailLabel.Text} doit être un entier positif.", DetailBox);
            return;
        }

        if (gros == 0 && detail == 0)
        {
            Fail("Indiquez au moins une quantité.", GrosBox);
            return;
        }

        var baseUnitsNeeded = gros * _factor + detail;
        if (baseUnitsNeeded > _product.Quantity)
        {
            Fail($"Stock insuffisant : {_product.QuantityDisplay} disponible.", GrosBox);
            return;
        }

        QuantiteGros = gros;
        QuantiteDetail = detail;
        DialogResult = true;
    }

    private void Regroup_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        if (Money.TryParse(box.Text, out int value)) box.Text = Money.FormatPlain(value);
    }

    private void Fail(string message, System.Windows.Controls.Control focus)
    {
        ErrorText.Text = message;
        focus.Focus();
    }
}
