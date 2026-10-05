using System.Windows;
using System.Windows.Controls;

namespace Lonnii.Client.Common.Controls;

/// <summary>
/// A <see cref="PasswordBox"/> with a show/hide eye. Built to be swapped in without touching the
/// code behind that reads it: the same <see cref="Password"/>, <see cref="Clear"/>,
/// <see cref="SelectAll"/> and <see cref="PasswordChanged"/> a PasswordBox has.
/// </summary>
public partial class RevealPasswordBox : UserControl
{
    public static readonly RoutedEvent PasswordChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(PasswordChanged), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(RevealPasswordBox));

    private bool _syncing;

    public RevealPasswordBox()
    {
        InitializeComponent();
    }

    public event RoutedEventHandler PasswordChanged
    {
        add => AddHandler(PasswordChangedEvent, value);
        remove => RemoveHandler(PasswordChangedEvent, value);
    }

    /// <summary>What has been typed, whether or not it is currently visible.</summary>
    public string Password
    {
        get => Hidden.Password;
        set
        {
            if (Hidden.Password != value) Hidden.Password = value;
        }
    }

    public void Clear()
    {
        Hidden.Clear();

        // Back to hidden: a cleared field must not stay readable for whoever types next.
        EyeButton.IsChecked = false;
        ApplyVisibility();
    }

    public void SelectAll()
    {
        if (Shown.Visibility == Visibility.Visible) Shown.SelectAll();
        else Hidden.SelectAll();
    }

    public new bool Focus() =>
        Shown.Visibility == Visibility.Visible ? Shown.Focus() : Hidden.Focus();

    private void Hidden_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncing)
        {
            _syncing = true;
            Shown.Text = Hidden.Password;
            _syncing = false;
        }

        RaiseEvent(new RoutedEventArgs(PasswordChangedEvent, this));
    }

    private void Shown_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;

        _syncing = true;
        Hidden.Password = Shown.Text;
        _syncing = false;
    }

    private void Eye_Click(object sender, RoutedEventArgs e)
    {
        ApplyVisibility();

        EyeButton.ToolTip = EyeButton.IsChecked == true ? "Masquer le mot de passe" : "Afficher le mot de passe";

        // Keep typing where they were: focus goes to whichever field is now showing.
        if (EyeButton.IsChecked == true)
        {
            Shown.Focus();
            Shown.CaretIndex = Shown.Text.Length;
        }
        else
        {
            Hidden.Focus();
        }
    }

    private void ApplyVisibility()
    {
        var reveal = EyeButton.IsChecked == true;
        Shown.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        Hidden.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
    }
}
