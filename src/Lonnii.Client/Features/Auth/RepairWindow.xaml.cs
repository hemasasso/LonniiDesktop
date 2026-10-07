using System.Windows;
using Lonnii.Client.Services;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Features.Auth;

/// <summary>
/// The only way to point this till at an address it could not find on its own: a code we
/// issued for this till and that address. See <see cref="RepairCode"/>.
/// </summary>
public partial class RepairWindow : Window
{
    public RepairWindow()
    {
        InitializeComponent();

        _copyReset = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _copyReset.Tick += (_, _) =>
        {
            _copyReset.Stop();
            CopyButton.Content = "Copier";
        };

        TagBox.Text = RepairCode.DeviceTag(DeviceIdentity.Current);
    }

    private void CopyTag_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TagBox.Text); }
        catch (System.Runtime.InteropServices.COMException) { return; /* clipboard busy: it can be read off the screen */ }

        // Say so on the button itself, then put the label back.
        CopyButton.Content = "Copié ✓";
        _copyReset.Stop();
        _copyReset.Start();
    }

    private readonly System.Windows.Threading.DispatcherTimer _copyReset;

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        if (address.Length == 0) { Show("Indiquez l'adresse du serveur."); return; }

        var result = RepairCode.Check(CodeBox.Text, DeviceIdentity.Current, address, DateTimeOffset.UtcNow, checkExpiry: true);
        if (result != RepairCodeResult.Valid)
        {
            Show(result switch
            {
                RepairCodeResult.Expired => "Ce code a expiré. Demandez-en un nouveau à Lonnii.",
                RepairCodeResult.WrongDevice => "Ce code a été émis pour un autre poste.",
                RepairCodeResult.WrongAddress => "Ce code ne correspond pas à cette adresse.",
                _ => "Code invalide. Vérifiez qu'il est recopié en entier."
            });
            return;
        }

        App.Settings.RepairHost = address;
        App.Settings.RepairCode = CodeBox.Text.Trim();
        App.Settings.HostAddress = address;

        // We have just authorised a different server, so the one this till was bound to no
        // longer applies; it binds to the new one on its next connection.
        App.Settings.PinnedHostId = null;
        App.Settings.Save();

        DialogResult = true;
    }

    private void Show(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
