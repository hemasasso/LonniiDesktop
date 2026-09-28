using System.Windows;
using System.Windows.Controls;
using Lonnii.Shared.Comptabilite;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>Creates or edits an account of the bilan or the compte de résultat. The table
/// cannot change once the account exists, and a default account keeps its type and
/// sous-type - the server enforces both too.</summary>
public partial class BilanCompteDialog : Window
{
    private sealed record TypeChoice(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    public SaveBilanCompteRequest? Result { get; private set; }

    public BilanCompteDialog(BilanCompteDto? existing, string tableType)
    {
        InitializeComponent();

        foreach (ComboBoxItem item in TableCombo.Items)
            if ((string)item.Tag == tableType) { TableCombo.SelectedItem = item; break; }

        FillTypes();
        TableCombo.SelectionChanged += (_, _) => FillTypes();

        if (existing is null)
        {
            HeaderText.Text = "Nouveau compte";
            HeaderHint.Text = "Ajoutez un compte au plan comptable de l'entreprise.";
        }
        else
        {
            HeaderText.Text = "Modifier le compte";
            HeaderHint.Text = $"{existing.NumeroCompte} — {existing.Libelle}";
            TableCombo.IsEnabled = false;

            NumeroBox.Text = existing.NumeroCompte;
            LibelleBox.Text = existing.Libelle;
            SousTypeBox.Text = existing.SousType ?? string.Empty;
            DescriptionBox.Text = existing.Description ?? string.Empty;
            TypeCombo.SelectedItem = TypeCombo.Items.Cast<TypeChoice>().FirstOrDefault(t => t.Value == existing.TypeCompte);

            if (existing.IsSystem)
            {
                TypeCombo.IsEnabled = false;
                SousTypeBox.IsEnabled = false;
                SystemNotice.Visibility = Visibility.Visible;
            }
        }

        Loaded += (_, _) => (existing is null ? NumeroBox : LibelleBox).Focus();
    }

    private string TableType => (string)((ComboBoxItem)TableCombo.SelectedItem).Tag;

    private void FillTypes()
    {
        var types = TableType == TablesCompte.Resultat
            ? TypesCompteResultat.All.Select(t => new TypeChoice(t, TypesCompteResultat.Label(t)))
            : TypesCompteBilan.All.Select(t => new TypeChoice(t, TypesCompteBilan.Label(t)));
        TypeCombo.ItemsSource = types.ToList();
        TypeCombo.SelectedIndex = 0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var numero = NumeroBox.Text.Trim();
        var libelle = LibelleBox.Text.Trim();

        if (numero.Length == 0)
        {
            ErrorText.Text = "Le numéro de compte est requis.";
            NumeroBox.Focus();
            return;
        }

        if (libelle.Length == 0)
        {
            ErrorText.Text = "Le libellé est requis.";
            LibelleBox.Focus();
            return;
        }

        if (TypeCombo.SelectedItem is not TypeChoice type)
        {
            ErrorText.Text = "Choisissez un type de compte.";
            return;
        }

        Result = new SaveBilanCompteRequest(
            TableType, numero, libelle, type.Value,
            string.IsNullOrWhiteSpace(SousTypeBox.Text) ? null : SousTypeBox.Text.Trim(),
            string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim());
        DialogResult = true;
    }
}
