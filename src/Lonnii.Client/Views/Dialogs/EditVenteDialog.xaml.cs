using System.Windows;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>Edits a sale's client name and/or date - fields sometimes forgotten at sale
/// time, which otherwise skews analytics. Mirrors Lonnii Business's edit-vente modal.</summary>
public partial class EditVenteDialog : Window
{
    public string? ClientNom { get; private set; }
    public DateTime? DateVente { get; private set; }

    public EditVenteDialog(string numeroVente, string? currentClientNom, DateTime currentDateVente)
    {
        InitializeComponent();
        SubtitleText.Text = $"Vente {numeroVente}";
        ClientNomBox.Text = currentClientNom ?? string.Empty;
        DateVentePicker.SelectedDate = currentDateVente.ToLocalTime().Date;
        Loaded += (_, _) => ClientNomBox.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (DateVentePicker.SelectedDate is not { } date)
        {
            ErrorText.Text = "La date est requise.";
            ErrorPanel.Visibility = Visibility.Visible;
            return;
        }

        ClientNom = ClientNomBox.Text.Trim();
        DateVente = date;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
