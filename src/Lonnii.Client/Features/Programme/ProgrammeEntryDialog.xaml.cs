using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Programme;

/// <summary>Sets or clears one worker's one day on the Programme board.</summary>
public partial class ProgrammeEntryDialog : Window
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private readonly string _userId;
    private readonly DateOnly _date;
    private readonly bool _hadExisting;

    public SaveProgrammeEntryRequest? Result { get; private set; }

    /// <summary>True when the user chose to clear an existing entry rather than save one.</summary>
    public bool ClearRequested { get; private set; }

    public ProgrammeEntryDialog(string userId, string userName, DateOnly date, ProgrammeEntryDto? existing)
    {
        _userId = userId;
        _date = date;
        _hadExisting = existing is not null;
        InitializeComponent();

        HeaderText.Text = existing is null ? "Nouvelle journée" : "Modifier la journée";
        HeaderHint.Text = $"{userName} — {date.ToString("dddd d MMMM yyyy", French)}";

        ClearButton.Visibility = _hadExisting ? Visibility.Visible : Visibility.Collapsed;

        if (existing is not null)
        {
            foreach (ComboBoxItem item in TypeCombo.Items)
                if ((string)item.Tag == existing.Type) { TypeCombo.SelectedItem = item; break; }

            HeureDebutPicker.Value = existing.HeureDebut;
            HeureFinPicker.Value = existing.HeureFin;
            NoteBox.Text = existing.Note ?? string.Empty;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ClearRequested = true;
        DialogResult = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var heureDebut = HeureDebutPicker.Value;
        var heureFin = HeureFinPicker.Value;

        if (heureDebut is not null && heureFin is not null && heureFin <= heureDebut)
        {
            ErrorText.Text = "L'heure de fin doit suivre l'heure de début.";
            HeureFinPicker.Focus();
            return;
        }

        var type = (string)((ComboBoxItem)TypeCombo.SelectedItem).Tag;
        var note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim();

        Result = new SaveProgrammeEntryRequest(_userId, _date, type, heureDebut, heureFin, note);
        DialogResult = true;
    }
}
