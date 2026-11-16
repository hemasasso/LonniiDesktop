using System.Windows;
using System.Windows.Controls;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Stock;

/// <summary>
/// Records one change to a product's empties: an inventaire (the number counted now), a return
/// to the supplier (with the consigne the supplier paid back, if any, put into the caisse), or
/// broken / lost ones.
/// </summary>
public sealed class EmballageMouvementDialog : Window
{
    private readonly IReadOnlyList<EmballageStockDto> _rows;
    private readonly LonniiApiClient _api;
    private readonly ComboBox _product = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly StackPanel _moneyPanel = new();
    private readonly TextBlock _supplierLabel = new() { Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _type = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock _current = new() { Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _quantityLabel = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBox _quantity = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly StackPanel _supplierPanel = new();
    private readonly ComboBox _supplier = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _montant = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBlock _montantHint = new() { FontSize = 12, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _mode = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _note = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button _ok = new() { Content = "Enregistrer", IsDefault = true };

    public EmballageMouvementDialog(Window? owner, IReadOnlyList<EmballageStockDto> rows, string? productId, string? type,
        IReadOnlyList<SupplierDto> suppliers, LonniiApiClient api)
    {
        Owner = owner;
        _rows = rows;
        _api = api;
        Title = "Mouvement d'emballages vides";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "Surface");

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(Label("Emballage"));
        // A shared type is one line: its empties are counted together.
        foreach (var r in rows)
            _product.Items.Add(new ComboBoxItem
            {
                Content = r.Produits is { Count: > 0 } ps ? $"{r.NomProduit}  ({string.Join(", ", ps.Select(p => p.NomProduit))})" : r.NomProduit,
                Tag = r.ProductId,
            });
        _product.SelectedIndex = Math.Max(0, rows.ToList().FindIndex(r => r.ProductId == productId));
        root.Children.Add(_product);

        root.Children.Add(Label("Mouvement"));
        _type.Items.Add(new ComboBoxItem { Content = "Retour au fournisseur (vides rendus)", Tag = EmballageMouvementTypes.RetourFournisseur });
        _type.Items.Add(new ComboBoxItem { Content = "Pleins reçus du fournisseur", Tag = EmballageMouvementTypes.RecuFournisseur });
        _type.Items.Add(new ComboBoxItem { Content = "Casse / perte", Tag = EmballageMouvementTypes.Casse });
        _type.Items.Add(new ComboBoxItem { Content = "Inventaire (compter les vides)", Tag = EmballageMouvementTypes.Inventaire });
        _type.SelectedIndex = type switch
        {
            EmballageMouvementTypes.RecuFournisseur => 1, EmballageMouvementTypes.Casse => 2, EmballageMouvementTypes.Inventaire => 3, _ => 0,
        };
        root.Children.Add(_type);

        _current.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        root.Children.Add(_current);
        _quantityLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        root.Children.Add(_quantityLabel);
        root.Children.Add(_quantity);
        _quantity.LostFocus += (_, _) =>
        {
            if (SaleCharges.TryParseCount(_quantity.Text, out var q) && q > 0) _quantity.Text = SaleCharges.CountLabel(q);
        };

        _supplierLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        _supplierPanel.Children.Add(_supplierLabel);
        _supplier.Items.Add(new ComboBoxItem { Content = "—", Tag = null, IsSelected = true });
        foreach (var s in suppliers) _supplier.Items.Add(new ComboBoxItem { Content = s.Name, Tag = s.Id });
        _supplierPanel.Children.Add(_supplier);
        _supplierPanel.Children.Add(_moneyPanel);
        _moneyPanel.Children.Add(Label("Consigne remboursée par le fournisseur (facultatif)"));
        MoneyInput.GroupAsYouType(_montant);
        _moneyPanel.Children.Add(_montant);
        _montantHint.Text = "Entre dans votre caisse ouverte. Laissez vide si le fournisseur échange les vides contre des pleins sans payer.";
        _montantHint.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        _moneyPanel.Children.Add(_montantHint);
        _moneyPanel.Children.Add(Label("Reçu en"));
        _mode.Items.Add(new ComboBoxItem { Content = "Espèces", Tag = "cash", IsSelected = true });
        _mode.Items.Add(new ComboBoxItem { Content = "Mobile money", Tag = "mobile_money" });
        _mode.Items.Add(new ComboBoxItem { Content = "Banque / carte", Tag = "carte" });
        _moneyPanel.Children.Add(_mode);
        root.Children.Add(_supplierPanel);

        root.Children.Add(Label("Note (facultatif)"));
        root.Children.Add(_note);

        _error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        root.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Annuler", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        cancel.SetResourceReference(StyleProperty, "SecondaryButton");
        _ok.SetResourceReference(StyleProperty, "PrimaryButton");
        _ok.Click += async (_, _) => await SaveAsync();
        buttons.Children.Add(cancel);
        buttons.Children.Add(_ok);
        root.Children.Add(buttons);
        Content = root;

        _product.SelectionChanged += (_, _) => Refresh();
        _type.SelectionChanged += (_, _) => Refresh();
        Refresh();
        Loaded += (_, _) => _quantity.Focus();
    }

    private static TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        return t;
    }

    private EmballageStockDto? Selected =>
        _rows.FirstOrDefault(r => r.ProductId == (_product.SelectedItem as ComboBoxItem)?.Tag as string);

    private string SelectedType => (_type.SelectedItem as ComboBoxItem)?.Tag as string ?? EmballageMouvementTypes.RetourFournisseur;

    private void Refresh()
    {
        _error.Text = "";
        var p = Selected;
        var unit = p?.Unite ?? "emballage(s)";
        _current.Text = p is null ? "" : $"Vides en magasin actuellement : {SaleCharges.CountLabel(Math.Max(0, p.Vides))} {unit}"
            + (p.Vides < 0 ? " (négatif : faites un inventaire)" : "");
        var recu = SelectedType == EmballageMouvementTypes.RecuFournisseur;
        _quantityLabel.Text = SelectedType == EmballageMouvementTypes.Inventaire
            ? $"Nombre de vides comptés au magasin ({unit})"
            : recu ? $"Nombre de pleins livrés ({unit})" : $"Nombre de vides qui sortent ({unit})";
        _current.Visibility = recu ? Visibility.Collapsed : Visibility.Visible;
        _supplierPanel.Visibility = SelectedType is EmballageMouvementTypes.RetourFournisseur or EmballageMouvementTypes.RecuFournisseur
            ? Visibility.Visible : Visibility.Collapsed;
        _moneyPanel.Visibility = recu ? Visibility.Collapsed : Visibility.Visible;
        _supplierLabel.Text = recu
            ? "Fournisseur qui a livré (obligatoire) - suivi de ce que vous lui devez en emballages"
            : "Fournisseur (facultatif)";
        // Suggest the consigne back at the product's own rate once a count is typed.
        if (p?.ConsigneMontant is > 0)
            _montantHint.Text = $"Consigne du produit : {Money.Format(p.ConsigneMontant.Value)} / {unit}. Entre dans votre caisse ouverte. "
                + "Laissez vide si le fournisseur échange les vides contre des pleins sans payer.";
    }

    private async Task SaveAsync()
    {
        _error.Text = "";
        var p = Selected;
        if (p is null) return;
        var text = _quantity.Text.Trim();
        if (!SaleCharges.TryParseCount(text, out var quantite) || quantite < 0)
        {
            _error.Text = "Nombre invalide : par exemple 3, 2,5 ou 1 + 1/2.";
            return;
        }
        var supplierId = (_supplier.SelectedItem as ComboBoxItem)?.Tag as string;
        if (SelectedType == EmballageMouvementTypes.RecuFournisseur && supplierId is null)
        {
            _error.Text = "Choisissez le fournisseur qui a livré.";
            return;
        }
        decimal? montant = null;
        if (SelectedType == EmballageMouvementTypes.RetourFournisseur && _montant.Text.Trim().Length > 0)
        {
            if (!Money.TryParse(_montant.Text, out decimal m) || m < 0)
            {
                _error.Text = "Montant invalide.";
                return;
            }
            montant = m > 0 ? m : null;
        }

        _ok.IsEnabled = false;
        try
        {
            await _api.AddEmballageMouvementAsync(new EmballageMouvementRequest(
                p.ProductId, SelectedType, quantite, montant,
                montant is null ? null : (_mode.SelectedItem as ComboBoxItem)?.Tag as string,
                SelectedType is EmballageMouvementTypes.RetourFournisseur or EmballageMouvementTypes.RecuFournisseur ? supplierId : null,
                string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim()));
            DialogResult = true;
        }
        catch (ApiException ex) { _error.Text = ex.Message; }
        catch (Exception ex) { _error.Text = $"Échec de l'enregistrement : {ex.Message}"; }
        finally { _ok.IsEnabled = true; }
    }
}
