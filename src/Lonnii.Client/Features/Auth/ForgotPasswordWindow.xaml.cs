using System.Windows;
using System.Windows.Input;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Auth;

/// <summary>
/// "Mot de passe oublié" for the shop owner, whose password nobody else in the shop may reset.
/// Two ways: a six-digit code Lonnii emails (needs the internet), or - for a shop that runs
/// offline - a code Lonnii signs and reads out over the phone, issued for the account tag this
/// window shows. Other members are reset by their administrator.
/// </summary>
public partial class ForgotPasswordWindow : Window
{
    private readonly AppSession _session;

    /// <summary>Minimum password length, matching what the API enforces.</summary>
    private const int MinimumPasswordLength = 8;

    private enum Step { Identify, EmailCode, OfflineCode }

    private Step _step = Step.Identify;
    private string? _requestId;
    private string _identifier = string.Empty;
    private readonly System.Windows.Threading.DispatcherTimer _copyReset;

    /// <summary>The account whose password was changed, to pre-fill sign-in.</summary>
    public string Identifier => _identifier;

    /// <summary>Resetting the account on the Lonnii online server (remote access), not on a shop's server.</summary>
    private readonly bool _online;

    public ForgotPasswordWindow(AppSession session, string? identifier, bool online = false)
    {
        _session = session;
        _online = online;
        InitializeComponent();

        if (online)
        {
            // The online server knows its accounts by email, and has no offline code to offer.
            OfflineLink.Visibility = Visibility.Collapsed;
            ShopNote.Visibility = Visibility.Collapsed;
            SubtitleText.Text = "Accès à distance : indiquez l'email de votre compte Lonnii. " +
                                "Le mot de passe de la boutique, lui, ne change pas.";
        }

        _copyReset = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _copyReset.Tick += (_, _) =>
        {
            _copyReset.Stop();
            CopyButton.Content = "Copier";
        };

        IdentifierBox.Text = identifier ?? string.Empty;
        UpdateHint();

        Loaded += (_, _) => IdentifierBox.Focus();
    }

    private void Password_Changed(object sender, RoutedEventArgs e) => UpdateHint();

    private void UpdateHint()
    {
        if (PasswordHint is null) return;

        var password = NewBox.Password;
        var confirm = ConfirmBox.Password;

        if (password.Length == 0)
            PasswordHint.Text = $"Au moins {MinimumPasswordLength} caractères.";
        else if (password.Length < MinimumPasswordLength)
            PasswordHint.Text = $"Encore {MinimumPasswordLength - password.Length} caractère(s) au minimum.";
        else if (confirm.Length > 0 && password != confirm)
            PasswordHint.Text = "Les deux mots de passe ne correspondent pas.";
        else
            PasswordHint.Text = "Mot de passe accepté.";
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        switch (_step)
        {
            case Step.Identify: await SendCodeAsync(); break;
            case Step.EmailCode: await ResetByEmailAsync(); break;
            case Step.OfflineCode: await ResetOfflineAsync(); break;
        }
    }

    private bool ReadIdentifier()
    {
        var identifier = IdentifierBox.Text.Trim();
        if (identifier.Length == 0)
        {
            Fail("Indiquez votre email ou nom d'utilisateur.", IdentifierBox);
            return false;
        }

        _identifier = identifier;
        return true;
    }

    private async Task SendCodeAsync()
    {
        if (!ReadIdentifier()) return;

        if (_online)
        {
            if (!_identifier.Contains('@'))
            {
                Fail("Indiquez l'adresse email de votre compte Lonnii.", IdentifierBox);
                return;
            }

            await BusyAsync(async () =>
            {
                var online = await _session.Api.OnlinePasswordResetStartAsync(_identifier);
                _requestId = online.RequestId;
                SubtitleText.Text = $"Si {_identifier} est un compte Lonnii, un code vient d'y être envoyé. Il est valable 15 minutes.";
                ShowStep(Step.EmailCode);
                CodeBox.Focus();
            });
            return;
        }

        await BusyAsync(async () =>
        {
            var started = await _session.Api.ForgotPasswordStartAsync(_identifier);
            _requestId = started.RequestId;

            SubtitleText.Text = $"Un code a été envoyé à {started.MaskedEmail}. Il est valable 15 minutes.";
            ShowStep(Step.EmailCode);
            CodeBox.Focus();
        });
    }

