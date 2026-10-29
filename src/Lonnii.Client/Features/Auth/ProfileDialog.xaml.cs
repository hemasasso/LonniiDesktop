using System.Windows;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Auth;

/// <summary>The signed-in user's own name, username and phone. The email is shown, not edited:
/// it is how the account is known to Lonnii.</summary>
public partial class ProfileDialog : Window
{
    private readonly AppSession _session;

    public ProfileDialog(AppSession session)
    {
        _session = session;
        InitializeComponent();

        var user = session.User;
        EmailText.Text = string.IsNullOrEmpty(user?.Email) ? string.Empty : user!.Email;
        FirstNameBox.Text = user?.FirstName ?? string.Empty;
        LastNameBox.Text = user?.LastName ?? string.Empty;
        UsernameBox.Text = user?.Username ?? string.Empty;
        PhoneBox.Text = user?.Phone ?? string.Empty;

        Loaded += (_, _) => FirstNameBox.Focus();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        SaveButton.IsEnabled = false;
        try
        {
            var saved = await _session.Api.UpdateProfileAsync(new UpdateProfileRequest(
                FirstNameBox.Text, LastNameBox.Text, UsernameBox.Text, PhoneBox.Text));
            _session.ApplyProfile(saved);
            DialogResult = true;
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
}
