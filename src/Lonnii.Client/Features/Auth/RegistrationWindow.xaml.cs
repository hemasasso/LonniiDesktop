using System.Windows;
using System.Windows.Input;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Auth;

/// <summary>
/// "Créer mon espace": a new shop registers with Lonnii itself, so we know - and approve -
/// everyone who uses the app. Three steps in one window:
///
/// <list type="number">
/// <item>shop name, email and password; a code is emailed;</item>
/// <item>the code is typed in, which creates the shop on our server as <em>pending</em>;</item>
/// <item>waiting for our approval. The password is asked for again here and never stored, and
/// the window can be closed and reopened days later - it resumes at this step.</item>
/// </list>
///
/// Returns true once the workspace exists on this machine.
/// </summary>
public partial class RegistrationWindow : Window
{
    private enum Step { Form, Code, Waiting }

    private readonly AppSession _session;
    private Step _step = Step.Form;
    private string? _requestId;
    private string _email = string.Empty;
    private string _shop = string.Empty;

    public RegistrationWindow(AppSession session)
    {
        _session = session;
        InitializeComponent();

        Loaded += async (_, _) => await ResumeAsync();
    }

    /// <summary>A shop that registered earlier and is still waiting goes straight to step 3.</summary>
    private async Task ResumeAsync()
    {
        try
        {
            var pending = await _session.Api.GetPendingRegistrationAsync();
            if (pending.Pending)
            {
                _email = pending.Email ?? string.Empty;
                _shop = pending.ShopName ?? string.Empty;
                Show(Step.Waiting);
                return;
            }
        }
        catch (ApiException)
        {
            // No answer yet is not a reason to stop: the form still works once the host is up.
        }

        Show(Step.Form);
    }

    private void Show(Step step)
    {
        _step = step;
        HideError();

        FormPanel.Visibility = step == Step.Form ? Visibility.Visible : Visibility.Collapsed;
        CodePanel.Visibility = step == Step.Code ? Visibility.Visible : Visibility.Collapsed;
        WaitingPanel.Visibility = step == Step.Waiting ? Visibility.Visible : Visibility.Collapsed;

        switch (step)
        {
            case Step.Form:
                TitleText.Text = "Créer mon espace";
                SubtitleText.Text = "Inscrivez votre boutique auprès de Lonnii. Un code de confirmation sera envoyé à votre adresse email.";
                PrimaryAction.Content = "Continuer";
                SecondaryAction.Content = "Annuler";
                ShopBox.Focus();
                break;

            case Step.Code:
                TitleText.Text = "Confirmez votre email";
                SubtitleText.Text = $"Un code à 6 chiffres a été envoyé à {_email}.";
                PrimaryAction.Content = "Valider";
                SecondaryAction.Content = "Retour";
                CodeBox.Clear();
                CodeBox.Focus();
                break;

            case Step.Waiting:
                TitleText.Text = "Inscription en attente";
                SubtitleText.Text = string.IsNullOrEmpty(_shop) ? string.Empty : $"Boutique : {_shop}";
                WaitingText.Text =
                    "Votre email est confirmé. Lonnii va vous contacter pour approuver votre inscription. " +
                    "Vous pouvez fermer cette fenêtre : revenez ici une fois contacté.";
                PrimaryAction.Content = "Vérifier l'approbation";
                SecondaryAction.Content = "Fermer";
                ActivationPasswordBox.Focus();
                break;
        }
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.Form: await StartAsync(); break;
            case Step.Code: await VerifyAsync(); break;
            case Step.Waiting: await ActivateAsync(); break;
        }
    }

    private async void Secondary_Click(object sender, RoutedEventArgs e)
    {
        if (_step == Step.Code)
        {
            Show(Step.Form);
            return;
        }

        DialogResult = false;
        await Task.CompletedTask;
    }

    // --- Step 1 -------------------------------------------------------------------

    private async Task StartAsync()
    {
        var shop = ShopBox.Text.Trim();
        var email = EmailBox.Text.Trim();
        var password = PasswordBox.Password;

        if (shop.Length < 2) { Fail("Indiquez le nom de la boutique."); ShopBox.Focus(); return; }
        if (!email.Contains('@')) { Fail("Indiquez une adresse email valide."); EmailBox.Focus(); return; }
        if (password.Length < 8) { Fail("Le mot de passe doit contenir au moins 8 caractères."); PasswordBox.Focus(); return; }
        if (password != ConfirmBox.Password) { Fail("Les deux mots de passe ne correspondent pas."); ConfirmBox.Focus(); return; }

        await RunAsync("Envoi du code…", async () =>
        {
            var started = await _session.Api.RegistrationStartAsync(new RegistrationStartRequest(
                shop, email, password, DeviceIdentity.Current, DeviceIdentity.FriendlyName));

            _requestId = started.RequestId;
            _email = email.ToLowerInvariant();
            _shop = shop;
            Show(Step.Code);
        });
    }

    // --- Step 2 -------------------------------------------------------------------

    private async Task VerifyAsync()
    {
        var code = CodeBox.Text.Trim();
        if (code.Length != 6) { Fail("Saisissez le code à 6 chiffres."); CodeBox.Focus(); return; }

        await RunAsync("Vérification…", async () =>
        {
            await _session.Api.RegistrationVerifyAsync(new SetupRegisterVerifyRequest(_requestId!, code, _email, _shop));

            // The password just typed is not kept: it is asked for once more at activation.
            PasswordBox.Clear();
            ConfirmBox.Clear();
            Show(Step.Waiting);
        });
    }

    // --- Step 3 -------------------------------------------------------------------

    private async Task ActivateAsync()
    {
        var password = ActivationPasswordBox.Password;
        if (password.Length == 0) { Fail("Saisissez votre mot de passe."); ActivationPasswordBox.Focus(); return; }

        await RunAsync("Vérification de l'approbation…", async () =>
        {
            var result = await _session.Api.ActivatePendingRegistrationAsync(password);

            MessageBox.Show(this,
                $"L'espace « {result.GroupName} » est prêt.\n\nConnectez-vous avec {result.AdminEmail}.",
                "Inscription approuvée", MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
        });
    }

    // --- Plumbing ------------------------------------------------------------------

    private async Task RunAsync(string busy, Func<Task> action)
    {
        HideError();
        BusyText.Text = busy;
        BusyText.Visibility = Visibility.Visible;
        PrimaryAction.IsEnabled = SecondaryAction.IsEnabled = false;
        Cursor = Cursors.Wait;

        try
        {
            await action();
        }
        catch (ApiException ex)
        {
            // The server's own wording: "en attente d'approbation", "trop de tentatives",
            // "code invalide" - each already says what to do next.
            Fail(ex.Message);
        }
        finally
        {
            BusyText.Visibility = Visibility.Collapsed;
            PrimaryAction.IsEnabled = SecondaryAction.IsEnabled = true;
            Cursor = null;
        }
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorPanel.Visibility = Visibility.Collapsed;
}
