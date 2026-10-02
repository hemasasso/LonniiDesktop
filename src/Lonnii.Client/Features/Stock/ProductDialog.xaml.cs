using System.IO;
using System.Windows;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Microsoft.Win32;

namespace Lonnii.Client.Features.Stock;

/// <summary>
/// Creates or edits a product.
///
/// On edit the quantity field is read-only: stock moves only through the adjust dialog,
/// so every change leaves a row in the history. That is the same rule the API enforces.
/// </summary>
public partial class ProductDialog : Window
{
    private readonly ProductDto? _existing;
    private readonly AppSession? _session;

    /// <summary>Set while Cost/Price/Marge are being assigned programmatically, so one box's
    /// recompute does not cross-trigger the other's and overwrite what was just loaded.</summary>
    private bool _updatingPricing;

    /// <summary>Sentinel for the "no category" row.</summary>
    private static readonly CategoryDto NoCategory = new("", "— Aucune —", null, null, null, null, true, 0);

    /// <summary>Sentinel for the "no supplier" row.</summary>
    private static readonly SupplierDto NoSupplier =
        new("", "— Aucun —", null, null, null, null, null, null, null, null, null, true);

    /// <summary>The request to send, once the dialog has been accepted.</summary>
    public SaveProductRequest? Result { get; private set; }

    /// <summary>
    /// A newly picked photo's bytes, if the user chose one. The dialog only stages the
    /// file locally - StockView uploads it after the product itself has been saved,
    /// since a brand new product has no id to attach a photo to until then.
    /// </summary>
    public byte[]? PendingPhoto { get; private set; }

    /// <summary>The file name of <see cref="PendingPhoto"/>, used to guess its content type on upload.</summary>
    public string? PendingPhotoFileName { get; private set; }

    /// <summary>True when the user removed an existing photo and no replacement was chosen.</summary>
    public bool PhotoRemoved { get; private set; }

    /// <summary>
    /// The dialog needs the API client to fetch the current photo for preview; the
    /// two-argument constructor is used only where no session is available (kept for
    /// callers that never show an image, none currently, but avoids a breaking change).
    /// </summary>
    public ProductDialog(IReadOnlyList<CategoryDto> categories, ProductDto? existing)
        : this(categories, [], existing, null)
    {
    }

    public ProductDialog(
        IReadOnlyList<CategoryDto> categories, ProductDto? existing, AppSession? session)
        : this(categories, [], existing, session)
    {
    }

