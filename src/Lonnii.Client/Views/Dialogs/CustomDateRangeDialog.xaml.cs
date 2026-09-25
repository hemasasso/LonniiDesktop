using System.Windows;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>Picks a "Période personnalisée" date range for the ventes list filter - mirrors
/// Lonnii Business's date-range modal (Ventes.jsx's <c>showDateModal</c>).</summary>
public partial class CustomDateRangeDialog : Window
{
    public DateOnly DateDebut { get; private set; }
    public DateOnly DateFin { get; private set; }

    public CustomDateRangeDialog(DateOnly initialDebut, DateOnly initialFin)
    {
        InitializeComponent();
        DateDebutPicker.SelectedDate = initialDebut.ToDateTime(TimeOnly.MinValue);
        DateFinPicker.SelectedDate = initialFin.ToDateTime(TimeOnly.MinValue);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (DateDebutPicker.SelectedDate is not { } debut || DateFinPicker.SelectedDate is not { } fin)
        {
            ErrorText.Text = "Les deux dates sont requises.";
            ErrorPanel.Visibility = Visibility.Visible;
            return;
        }

        if (fin < debut)
        {
            ErrorText.Text = "La date de fin doit être postérieure à la date de début.";
            ErrorPanel.Visibility = Visibility.Visible;
            return;
        }

        DateDebut = DateOnly.FromDateTime(debut);
        DateFin = DateOnly.FromDateTime(fin);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
