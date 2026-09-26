using System.Windows;
using System.Windows.Controls;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>Picks how a closed session's non-zero écart gets closed out.</summary>
public partial class ResolveEcartDialog : Window
{
    private sealed record TypeOption(string Value, string Label, string Hint);

    private static readonly TypeOption[] Types =
    [
        new(EcartResolutionTypes.Justified, "Justifié",
            "L'écart est expliqué (arrondi, erreur de rendu monnaie...) et reste enregistré tel quel."),
        new(EcartResolutionTypes.WrittenOff, "Passé en perte",
            "L'écart est accepté comme une perte (ou un gain) et reste enregistré tel quel."),
        new(EcartResolutionTypes.Adjusted, "Ajusté",
            "Corrige le montant compté pour ramener l'écart à zéro."),
    ];

    public ResolveEcartRequest? Result { get; private set; }

    public ResolveEcartDialog(decimal ecart)
    {
        InitializeComponent();

        SubtitleText.Text = ecart > 0
            ? $"Excédent constaté : {Money.Format(ecart)}"
            : $"Manque constaté : {Money.Format(-ecart)}";

        TypeBox.ItemsSource = Types;
        TypeBox.SelectionChanged += (_, _) => TypeHint.Text = ((TypeOption)TypeBox.SelectedItem).Hint;
        TypeBox.SelectedIndex = 0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var selected = (TypeOption)TypeBox.SelectedItem;
        Result = new ResolveEcartRequest(selected.Value, string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim());
        DialogResult = true;
    }
}