    /// <summary>No internet: show the account tag for the owner to read out to Lonnii.</summary>
    private async void Offline_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (!ReadIdentifier()) return;

        await BusyAsync(async () =>
        {
            var tag = await _session.Api.OfflineResetTagAsync(_identifier);
            TagBox.Text = tag.AccountTag;

            SubtitleText.Text = "Appelez Lonnii et communiquez l'identifiant ci-dessous. " +
                                "Le code qu'on vous donnera ne vaut que pour ce compte, une seule fois.";
            ShowStep(Step.OfflineCode);
            OfflineCodeBox.Focus();
        });
    }

    private void ShowStep(Step step)
    {
        _step = step;
        IdentifierPanel.Visibility = Visibility.Collapsed;
        EmailCodePanel.Visibility = step == Step.EmailCode ? Visibility.Visible : Visibility.Collapsed;
        OfflineCodePanel.Visibility = step == Step.OfflineCode ? Visibility.Visible : Visibility.Collapsed;
        PasswordPanel.Visibility = Visibility.Visible;
        NextButton.Content = "Changer le mot de passe";
    }

    private void CopyTag_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TagBox.Text); }
        catch (System.Runtime.InteropServices.COMException) { return; /* clipboard busy: it can be read off the screen */ }

        CopyButton.Content = "Copié ✓";
        _copyReset.Stop();
        _copyReset.Start();
    }

    private bool CheckNewPassword()
    {
        if (NewBox.Password.Length < MinimumPasswordLength)
        {
            Fail($"Le mot de passe doit contenir au moins {MinimumPasswordLength} caractères.", NewBox);
            return false;
        }

        if (NewBox.Password != ConfirmBox.Password)
        {
            Fail("Les deux mots de passe ne correspondent pas.", ConfirmBox);
            return false;
        }

        return true;
    }

    private async Task ResetByEmailAsync()
    {
        var code = CodeBox.Text.Trim();
        if (code.Length != 6)
        {
            Fail("Le code contient 6 chiffres.", CodeBox);
            return;
        }

        if (!CheckNewPassword()) return;

        await BusyAsync(async () =>
        {
            if (_online)
            {
                await _session.Api.OnlinePasswordResetVerifyAsync(
                    new PasswordResetVerifyRequest(_requestId!, _identifier, code, NewBox.Password));
                DialogResult = true;
                return;
            }

            await _session.Api.ForgotPasswordVerifyAsync(
                new ForgotPasswordVerifyRequest(_requestId!, _identifier, code, NewBox.Password));
            DialogResult = true;
        });
    }

    private async Task ResetOfflineAsync()
    {
        var code = OfflineCodeBox.Text.Trim();
        if (code.Length == 0)
        {
            Fail("Saisissez le code fourni par Lonnii.", OfflineCodeBox);
            return;
        }

        if (!CheckNewPassword()) return;

        await BusyAsync(async () =>
        {
            await _session.Api.OfflineResetApplyAsync(
                new OfflineResetApplyRequest(_identifier, code, NewBox.Password));
            DialogResult = true;
        });
    }

    private async Task BusyAsync(Func<Task> work)
    {
        NextButton.IsEnabled = false;
        Cursor = Cursors.Wait;
        try
        {
            await work();
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
        }
        finally
        {
            NextButton.IsEnabled = true;
            Cursor = null;
        }
    }

    private void Fail(string message, System.Windows.Controls.Control focus)
    {
        ErrorText.Text = message;
        focus.Focus();
    }
}
