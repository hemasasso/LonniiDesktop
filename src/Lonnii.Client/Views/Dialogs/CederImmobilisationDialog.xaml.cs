using System.Windows;
using System.Windows.Controls;
using Lonnii.Shared.Comptabilite;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>Records a sale or scrapping. Shows, before anything is confirmed, the book value
/// at the exit and the gain or loss it produces.</summary>
public partial class CederImmobilisationDialog : Window
{
    private readonly ImmobilisationDto _immo;
    private readonly IReadOnlyList<AmortissementEcheanceDto> _echeances;

    public CederImmobilisationRequest? Result { get; private set; }

    public CederImmobilisationDialog(ImmobilisationDto immo)
    {
        _immo = immo;
        _echeances = AmortissementCalculator.Calculer(
            immo.ValeurAcquisition, immo.ValeurResiduelle, immo.DureeAmortissement, immo.MethodeAmortissement,
            immo.TauxDegressif, immo.DateAcquisition, immo.DateMiseEnService);

        InitializeComponent();

        HeaderHint.Text = $"{immo.Nom} — acquise le {immo.DateAcquisition:dd/MM/yyyy} pour {Money.Format(immo.ValeurAcquisition)}";
        DatePicker.SelectedDate = DateTime.Today;
        DatePicker.DisplayDateStart = immo.DateAcquisition.ToDateTime(TimeOnly.MinValue);

        TypeCombo.SelectionChanged += (_, _) => Refresh();
        DatePicker.SelectedDateChanged += (_, _) => Refresh();
        ValeurBox.TextChanged += (_, _) => Refresh();
        ValeurBox.LostFocus += (_, _) =>
        {
            if (Money.TryParse(ValeurBox.Text, out decimal v)) ValeurBox.Text = Money.FormatPlain(v);
        };

        Refresh();
        Loaded += (_, _) => { ValeurBox.Focus(); ValeurBox.SelectAll(); };
    }

    private bool IsCession => (string)((ComboBoxItem)TypeCombo.SelectedItem).Tag == StatutsImmobilisation.Cede;

    private void Refresh()
    {
        ValeurPanel.Visibility = IsCession ? Visibility.Visible : Visibility.Collapsed;
        if (DatePicker.SelectedDate is not { } date)
        {
            VncText.Text = string.Empty;
            ResultText.Text = string.Empty;
            return;
        }

        var cumule = _echeances.Where(e => e.Annee <= date.Year).Sum(e => e.DotationAnnuelle);
        var vnc = _immo.ValeurAcquisition - cumule;
        var prix = IsCession && Money.TryParse(ValeurBox.Text, out decimal p) ? p : 0m;
        var gain = prix - vnc;

        VncText.Text = $"Valeur nette comptable fin {date.Year} : {Money.Format(vnc)}";
        ResultText.Text = gain >= 0 ? $"Plus-value : {Money.Format(gain)}" : $"Moins-value : {Money.Format(-gain)}";
        ResultText.SetResourceReference(TextBlock.ForegroundProperty, gain >= 0 ? "Success" : "Danger");
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (DatePicker.SelectedDate is not { } date)
        {
            ErrorText.Text = "La date de sortie est requise.";
            return;
        }

        var sortie = DateOnly.FromDateTime(date);
        if (sortie < _immo.DateAcquisition)
        {
            ErrorText.Text = "La sortie ne peut pas précéder l'acquisition.";
            return;
        }

        decimal? valeur = null;
        if (IsCession)
        {
            if (!Money.TryParse(ValeurBox.Text, out decimal v) || v < 0)
            {
                ErrorText.Text = "Prix de cession invalide.";
                ValeurBox.Focus();
                return;
            }
            valeur = v;
        }

        var motif = string.IsNullOrWhiteSpace(MotifBox.Text) ? null : MotifBox.Text.Trim();
        Result = new CederImmobilisationRequest(
            IsCession ? StatutsImmobilisation.Cede : StatutsImmobilisation.Reforme, sortie, valeur, motif);
        DialogResult = true;
    }
}
