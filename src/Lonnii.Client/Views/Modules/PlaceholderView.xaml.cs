using System.Windows.Controls;

namespace Lonnii.Client.Views.Modules;

/// <summary>
/// Stands in for a module whose screen has not been built yet.
///
/// The navigation is driven by the API, so a user's menu already lists every module
/// their privileges allow. This view keeps those entries honest: it names the module and
/// says plainly that it is still being built, rather than opening a blank panel.
/// </summary>
public partial class PlaceholderView : UserControl
{
    private PlaceholderView() => InitializeComponent();

    /// <summary>A placeholder for a menu entry that has no screen yet.</summary>
    public static PlaceholderView For(string label, string key)
    {
        var view = new PlaceholderView();
        view.TitleText.Text = label;
        view.MessageText.Text =
            $"Le module « {label} » n'est pas encore disponible dans la version Windows.\n\n" +
            "Vos droits d'accès sont déjà en place : ce module apparaîtra ici dès que son écran sera terminé.";
        return view;
    }

    /// <summary>A free-form message, for the states the shell needs to explain.</summary>
    public static PlaceholderView Message(string title, string message)
    {
        var view = new PlaceholderView();
        view.TitleText.Text = title;
        view.MessageText.Text = message;
        return view;
    }
}
