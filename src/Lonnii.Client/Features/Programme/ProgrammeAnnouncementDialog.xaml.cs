using System.Windows;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Programme;

/// <summary>Creates or edits a Programme announcement - group-wide when
/// <see cref="WorkerOption.Id"/> is null, addressed to one worker otherwise.</summary>
public partial class ProgrammeAnnouncementDialog : Window
{
    /// <summary>One row of <see cref="WorkerCombo"/>. <paramref name="Id"/> null means
    /// "Tout le monde" - the group-wide option every list is seeded with.</summary>
    public sealed record WorkerOption(string? Id, string Nom);

    public SaveProgrammeAnnouncementRequest? Result { get; private set; }

    public ProgrammeAnnouncementDialog(
        IReadOnlyList<Shared.Contracts.GroupMemberDto> members,
        ProgrammeAnnouncementDto? existing,
        DateOnly defaultDate)
    {
        InitializeComponent();

        var options = new List<WorkerOption> { new(null, "Tout le monde") };
        options.AddRange(members.Select(m => new WorkerOption(m.IdUser, DisplayName(m))));
        WorkerCombo.ItemsSource = options;

        HeaderText.Text = existing is null ? "Nouvelle annonce" : "Modifier l'annonce";

        if (existing is null)
        {
            WorkerCombo.SelectedIndex = 0;
            DatePicker.SelectedDate = defaultDate.ToDateTime(TimeOnly.MinValue);
        }
        else
        {
            WorkerCombo.SelectedItem = options.FirstOrDefault(o => o.Id == existing.UserId) ?? options[0];
            DatePicker.SelectedDate = existing.Date.ToDateTime(TimeOnly.MinValue);
            TitreBox.Text = existing.Titre;
            MessageTextBox.Text = existing.Message;
        }

        Loaded += (_, _) => { TitreBox.Focus(); TitreBox.SelectAll(); };
    }

    private static string DisplayName(Shared.Contracts.GroupMemberDto m)
    {
        var full = $"{m.FirstName} {m.LastName}".Trim();
        return !string.IsNullOrWhiteSpace(full) ? full : m.Username ?? m.Email;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var titre = TitreBox.Text.Trim();
        var message = MessageTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(titre))
        {
            ErrorText.Text = "Le titre est requis.";
            TitreBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            ErrorText.Text = "Le message est requis.";
            MessageTextBox.Focus();
            return;
        }

        if (DatePicker.SelectedDate is not { } date)
        {
            ErrorText.Text = "La date est requise.";
            DatePicker.Focus();
            return;
        }

        var worker = (WorkerOption)WorkerCombo.SelectedItem;
        Result = new SaveProgrammeAnnouncementRequest(worker.Id, titre, message, DateOnly.FromDateTime(date));
        DialogResult = true;
    }
}
