using System.Windows;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>Collects a mandatory cancellation reason before <c>PUT /ventes/{id}/annuler</c> -
/// the server rejects an empty one anyway (mirrors backend/routes/ventes.js), so the dialog
/// checks first rather than round-tripping to find out.</summary>
public partial class CancelVenteDialog : Window
{
    /// <summary>The reason typed in, trimmed. Only meaningful when <see cref="Window.DialogResult"/> is true.</summary>
    public string Motif { get; private set; } = string.Empty;

    public CancelVenteDialog(string numeroVente)
    {
        InitializeComponent();
        SubtitleText.Text = $"Vente {numeroVente} — le stock des articles sera restitué. Cette action ne peut pas être annulée.";
        Loaded += (_, _) => MotifBox.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var motif = MotifBox.Text.Trim();
        if (motif.Length == 0)
        {
            ErrorText.Text = "Le motif d'annulation est requis.";
            ErrorPanel.Visibility = Visibility.Visible;
            return;
        }

        Motif = motif;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
