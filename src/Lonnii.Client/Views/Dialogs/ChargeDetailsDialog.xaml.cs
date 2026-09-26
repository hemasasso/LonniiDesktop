using System.Windows;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>Shows a recurring charge's schedule and every occurrence it has generated so far -
/// the desktop counterpart of Lonnii Business's own charge details modal
/// (client/src/components/baro/gestion/charges/Charges.jsx's <c>openDetailsModal</c>).</summary>
public partial class ChargeDetailsDialog : Window
{
    private sealed record GeneratedRow(ChargeDto Charge)
    {
        public DateTime Date => Charge.Date;
        public string MontantDisplay => Money.Format(Charge.Montant);
    }

    public ChargeDetailsDialog(ChargeDetailsResponse details)
    {
        InitializeComponent();

        var source = details.SourceCharge;
        HeaderText.Text = source.Description;
        HeaderHint.Text = details.ScheduleDescription;

        MontantText.Text = Money.Format(source.Montant);
        CategorieText.Text = source.Categorie;
        NextText.Text = details.NextScheduledDate?.ToString("dd/MM/yyyy") ?? "—";
        EndText.Text = source.RecurringEndDate?.ToString("dd/MM/yyyy") ?? "—";

        var rows = details.GeneratedCharges.Select(c => new GeneratedRow(c)).ToList();
        GeneratedGrid.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
