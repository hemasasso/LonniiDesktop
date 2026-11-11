using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Features.Ventes;

/// <summary>
/// The shop's serveurs (or whatever it calls them - the window takes the label from
/// Paramètres Reçu et Facture). Add, rename, deactivate; never delete, since past sales
/// keep the name. Anyone who sells may add a name; renaming and deactivating need
/// "Gérer les clients", as on the server.
/// </summary>
public partial class ServeurManagerDialog : Window
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private readonly AppSession _session;
    private readonly bool _canManage;
    private readonly string _label;
    private ServeurDto? _editing;

    private sealed record Row(ServeurDto Serveur)
    {
        public string StatutDisplay => Serveur.IsActive ? "actif" : "désactivé";
        public string CreatedDisplay => Serveur.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy", French);
    }

    /// <param name="label">Singular, e.g. "Serveur".</param>
    /// <param name="labelPlural">Plural, e.g. "Serveurs" - the window's title.</param>
    public ServeurManagerDialog(AppSession session, string label, string labelPlural)
    {
        _session = session;
        _canManage = session.Can(Priv.Gestion.ManageClients);
        _label = label;
        InitializeComponent();

        Title = TitleText.Text = labelPlural;
        EmptyTitle.Text = $"Aucun {label.ToLower(French)}";
        if (!_canManage)
            ToggleActiveButton.ToolTip = "Nécessite le privilège « Gérer les clients »";

        Loaded += async (_, _) =>
        {
            await LoadAsync();
            NomBox.Focus();
        };
    }

    private async Task LoadAsync()
    {
        try
        {
            var serveurs = await _session.Api.GetServeursAsync();
            ServeurGrid.ItemsSource = serveurs.Select(s => new Row(s)).ToList();
            EmptyPanel.Visibility = serveurs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var actifs = serveurs.Count(s => s.IsActive);
            SummaryText.Text = $"{actifs} actif(s)"
                               + (serveurs.Count > actifs ? $", {serveurs.Count - actifs} désactivé(s)" : string.Empty)
                               + " - proposés à la caisse sur chaque vente";
            ErrorText.Text = string.Empty;
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
        }
        Grid_SelectionChanged(this, null!);
    }

    private ServeurDto? Selected => (ServeurGrid.SelectedItem as Row)?.Serveur;

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var serveur = Selected;
        ToggleActiveButton.IsEnabled = _canManage && serveur is not null;
        ToggleActiveButton.Content = serveur?.IsActive == false ? "Activer" : "Désactiver";

        // Selecting a row loads it into the form for renaming - only for someone allowed to.
        if (serveur is not null && _canManage) StartEdit(serveur);
    }

    private void StartEdit(ServeurDto serveur)
    {
        _editing = serveur;
        NomBox.Text = serveur.Nom;
        TelephoneBox.Text = serveur.Telephone ?? string.Empty;
        SaveButton.Content = "Enregistrer";
        CancelEditButton.Visibility = Visibility.Visible;
    }

    private void ResetForm()
    {
        _editing = null;
        NomBox.Clear();
        TelephoneBox.Clear();
        SaveButton.Content = "Ajouter";
        CancelEditButton.Visibility = Visibility.Collapsed;
        NomBox.Focus();
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        ServeurGrid.SelectedItem = null;
        ResetForm();
    }

    private void Form_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Save_Click(sender, e);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var nom = NomBox.Text.Trim();
        if (nom.Length == 0)
        {
            ErrorText.Text = $"Saisissez le nom du {_label.ToLower(French)}.";
            NomBox.Focus();
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            var telephone = string.IsNullOrWhiteSpace(TelephoneBox.Text) ? null : TelephoneBox.Text.Trim();
            if (_editing is { } editing)
                await _session.Api.UpdateServeurAsync(editing.Id, new SaveServeurRequest(nom, telephone, editing.IsActive));
            else
                await _session.Api.CreateServeurAsync(new SaveServeurRequest(nom, telephone));

            ServeurGrid.SelectedItem = null;
            ResetForm();
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private async void ToggleActive_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;

        try
        {
            await _session.Api.UpdateServeurAsync(s.Id, new SaveServeurRequest(s.Nom, s.Telephone, IsActive: !s.IsActive));
            ServeurGrid.SelectedItem = null;
            ResetForm();
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }
}
