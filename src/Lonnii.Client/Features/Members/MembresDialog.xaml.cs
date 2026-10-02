using Lonnii.Client.Services;

namespace Lonnii.Client.Features.Members;

/// <summary>
/// Hosts <see cref="MembersView"/> as a resizable window, opened from Paramètres' "MEMBRES ET
/// PRIVILÈGES" section rather than from a top-bar pill - see the "no Options pill" comment on
/// <c>AppMenu.Espace</c>. A plain window rather than an embedded panel: <see cref="MembersView"/>
/// is a full screen in its own right (toolbar, grid, its own scrolling), and squeezing it into
/// Paramètres' single outer ScrollViewer alongside every other section would leave its grid with
/// no bounded height to size against.
/// </summary>
public partial class MembresDialog : System.Windows.Window
{
    public MembresDialog(AppSession session)
    {
        InitializeComponent();
        Host.Children.Add(new MembersView(session));
    }
}
