using System.IO;
using System.Windows;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Microsoft.Win32;

namespace Lonnii.Client.Views.Dialogs;

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

    /// <summary>Sentinel for the "no category" row.</summary>
    private static readonly CategoryDto NoCategory = new("", "— Aucune —", null, null, null, null, true, 0);

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
        : this(categories, existing, null)
    {
    }

    public ProductDialog(IReadOnlyList<CategoryDto> categories, ProductDto? existing, AppSession? session)
    {
        _existing = existing;
        _session = session;
        InitializeComponent();

        CategoryBox.ItemsSource = new[] { NoCategory }.Concat(categories).ToList();

        if (existing is null)
        {
            Title = "Nouveau produit";
            HeaderText.Text = "Nouveau produit";
            HeaderHint.Text = "Ajoutez un article à votre inventaire.";
            ThresholdBox.Text = Money.FormatPlain(5);
            QuantityBox.Text = Money.FormatPlain(0);
            CategoryBox.SelectedIndex = 0;
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
            PriceBox.Text = Money.FormatPlain(existing.Price);
            ThresholdBox.Text = Money.FormatPlain(existing.MinimumThreshold);
            PrixFixeCheck.IsChecked = existing.PrixFixe;

            CategoryBox.SelectedValue = existing.CategoryId ?? string.Empty;

            QuantityLabel.Text = "Quantité en stock";
            QuantityBox.Text = Money.FormatPlain(existing.Quantity);
            QuantityBox.IsEnabled = false;
            QuantityHint.Visibility = Visibility.Visible;

            RemovePhotoButton.Visibility = existing.ImageUrl is null ? Visibility.Collapsed : Visibility.Visible;
        }

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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Fail("Le nom du produit est requis.", NameBox);
            return;
        }

        if (!Money.TryParse(PriceBox.Text, out decimal price) || price < 0)
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

        if (!Money.TryParse(ThresholdBox.Text, out int threshold) || threshold < 0)
        {
            Fail("Le seuil d'alerte doit être un entier positif.", ThresholdBox);
            return;
        }

        var quantity = _existing?.Quantity ?? 0;
        if (_existing is null)
        {
            if (!Money.TryParse(QuantityBox.Text, out quantity) || quantity < 0)
            {
                Fail("La quantité initiale doit être un entier positif.", QuantityBox);
                return;
            }
        }

        // A sale price below cost is legitimate (clearance), but worth a confirmation.
        if (cost is { } c && c > price)
        {
            var confirm = MessageBox.Show(this,
                $"Le prix de vente ({Money.Format(price)}) est inférieur au prix d'achat ({Money.Format(c)}).\n\n" +
                "Enregistrer quand même ?",
                "Marge négative", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;
        }

        var categoryId = (CategoryBox.SelectedItem as CategoryDto)?.Id;

        Result = new SaveProductRequest(
            Name: name,
            Price: price,
            Description: Blank(DescriptionBox.Text),
            Sku: Blank(SkuBox.Text),
            Barcode: Blank(BarcodeBox.Text),
            CategoryId: Blank(categoryId),
            SupplierId: _existing?.SupplierId,
            Quantity: quantity,
            MinimumThreshold: threshold,
            CostPrice: cost,
            PrixFixe: PrixFixeCheck.IsChecked == true,
            StorageLocation: Blank(LocationBox.Text),
            ExpiryDate: _existing?.ExpiryDate);

        DialogResult = true;
    }

    /// <summary>Regroups a money/quantity field once the user leaves it, so "1000" becomes
    /// "1 000" without fighting the caret while they are still typing.</summary>
    private void Regroup_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        if (Money.TryParse(box.Text, out decimal value)) box.Text = Money.FormatPlain(value);
    }

    private void Fail(string message, System.Windows.Controls.Control focus)
    {
        ErrorText.Text = message;
        focus.Focus();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
