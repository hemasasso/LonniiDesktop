using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Ventes;

/// <summary>Edits a sale's client name and/or date - fields sometimes forgotten at sale
/// time, which otherwise skews analytics. Mirrors Lonnii Business's edit-vente modal.</summary>
public partial class EditVenteDialog : Window
{
    private readonly AppSession _session;
    private List<ClientDto> _clients = [];

    public string? ClientNom { get; private set; }
    public DateTime? DateVente { get; private set; }
    public string? ClientTelephone { get; private set; }
    public string? ClientEmail { get; private set; }

    private sealed record ClientSuggestion(ClientDto Client)
    {
        public string Nom => Client.Nom;
        public string Detail => string.Join("  •  ", new[]
        {
            Client.Telephone ?? Client.Email,
            Client.ResteDu > 0 ? $"doit {Money.Format(Client.ResteDu)}" : null,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public EditVenteDialog(
        AppSession session, string numeroVente, string? currentClientNom, DateTime currentDateVente,
        string? currentClientTelephone = null, string? currentClientEmail = null)
    {
        _session = session;
        InitializeComponent();
        SubtitleText.Text = $"Vente {numeroVente}";
        ClientNomBox.Text = currentClientNom ?? string.Empty;
        ClientTelephoneBox.Text = currentClientTelephone ?? string.Empty;
        ClientEmailBox.Text = currentClientEmail ?? string.Empty;
        DateVentePicker.SelectedDate = currentDateVente.ToLocalTime().Date;
        Loaded += async (_, _) =>
        {
            ClientNomBox.Focus();
            ClientNomBox.CaretIndex = ClientNomBox.Text.Length;
            await LoadClientsAsync();
            UpdateClientDue();
        };
    }

    private async Task LoadClientsAsync()
    {
        try
        {
            _clients = await _session.Api.GetClientsAsync();
        }
        catch (ApiException)
        {
            // Suggestions and the due warning are a convenience; a failed read must not
            // stop the edit itself.
        }
    }

    private void ClientNom_TextChanged(object sender, TextChangedEventArgs e)
    {
        SuggestClients();
        UpdateClientDue();
    }

    private void SuggestClients()
    {
        var text = ClientNomBox.Text.Trim();
        if (text.Length == 0 || !ClientNomBox.IsKeyboardFocusWithin)
        {
            ClientSuggestPopup.IsOpen = false;
            return;
        }

        var sameName = _clients.Count(c => string.Equals(c.Nom, text, StringComparison.CurrentCultureIgnoreCase));
        var matches = _clients
            .Where(c => c.IsActive && c.Nom.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .Where(c => sameName > 1 || !string.Equals(c.Nom, text, StringComparison.CurrentCultureIgnoreCase))
            .Take(8)
            .Select(c => new ClientSuggestion(c))
            .ToList();

        ClientSuggestList.ItemsSource = matches;
        ClientSuggestPopup.IsOpen = matches.Count > 0;
    }

    /// <summary>Shows what the typed name still owes, once it exactly matches one known
    /// client - the same warning Nouvelle Vente's own client field shows, so re-opening an
    /// old facture for edit does not hide that its client has unrelated unpaid sales too.</summary>
    private void UpdateClientDue()
    {
        var text = ClientNomBox.Text.Trim();
        var client = _clients.FirstOrDefault(c => string.Equals(c.Nom, text, StringComparison.CurrentCultureIgnoreCase));

        if (client is not { ResteDu: > 0 })
        {
            ClientDuePanel.Visibility = Visibility.Collapsed;
            return;
        }

        ClientDueText.Text = $"⚠ {client.Nom} doit encore {Money.Format(client.ResteDu)}"
            + (client.FacturesImpayees > 1 ? $" ({client.FacturesImpayees} vente(s) non soldée(s))." : ".");
        ClientDuePanel.Visibility = Visibility.Visible;
    }

    private void ClientNom_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!ClientSuggestPopup.IsOpen) return;

        if (e.Key == Key.Down)
        {
            ClientSuggestList.SelectedIndex = 0;
            (ClientSuggestList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClientSuggestPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void ClientSuggest_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (ClientSuggestList.SelectedItem is ClientSuggestion pick) PickClient(pick.Client);
    }

    private void ClientSuggest_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ClientSuggestList.SelectedItem is ClientSuggestion pick)
        {
            PickClient(pick.Client);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClientSuggestPopup.IsOpen = false;
            ClientNomBox.Focus();
        }
    }

    private void PickClient(ClientDto client)
    {
        ClientNomBox.Text = client.Nom;
        if (!string.IsNullOrWhiteSpace(client.Telephone)) ClientTelephoneBox.Text = client.Telephone;
        if (!string.IsNullOrWhiteSpace(client.Email)) ClientEmailBox.Text = client.Email;
        ClientSuggestPopup.IsOpen = false;
        ClientNomBox.Focus();
        ClientNomBox.CaretIndex = ClientNomBox.Text.Length;
        UpdateClientDue();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (DateVentePicker.SelectedDate is not { } date)
        {
            ErrorText.Text = "La date est requise.";
            ErrorPanel.Visibility = Visibility.Visible;
            return;
        }

        // Always sent, even blank - like ClientNom below, an empty string still reaches the
        // server (never plain null) so clearing the box actually clears the stored value
        // instead of the request's own null-means-unchanged rule silently keeping the old one.
        ClientNom = ClientNomBox.Text.Trim();
        DateVente = date;
        ClientTelephone = ClientTelephoneBox.Text.Trim();
        ClientEmail = ClientEmailBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
