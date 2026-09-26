using System.Windows;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>Collects the float counted into the drawer before the first sale of a session.</summary>
public partial class OpenCaisseDialog : Window
{
    public OpenCaisseRequest? Result { get; private set; }

    public OpenCaisseDialog()
    {
        InitializeComponent();
        MontantCashBox.Text = Money.FormatPlain(0m);
        MontantMobileBox.Text = Money.FormatPlain(0m);
        Loaded += (_, _) => { MontantCashBox.Focus(); MontantCashBox.SelectAll(); };

        // Grouped ("1 000") only once typing is done, same as VentesView's cart price boxes -
        // reformatting every keystroke would fight the caret position and the space the user
        // is trying to type past.
        MontantCashBox.LostFocus += (_, _) =>
        {
            if (Money.TryParse(MontantCashBox.Text, out decimal montant))
                MontantCashBox.Text = Money.FormatPlain(montant);
        };
        MontantMobileBox.LostFocus += (_, _) =>
        {
            if (Money.TryParse(MontantMobileBox.Text, out decimal montant))
                MontantMobileBox.Text = Money.FormatPlain(montant);
        };
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!Money.TryParse(MontantCashBox.Text, out decimal cash) || cash < 0)
        {
            ErrorText.Text = "Indiquez un montant valide pour les espèces.";
            MontantCashBox.Focus();
            return;
        }

        // Blank is the common case (most tills never open with a mobile-money float), so it
        // reads as zero rather than forcing every cashier to type "0" first.
        var mobileText = MontantMobileBox.Text;
        decimal mobile = 0;
        if (!string.IsNullOrWhiteSpace(mobileText) && (!Money.TryParse(mobileText, out mobile) || mobile < 0))
        {
            ErrorText.Text = "Indiquez un montant valide pour le mobile money.";
            MontantMobileBox.Focus();
            return;
        }

        Result = new OpenCaisseRequest(cash, mobile, string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim());
        DialogResult = true;
    }
}
