using System.Windows;
using System.Windows.Controls;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Stock;

/// <summary>Creates or edits a supplier.</summary>
public partial class SupplierDialog : Window
{
    /// <summary>The request to send, once the dialog has been accepted.</summary>
    public SaveSupplierRequest? Result { get; private set; }

    public SupplierDialog(SupplierDto? existing)
    {
        InitializeComponent();

        if (existing is null)
        {
            Title = "Nouveau fournisseur";
            HeaderText.Text = "Nouveau fournisseur";
            HeaderHint.Text = "Ajoutez un fournisseur pour le rattacher à vos produits.";
        }
        else
        {
            Title = "Modifier le fournisseur";
            HeaderText.Text = existing.Name;
            HeaderHint.Text = "Modifiez les informations du fournisseur.";

            NameBox.Text = existing.Name;
            ContactPersonBox.Text = existing.ContactPerson ?? string.Empty;
            PhoneBox.Text = existing.Phone ?? string.Empty;
            EmailBox.Text = existing.Email ?? string.Empty;
            AddressBox.Text = existing.Address ?? string.Empty;
            CityBox.Text = existing.City ?? string.Empty;
            CountryBox.Text = existing.Country ?? string.Empty;
            PaymentTermsBox.Text = existing.PaymentTerms ?? string.Empty;
            NotesBox.Text = existing.Notes ?? string.Empty;
            ActiveCheck.IsChecked = existing.IsActive;
            RatingBox.SelectedIndex = existing.Rating ?? 0;
            MontantDuBox.Text = Money.FormatPlain(existing.MontantDu);
        }

        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorText.Text = "Le nom du fournisseur est requis.";
            NameBox.Focus();
            return;
        }

        if (!Money.TryParse(MontantDuBox.Text, out decimal montantDu) || montantDu < 0)
        {
            ErrorText.Text = "Le montant dû doit être un nombre positif.";
            MontantDuBox.Focus();
            return;
        }

        Result = new SaveSupplierRequest(
            Name: name,
            ContactPerson: Blank(ContactPersonBox.Text),
            Email: Blank(EmailBox.Text),
            Phone: Blank(PhoneBox.Text),
            Address: Blank(AddressBox.Text),
            City: Blank(CityBox.Text),
            Country: Blank(CountryBox.Text),
            PaymentTerms: Blank(PaymentTermsBox.Text),
            Notes: Blank(NotesBox.Text),
            Rating: SelectedRating(),
            IsActive: ActiveCheck.IsChecked != false,
            MontantDu: montantDu);

        DialogResult = true;
    }

    private int? SelectedRating() =>
        RatingBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var rating)
            ? rating
            : null;

    /// <summary>Regroups a money field once the user leaves it, so "50000" becomes "50 000"
    /// without fighting the caret while they are still typing.</summary>
    private void Regroup_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (Money.TryParse(box.Text, out decimal value)) box.Text = Money.FormatPlain(value);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