    public ProductDialog(
        IReadOnlyList<CategoryDto> categories, IReadOnlyList<SupplierDto> suppliers,
        ProductDto? existing, AppSession? session)
    {
        _existing = existing;
        _session = session;
        InitializeComponent();

        CategoryBox.ItemsSource = new[] { NoCategory }.Concat(categories).ToList();
        SupplierBox.ItemsSource = new[] { NoSupplier }.Concat(suppliers).ToList();

        TypeBox.ItemsSource = ProductTypes.All.Select(t => new TypeOption(t, ProductTypes.DisplayName(t))).ToList();
        TypeBox.DisplayMemberPath = nameof(TypeOption.Label);
        TypeBox.SelectedValuePath = nameof(TypeOption.Value);
        TypeBox.SelectedValue = existing?.TypeProduit ?? ProductTypes.Marchandise;

        // Guarded: Cost_Changed/Price_Changed/Marge_Changed would otherwise cross-recompute
        // each other as these are populated below, clobbering the stored margin with a
        // freshly derived one before it is even read.
        _updatingPricing = true;

        if (existing is null)
        {
            Title = "Nouveau produit";
            HeaderText.Text = "Nouveau produit";
            HeaderHint.Text = "Ajoutez un article à votre inventaire.";
            ThresholdBox.Text = Money.FormatPlain(5);
            QuantityBox.Text = Money.FormatPlain(0);
            CategoryBox.SelectedIndex = 0;
            SupplierBox.SelectedIndex = 0;
            PrixNegociableCheck.IsChecked = false;
            MargeBox.Text = "30";
        }
        else
        {
            Title = "Modifier le produit";
            HeaderText.Text = existing.Name;
            HeaderHint.Text = "Modifiez les informations du produit.";

            NameBox.Text = existing.Name;
            DescriptionBox.Text = existing.Description ?? string.Empty;
            SkuBox.Text = existing.Sku ?? string.Empty;
            BarcodeBox.Text = existing.Barcode ?? string.Empty;
            LocationBox.Text = existing.StorageLocation ?? string.Empty;
            CostBox.Text = existing.CostPrice is { } cost ? Money.FormatPlain(cost) : string.Empty;
            // A negociable product with no minimum set stores Price as 0 - shown blank here,
            // not "0", so re-saving without touching the field keeps it free of a floor.
            PriceBox.Text = existing.PrixFixe || existing.Price > 0
                ? Money.FormatPlain(existing.Price) : string.Empty;
            MargeBox.Text = existing.MarginPercentage is { } marge ? Money.FormatPlain(marge, 2) : string.Empty;
            ThresholdBox.Text = Money.FormatPlain(existing.MinimumThreshold);
            PrixNegociableCheck.IsChecked = !existing.PrixFixe;
            VenteLibreCheck.IsChecked = existing.VenteLibre;
            UnitBox.Text = existing.UniteAffichage ?? string.Empty;

            CategoryBox.SelectedValue = existing.CategoryId ?? string.Empty;
            SupplierBox.SelectedValue = existing.SupplierId ?? string.Empty;

            QuantityLabel.Text = "Quantité en stock";
            QuantityBox.Text = Money.FormatPlain(existing.Quantity);
            QuantityBox.IsEnabled = false;
            QuantityHint.Visibility = Visibility.Visible;

            RemovePhotoButton.Visibility = existing.ImageUrl is null ? Visibility.Collapsed : Visibility.Visible;
        }

        _updatingPricing = false;

        ApplySalesModeVisuals();
        ApplyNegotiableVisuals();

        Loaded += async (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
            await LoadExistingPhotoAsync();
        };
    }

    /// <summary>Downloads and shows the product's current photo, if it has one.</summary>
    private async Task LoadExistingPhotoAsync()
    {
        if (_existing?.ImageUrl is not { } url || _session is null) return;

        try
        {
            var bytes = await _session.Api.GetImageBytesAsync(url);
            ShowPhoto(bytes);
        }
        catch (ApiException)
        {
            // A photo that fails to load is not worth blocking the dialog over; the
            // placeholder just keeps showing "Aucune photo".
        }
    }

    private void ShowPhoto(byte[] bytes)
    {
        PhotoImage.Source = ImageHelper.FromBytes(bytes);
        PhotoPlaceholder.Visibility = Visibility.Collapsed;
        RemovePhotoButton.Visibility = Visibility.Visible;
    }

    private void ClearPhoto()
    {
        PhotoImage.Source = null;
        PhotoPlaceholder.Visibility = Visibility.Visible;
        RemovePhotoButton.Visibility = Visibility.Collapsed;
    }

