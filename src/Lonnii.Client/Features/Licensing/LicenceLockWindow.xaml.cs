using System.Windows;
using System.Windows.Input;
using Lonnii.Client.Services;

namespace Lonnii.Client.Features.Licensing;

/// <summary>
/// The hard stop: shown when the host refuses the workspace because this shop has gone past
/// its offline deadline without reaching the licence server.
///
/// <para>
/// There is no way past it except renewing. "Réessayer" asks the host to reach the licence
/// server; if that succeeds the lock lifts, otherwise the server's own reason is shown - no
/// connection, an unpaid subscription, a machine that was revoked. Closing the window any
/// other way quits the application rather than leaving the till usable behind it.
/// </para>
/// </summary>
public partial class LicenceLockWindow : Window
{
    private static bool _open;

    private readonly AppSession _session = App.Session;
    private readonly string _groupId;
    private bool _renewed;

    private LicenceLockWindow(string groupId, string message)
    {
        InitializeComponent();
        _groupId = groupId;
        MessageText.Text = message;
        Closing += (_, _) =>
        {
            if (!_renewed) Application.Current.Shutdown();
        };
    }

    /// <summary>
    /// Blocks until the licence is renewed, and returns true when it was. Quitting ends the
    /// application instead. Several refused calls - or the group picker and the shell at once -
    /// can raise the lock together, so a second request while one is open returns false.
    /// </summary>
    public static bool ShowLocked(Window owner, string groupId, string message)
    {
        if (_open) return false;

        _open = true;
        try
        {
            var window = new LicenceLockWindow(groupId, message) { Owner = owner };
            window.ShowDialog();
            return window._renewed;
        }
        finally
        {
            _open = false;
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        ErrorPanel.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Wait;

        try
        {
            var status = await _session.Api.SyncLicenceAsync(_groupId);

            if (status.Expired)
            {
                ShowError(status.Message ?? "La licence n'a pas pu être renouvelée.");
                return;
            }

            _renewed = true;
            Close();
        }
        catch (ApiException ex)
        {
            // Carries the licence server's own wording: no connection, subscription expired,
            // machine no longer authorised.
            ShowError(ex.Message);
        }
        finally
        {
            Cursor = null;
            RetryButton.IsEnabled = true;
        }
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }
}
