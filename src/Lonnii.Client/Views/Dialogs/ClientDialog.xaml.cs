using System.Windows;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>Creates or edits a saved client. Also used to save a customer who so far only
/// appears on sales, pre-filled from what the till recorded.</summary>
public partial class ClientDialog : Window
{
    public SaveClientRequest? Result { get; private set; }

    public ClientDialog(ClientDto? existing)
    {
        InitializeComponent();

        var isNew = existing?.IsRegistered != true;
        Title = isNew ? "Nouveau client" : "Modifier le client";
        HeaderText.Text = isNew ? existing?.Nom ?? "Nouveau client" : existing!.Nom;
        HeaderHint.Text = isNew
            ? "Enregistrez le client pour le retrouver à la caisse et suivre ses achats."
            : "Modifiez les informations du client.";

        if (existing is not null)
        {
            NomBox.Text = existing.Nom;
            TelephoneBox.Text = existing.Telephone ?? string.Empty;
            EmailBox.Text = existing.Email ?? string.Empty;
            AdresseBox.Text = existing.Adresse ?? string.Empty;
            VilleBox.Text = existing.Ville ?? string.Empty;
            NotesBox.Text = existing.Notes ?? string.Empty;
            ActiveCheck.IsChecked = existing.IsActive;
        }

        Loaded += (_, _) => { NomBox.Focus(); NomBox.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NomBox.Text))
        {
            ErrorText.Text = "Le nom du client est requis.";
            NomBox.Focus();
            return;
        }

        Result = new SaveClientRequest(
            NomBox.Text.Trim(), Blank(TelephoneBox.Text), Blank(EmailBox.Text), Blank(AdresseBox.Text),
            Blank(VilleBox.Text), Blank(NotesBox.Text), ActiveCheck.IsChecked != false);
        DialogResult = true;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