    private void ChoosePhoto_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choisir une photo",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.webp",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            PendingPhoto = bytes;
            PendingPhotoFileName = Path.GetFileName(dialog.FileName);
            PhotoRemoved = false;
            ShowPhoto(bytes);
        }
        catch (IOException ex)
        {
            ErrorText.Text = $"Impossible de lire ce fichier : {ex.Message}";
        }
    }

    private void RemovePhoto_Click(object sender, RoutedEventArgs e)
    {
        PendingPhoto = null;
        PendingPhotoFileName = null;
        PhotoRemoved = true;
        ClearPhoto();
    }

    /// <summary>
    /// Vente Libre alone means unlimited quantity - no separate "Stock indéfini" choice to
    /// make about it (an earlier version of this dialog had one, mirrored from Lonnii
    /// Business's GestionDeStock.jsx; the user decided that nested option was unnecessary
    /// noise here and asked for a plain "Vente Libre implies infinite quantity" rule instead).
    /// Quantity (new products only - it stays read-only on edit regardless) and the low-stock
    /// threshold are disabled while it applies, and there is no purchase cost to track either
    /// (it is a service, not a stocked good), so Prix d'achat is disabled and cleared too.
    /// </summary>
    private void ApplySalesModeVisuals()
    {
        var venteLibre = VenteLibreCheck.IsChecked == true;
        VenteLibreHint.Visibility = venteLibre ? Visibility.Visible : Visibility.Collapsed;

        if (_existing is null) QuantityBox.IsEnabled = !venteLibre;
        ThresholdBox.IsEnabled = !venteLibre;

        CostBox.IsEnabled = !venteLibre;
        if (venteLibre) CostBox.Text = string.Empty;

        MargeBox.IsEnabled = !venteLibre;
        if (venteLibre) MargeBox.Text = string.Empty;
    }

    private void VenteLibre_Changed(object sender, RoutedEventArgs e) => ApplySalesModeVisuals();

    /// <summary>Négociable turns Prix de vente from a required fixed price into an optional
    /// floor: left blank, the cashier can accept any amount at the till; filled in, it is the
    /// least the product may sell for once the cashier negotiates it down (see VentesView's
    /// cart pricing, which pre-fills and clamps to this value).</summary>
    private void ApplyNegotiableVisuals()
    {
        var negociable = PrixNegociableCheck.IsChecked == true;
        PriceLabel.Text = negociable ? "Prix de vente (minimum, optionnel)" : "Prix de vente *";
        PriceMinimumHint.Visibility = negociable ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PrixNegociable_Changed(object sender, RoutedEventArgs e) => ApplyNegotiableVisuals();

    private sealed record TypeOption(string Value, string Label);

    private string SelectedType => TypeBox.SelectedValue as string ?? ProductTypes.Marchandise;

    /// <summary>A product that is not for sale has no use for the till-only options, so the
    /// hint says why it will vanish from Ventes.</summary>
    private void Type_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TypeHint is null) return;

        var sellable = ProductTypes.IsSellable(SelectedType);
        TypeHint.Visibility = sellable ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Fail("Le nom du produit est requis.", NameBox);
            return;
        }

        var negociable = PrixNegociableCheck.IsChecked == true;

        // Négociable makes this optional: left blank, it is stored as 0 and means "no floor -
        // the cashier may accept any price." A fixed-price product still requires a real value.
        decimal price = 0;
        if (string.IsNullOrWhiteSpace(PriceBox.Text))
        {
            if (!negociable)
            {
                Fail("Le prix de vente doit être un nombre positif.", PriceBox);
                return;
            }
        }
        else if (!Money.TryParse(PriceBox.Text, out price) || price < 0)
        {
            Fail("Le prix de vente doit être un nombre positif.", PriceBox);
            return;
        }

        decimal? cost = null;
        if (!string.IsNullOrWhiteSpace(CostBox.Text))
        {
            if (!Money.TryParse(CostBox.Text, out decimal parsedCost) || parsedCost < 0)
            {
                Fail("Le prix d'achat doit être un nombre positif.", CostBox);
                return;
            }
            cost = parsedCost;
        }

        var venteLibre = VenteLibreCheck.IsChecked == true;

        var threshold = 0;
        if (!venteLibre && (!Money.TryParse(ThresholdBox.Text, out threshold) || threshold < 0))
        {
            Fail("Le seuil d'alerte doit être un entier positif.", ThresholdBox);
            return;
        }

        decimal? margin = null;
        if (!string.IsNullOrWhiteSpace(MargeBox.Text))
        {
            if (!Money.TryParse(MargeBox.Text, out decimal parsedMargin) || parsedMargin < 0)
            {
                Fail("La marge doit être un nombre positif.", MargeBox);
                return;
            }
            margin = parsedMargin;
        }

        var quantity = _existing?.Quantity ?? 0;
        if (_existing is null && !venteLibre)
        {
            if (!Money.TryParse(QuantityBox.Text, out quantity) || quantity < 0)
            {
                Fail("La quantité initiale doit être un entier positif.", QuantityBox);
                return;
            }
        }

        // A sale price below cost is legitimate (clearance), but worth a confirmation. Skipped
        // for a négociable product with no minimum set (price is 0, meaning "not decided yet",
        // not an actual sale price to compare against the cost).
        if (cost is { } c && c > price && !(negociable && price == 0))
        {
            var confirm = MessageBox.Show(this,
                $"Le prix de vente ({Money.Format(price)}) est inférieur au prix d'achat ({Money.Format(c)}).\n\n" +
                "Enregistrer quand même ?",
                "Marge négative", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;
        }

        var categoryId = (CategoryBox.SelectedItem as CategoryDto)?.Id;
        var supplierId = (SupplierBox.SelectedItem as SupplierDto)?.Id;

        Result = new SaveProductRequest(
            Name: name,
            Price: price,
            Description: Blank(DescriptionBox.Text),
            Sku: Blank(SkuBox.Text),
            Barcode: Blank(BarcodeBox.Text),
            CategoryId: Blank(categoryId),
            SupplierId: Blank(supplierId),
            Quantity: quantity,
            MinimumThreshold: threshold,
            CostPrice: cost,
            PrixFixe: !negociable,
            VenteLibre: venteLibre,
            // No separate UI choice any more - Vente Libre alone always means unlimited
            // quantity, so the two flags are kept in lockstep here rather than exposing a
            // second checkbox for what is now a single decision.
            StockIllimite: venteLibre,
            UniteAffichage: Blank(UnitBox.Text),
            StorageLocation: Blank(LocationBox.Text),
            ExpiryDate: _existing?.ExpiryDate,
            TypeProduit: SelectedType,
            MarginPercentage: margin,
            // Vente Mixte can no longer be turned on from here - the option was removed as
            // too confusing (a shop can just open a bulk box and sell it one unit at a time).
            // A product that already had it keeps its stored gros/détail data untouched, since
            // there is no UI here to change it any more; a brand new product is never mixte.
            VenteMixte: _existing?.VenteMixte ?? false,
            UniteVente: _existing?.UniteVente,
            FacteurConversion: _existing?.FacteurConversion,
            PrixVenteDetail: _existing?.PrixVenteDetail);

        DialogResult = true;
    }

    /// <summary>Regroups a money/quantity field once the user leaves it, so "1000" becomes
    /// "1 000" without fighting the caret while they are still typing.</summary>
    private void Regroup_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        if (Money.TryParse(box.Text, out decimal value)) box.Text = Money.FormatPlain(value);
    }

    // --- Bidirectional marge: editing the cost or the margin recomputes the sale price;
    // editing the sale price recomputes the margin. Either can be the one the user types. ---

    private void Cost_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => RecomputePriceFromMarge();

    private void Marge_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => RecomputePriceFromMarge();

    private void Price_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => RecomputeMargeFromPrice();

    private void RecomputePriceFromMarge()
    {
        if (_updatingPricing) return;
        if (!Money.TryParse(CostBox.Text, out decimal cost) || cost <= 0) return;
        if (!Money.TryParse(MargeBox.Text, out decimal marge)) return;

        _updatingPricing = true;
        PriceBox.Text = Money.FormatPlain(cost + cost * marge / 100m, 2);
        _updatingPricing = false;
    }

    private void RecomputeMargeFromPrice()
    {
        if (_updatingPricing) return;
        if (!Money.TryParse(CostBox.Text, out decimal cost) || cost <= 0) return;
        if (!Money.TryParse(PriceBox.Text, out decimal price)) return;

        _updatingPricing = true;
        MargeBox.Text = Money.FormatPlain((price - cost) / cost * 100m, 2);
        _updatingPricing = false;
    }

    private void Fail(string message, System.Windows.Controls.Control focus)
    {
        ErrorText.Text = message;
        focus.Focus();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
