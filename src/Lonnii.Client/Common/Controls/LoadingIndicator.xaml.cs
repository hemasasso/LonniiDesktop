using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Lonnii.Client.Common.Controls;

/// <summary>The busy overlay shown while a view loads: a spinner and an animated
/// "Chargement…". Drop it inside a Border and toggle the Border's Visibility; the animation
/// runs only while it is visible, so the hidden panels cost nothing.</summary>
public partial class LoadingIndicator : UserControl
{
    public LoadingIndicator()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            var spin = (Storyboard)Resources["Spin"];
            if (IsVisible) spin.Begin(this, isControllable: true);
            else spin.Stop(this);
        };
    }
}
