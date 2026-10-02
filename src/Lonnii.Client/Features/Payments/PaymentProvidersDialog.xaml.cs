using System.Windows;
using System.Windows.Controls;

namespace Lonnii.Client.Features.Payments;

/// <summary>Parametres → Moyens de paiement: for each kind of non-cash payment, any number of
/// accounts, each switched on or off and holding its own merchant details. Reachable only once
/// <see cref="PaymentProviderRegistry.SettingsVisible"/> is on.</summary>
public partial class PaymentProvidersDialog : Window
{
    private readonly Dictionary<string, List<PaymentProviderSettings>> _saved = PaymentSettingsStore.Load();

    /// <summary>What is on screen right now, per provider, so Enregistrer reads the controls once.</summary>
    private readonly Dictionary<string, List<AccountEditor>> _editors = [];

    private sealed record AccountEditor(
        PaymentProviderSettings Account, TextBox Label, CheckBox Enabled, Dictionary<string, Control> Fields, Border Card);

    public PaymentProvidersDialog()
    {
        InitializeComponent();

        foreach (var provider in PaymentProviderRegistry.Planned)
            Cards.Children.Add(BuildProviderCard(provider));
    }

    private Border BuildProviderCard(PaymentProviderInfo provider)
    {
        var implemented = PaymentProviderRegistry.IsImplemented(provider.Id);
        _editors[provider.Id] = [];

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = provider.Name, FontWeight = FontWeights.SemiBold, FontSize = 14 });

        var description = new TextBlock { Text = provider.Description, TextWrapping = TextWrapping.Wrap };
        description.SetResourceReference(TextBlock.StyleProperty, "PageSubtitle");
        panel.Children.Add(description);

        var status = new TextBlock
        {
            Text = implemented
                ? "Intégration disponible."
                : "Intégration à venir : les réglages sont enregistrés, mais aucun paiement n'est encore envoyé.",
            Margin = new Thickness(0, 4, 0, 0),
        };
        status.SetResourceReference(TextBlock.StyleProperty, "PageSubtitle");
        panel.Children.Add(status);

        var accounts = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(accounts);

        foreach (var account in _saved.GetValueOrDefault(provider.Id) ?? [])
            AddAccount(provider, accounts, account, implemented);

        var add = new Button { Content = "+ Ajouter un compte", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        add.SetResourceReference(StyleProperty, "SecondaryButton");
        add.Click += (_, _) => AddAccount(provider, accounts, new PaymentProviderSettings(), implemented);
        panel.Children.Add(add);

        var card = new Border
        {
            Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(0, 0, 0, 12),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Child = panel,
        };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        return card;
    }

    private void AddAccount(PaymentProviderInfo provider, StackPanel host, PaymentProviderSettings account, bool implemented)
    {
        var panel = new StackPanel();

        var header = new DockPanel();
        var remove = new Button { Content = "Supprimer", Padding = new Thickness(8, 2, 8, 2) };
        remove.SetResourceReference(StyleProperty, "SecondaryButton");
        DockPanel.SetDock(remove, Dock.Right);
        header.Children.Add(remove);

        var enabled = new CheckBox
        {
            Content = "Activer", IsChecked = account.Enabled,
            ToolTip = implemented ? null : "Enregistré, mais rien ne sera débité tant que l'intégration n'est pas branchée.",
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(enabled);
        panel.Children.Add(header);

        panel.Children.Add(FieldLabel("Nom du compte"));
        var label = new TextBox
        {
            Text = account.Label, Padding = new Thickness(8, 6, 8, 6),
            ToolTip = "Ce que verra le caissier, ex. « Orange Money caisse 1 »",
        };
        panel.Children.Add(label);

        var fields = new Dictionary<string, Control>();
        foreach (var field in provider.Fields)
        {
            panel.Children.Add(FieldLabel(field.Label));

            var stored = account.Values.GetValueOrDefault(field.Key) ?? string.Empty;
            Control input = field.Secret
                ? new PasswordBox { Password = PaymentSettingsStore.Unprotect(stored), Padding = new Thickness(8, 6, 8, 6) }
                : new TextBox { Text = stored, Padding = new Thickness(8, 6, 8, 6), ToolTip = field.Hint };
            panel.Children.Add(input);
            fields[field.Key] = input;
        }

        var card = new Border
        {
            Padding = new Thickness(12, 10, 12, 12), Margin = new Thickness(0, 6, 0, 0),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Child = panel,
        };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        host.Children.Add(card);

        var editor = new AccountEditor(account, label, enabled, fields, card);
        _editors[provider.Id].Add(editor);

        remove.Click += (_, _) =>
        {
            host.Children.Remove(card);
            _editors[provider.Id].Remove(editor);
        };
    }

    private static TextBlock FieldLabel(string text)
    {
        var block = new TextBlock { Text = text, Margin = new Thickness(0, 10, 0, 0) };
        block.SetResourceReference(TextBlock.StyleProperty, "FieldLabel");
        return block;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var all = new Dictionary<string, List<PaymentProviderSettings>>();

        foreach (var provider in PaymentProviderRegistry.Planned)
        {
            var accounts = new List<PaymentProviderSettings>();
            foreach (var editor in _editors[provider.Id])
            {
                var account = new PaymentProviderSettings
                {
                    Id = editor.Account.Id,
                    Label = editor.Label.Text.Trim(),
                    Enabled = editor.Enabled.IsChecked == true,
                };

                foreach (var field in provider.Fields)
                {
                    var value = editor.Fields[field.Key] switch
                    {
                        PasswordBox box => PaymentSettingsStore.Protect(box.Password),
                        TextBox box => box.Text.Trim(),
                        _ => string.Empty,
                    };
                    if (value.Length > 0) account.Values[field.Key] = value;
                }

                if (account.Label.Length == 0)
                {
                    MessageBox.Show(this, $"Donnez un nom à chaque compte « {provider.Name} ».", "Moyens de paiement",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    editor.Label.Focus();
                    return;
                }

                accounts.Add(account);
            }

            if (accounts.Count > 0) all[provider.Id] = accounts;
        }

        PaymentSettingsStore.Save(all);
        DialogResult = true;
    }
}
