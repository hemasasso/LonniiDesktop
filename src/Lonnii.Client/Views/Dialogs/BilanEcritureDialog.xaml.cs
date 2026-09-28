using System.Windows;
using System.Windows.Controls;
using Lonnii.Shared.Comptabilite;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>Creates or edits a manual écriture on a bilan account. Says, as the amounts are
/// typed, whether the entry raises or lowers the chosen account's balance - the part of
/// debit/credit that is easy to get backwards.</summary>
public partial class BilanEcritureDialog : Window
{
    private sealed record CompteChoice(BilanCompteDto Compte)
    {
        public override string ToString() => $"{Compte.NumeroCompte} — {Compte.Libelle}";
    }

    public SaveBilanEcritureRequest? Result { get; private set; }

    public BilanEcritureDialog(IReadOnlyList<BilanCompteDto> comptes, BilanEcritureDto? existing, int? compteId = null)
    {
        InitializeComponent();

        var choices = comptes.Select(c => new CompteChoice(c)).ToList();
        CompteCombo.ItemsSource = choices;

        if (existing is null)
        {
            HeaderText.Text = "Nouvelle écriture";
            DatePicker.SelectedDate = DateTime.Today;
            CompteCombo.SelectedItem = choices.FirstOrDefault(c => c.Compte.Id == compteId);
        }
        else
        {
            HeaderText.Text = "Modifier l'écriture";
            CompteCombo.SelectedItem = choices.FirstOrDefault(c => c.Compte.Id == existing.CompteId);
            DatePicker.SelectedDate = existing.DateEcriture.ToDateTime(TimeOnly.MinValue);
            LibelleBox.Text = existing.Libelle;
            DebitBox.Text = Money.FormatPlain(existing.MontantDebit);
            CreditBox.Text = Money.FormatPlain(existing.MontantCredit);
            ReferenceBox.Text = existing.Reference ?? string.Empty;
            NotesBox.Text = existing.Notes ?? string.Empty;
        }

        foreach (var box in new[] { DebitBox, CreditBox })
        {
            box.TextChanged += (_, _) => Refresh();
            box.LostFocus += (_, _) =>
            {
                if (Money.TryParse(box.Text, out decimal v)) box.Text = Money.FormatPlain(v);
            };
        }
        CompteCombo.SelectionChanged += (_, _) => Refresh();

        Refresh();
        Loaded += (_, _) => (CompteCombo.SelectedItem is null ? (Control)CompteCombo : LibelleBox).Focus();
    }

    private void Refresh()
    {
        if (CompteCombo.SelectedItem is not CompteChoice { Compte: var compte })
        {
            CompteHint.Text = string.Empty;
            EffetText.Text = string.Empty;
            return;
        }

        var actif = TypesCompteBilan.IsActif(compte.TypeCompte);
        CompteHint.Text = $"{TypesCompteBilan.Label(compte.TypeCompte)} — "
                          + (actif ? "un débit augmente ce compte, un crédit le diminue."
                                   : "un crédit augmente ce compte, un débit le diminue.");

        var debit = Money.TryParse(DebitBox.Text, out decimal d) ? d : 0m;
        var credit = Money.TryParse(CreditBox.Text, out decimal c) ? c : 0m;
        var effet = actif ? debit - credit : credit - debit;

        EffetText.Text = effet switch
        {
            > 0 => $"Le solde du compte {compte.NumeroCompte} augmentera de {Money.Format(effet)}.",
            < 0 => $"Le solde du compte {compte.NumeroCompte} diminuera de {Money.Format(-effet)}.",
            _ => string.Empty,
        };
        EffetText.SetResourceReference(TextBlock.ForegroundProperty, effet >= 0 ? "Success" : "Warning");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (CompteCombo.SelectedItem is not CompteChoice { Compte: var compte })
        {
            ErrorText.Text = "Choisissez un compte.";
            CompteCombo.Focus();
            return;
        }

        if (DatePicker.SelectedDate is not { } date)
        {
            ErrorText.Text = "La date est requise.";
            DatePicker.Focus();
            return;
        }

        var libelle = LibelleBox.Text.Trim();
        if (libelle.Length == 0)
        {
            ErrorText.Text = "Le libellé est requis.";
            LibelleBox.Focus();
            return;
        }

        if (!TryAmount(DebitBox, out var debit) || !TryAmount(CreditBox, out var credit))
        {
            ErrorText.Text = "Montant invalide.";
            return;
        }

        if (debit == 0 && credit == 0)
        {
            ErrorText.Text = "Indiquez un montant au débit ou au crédit.";
            DebitBox.Focus();
            return;
        }

        static string? Text(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();

        Result = new SaveBilanEcritureRequest(
            compte.Id, DateOnly.FromDateTime(date), libelle, debit, credit, Text(ReferenceBox), Text(NotesBox));
        DialogResult = true;
    }

    private static bool TryAmount(TextBox box, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(box.Text)) return true;
        return Money.TryParse(box.Text, out value) && value >= 0;
    }
}
