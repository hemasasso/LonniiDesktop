using System.Windows;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Ventes;

/// <summary>Shows a "Paiement Groupé" as a printable reçu - right after it is made, and again
/// from the history when a customer wants a copy.</summary>
public partial class GroupeReceiptDialog : Window
{
    private readonly Func<bool, System.Windows.Controls.Border> _build;
    private readonly string _jobName;

    /// <summary>Loads the shop's receipt configuration (cached after the first call) and shows
    /// the reçu, same single entry point idea as <see cref="VenteReceiptDialog.ShowForAsync"/>.</summary>
    public static async Task ShowForAsync(GroupePaiementDto payment, AppSession session, Window? owner)
    {
        var settings = await session.GetReceiptSettingsAsync();
        new GroupeReceiptDialog(payment, settings, session.ReceiptLogo, session.Groupe?.Nom ?? "Lonnii")
        {
            Owner = owner,
        }.ShowDialog();
    }

    public GroupeReceiptDialog(GroupePaiementDto payment, ReceiptSettingsDto settings, byte[]? logo, string fallbackCompany)
    {
        InitializeComponent();
        _build = forPrint => GroupeReceiptDocument.Build(payment, settings, logo, fallbackCompany, forPrint);
        _jobName = $"Reçu groupé {payment.Date.ToLocalTime():yyyy-MM-dd HH:mm}";
        PaperHost.Content = _build(false);
    }

    private void Print_Click(object sender, RoutedEventArgs e) =>
        ReceiptDocument.Print(() => _build(true), singlePage: false, _jobName);
}
