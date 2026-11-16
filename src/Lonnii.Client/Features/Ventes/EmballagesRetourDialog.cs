using System.Windows;
using System.Windows.Controls;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Ventes;

/// <summary>
/// Empties brought back, typed per product. Each line shows what the client still owes and
/// the consigne one empty refunds (what they actually paid for that product). Used at the till
/// (the refund comes off the sale) and in Clients (paid out, so the mode is asked too).
/// </summary>
public sealed class EmballagesRetourDialog : Window
{
    private readonly List<(ConsigneSoldeDto Solde, TextBox Box)> _lines = [];
    private readonly TextBlock _total = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox? _mode;
    private readonly ReceiptSettingsDto? _settings;

    public IReadOnlyList<EmballageRetourLigne> Result { get; private set; } = [];
    public string? ModePaiement { get; private set; }

    /// <summary>Mixte: the part refunded in mobile money.</summary>
    public decimal? MontantMobile { get; private set; }
    private readonly StackPanel _mobilePanel = new();
    private readonly TextBox _mobileBox = new();

    public EmballagesRetourDialog(Window? owner, string client, IReadOnlyList<ConsigneSoldeDto> soldes,
        IReadOnlyList<EmballageRetourLigne>? current, ReceiptSettingsDto? settings, bool askMode)
    {
        Owner = owner;
        _settings = settings;
        Title = "Emballages rapportés";
        Width = 620; // room for "doit 2 + 1/2 · 3 000 FCFA / caisse · versée 7 500 FCFA"
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "Surface");

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = $"{client} : nombre rapporté par produit", FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        foreach (var t in root.Children.OfType<TextBlock>()) t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        for (var i = 0; i < soldes.Count; i++)
        {
            var s = soldes[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock
            {
                Text = (s.Contenance is > 0 ? $"{s.NomProduit} ({UnitWord(s.Unite)} de {s.Contenance})" : s.NomProduit)
                    // Interchangeable crates: any of these products' crates comes back on this line.
                    + (string.IsNullOrWhiteSpace(s.Produits) ? "" : $"\n🔗 {s.Produits}"),
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                ToolTip = string.IsNullOrWhiteSpace(s.Produits) ? null : $"Caisses interchangeables : le client peut rapporter une caisse de {s.Produits}.",
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var info = new TextBlock
            {
                // What one empty refunds, and how much consigne the client paid on all they owe.
                Text = $"doit {SaleCharges.CountLabel(s.Dus)}" + (s.ParUnite > 0
                    ? $" · {Money.Format(s.ParUnite)} / {UnitWord(s.Unite)} · versée {Money.Format(s.ConsigneRestante)}"
                    : " · sans consigne"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0), FontSize = 12,
            };
            info.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            var box = new TextBox
            {
                Text = current?.FirstOrDefault(c => c.ProductId == s.ProductId)?.Quantite is { } q and > 0 ? SaleCharges.CountLabel(q) : "",
                Margin = new Thickness(0, 3, 0, 3), TextAlignment = TextAlignment.Right,
            };
            box.TextChanged += (_, _) => UpdateTotal();
            // As the sale screen does: "2.5" or "2,5" typed shows back as "2 + 1/2" once the field is left.
            box.LostFocus += (_, _) =>
            {
                if (SaleCharges.TryParseCount(box.Text, out var typed) && typed > 0)
                    box.Text = SaleCharges.CountLabel(typed);
            };
            Grid.SetRow(label, i); Grid.SetRow(info, i); Grid.SetRow(box, i);
            Grid.SetColumn(info, 1); Grid.SetColumn(box, 2);
            grid.Children.Add(label); grid.Children.Add(info); grid.Children.Add(box);
            _lines.Add((s, box));
        }
        root.Children.Add(grid);

        // The usual case: the client brings back everything they owe - old empties and the ones for
        // today's purchase. One click fills every product with its full due; edit any line after.
        var all = new Button { Content = "Tout rapporté", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        all.SetResourceReference(StyleProperty, "SecondaryButton");
        all.Click += (_, _) =>
        {
            foreach (var (s, box) in _lines)
                box.Text = s.Dus > 0 ? SaleCharges.CountLabel(s.Dus) : "";
        };
        root.Children.Add(all);
        root.Children.Add(_total);

        if (askMode)
        {
            var modeLabel = new TextBlock { Text = "Rembourser la consigne en :", Margin = new Thickness(0, 12, 0, 4) };
            modeLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            root.Children.Add(modeLabel);
            _mode = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
            _mode.Items.Add(new ComboBoxItem { Content = "Espèces", Tag = "cash", IsSelected = true });
            _mode.Items.Add(new ComboBoxItem { Content = "Mobile money", Tag = "mobile_money" });
            _mode.Items.Add(new ComboBoxItem { Content = "Banque / carte", Tag = "carte" });
            _mode.Items.Add(new ComboBoxItem { Content = "Mixte (espèces + mobile money)", Tag = "mixte" });
            root.Children.Add(_mode);
            var mobileLabel = new TextBlock { Text = "Dont en mobile money (le reste en espèces) :", Margin = new Thickness(0, 8, 0, 4) };
            mobileLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            _mobilePanel.Children.Add(mobileLabel);
            _mobileBox.Width = 160;
            _mobileBox.HorizontalAlignment = HorizontalAlignment.Left;
            _mobilePanel.Children.Add(_mobileBox);
            _mobilePanel.Visibility = Visibility.Collapsed;
            root.Children.Add(_mobilePanel);
            MoneyInput.GroupAsYouType(_mobileBox);
            _mode.SelectionChanged += (_, _) => _mobilePanel.Visibility =
                (_mode.SelectedItem as ComboBoxItem)?.Tag as string == "mixte" ? Visibility.Visible : Visibility.Collapsed;
        }

        var error = _error;
        error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        root.Children.Add(error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Annuler", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        cancel.SetResourceReference(StyleProperty, "SecondaryButton");
        var ok = new Button { Content = "Valider", IsDefault = true };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        ok.Click += (_, _) =>
        {
            var result = new List<EmballageRetourLigne>();
            foreach (var (s, box) in _lines)
            {
                var text = box.Text.Trim();
                if (text.Length == 0) continue;
                if (!SaleCharges.TryParseCount(text, out var q))
                {
                    error.Text = $"{s.NomProduit} : nombre invalide (ex. 2, 1,5 ou 1 + 1/2 ; par quart).";
                    box.Focus();
                    return;
                }
                // More than owed: a credit for the client, only when the shop accepts it.
                if (_settings?.EmballagesCreditAccepte != true && q > s.Dus)
                {
                    error.Text = $"{s.NomProduit} : le client ne doit que {SaleCharges.CountLabel(s.Dus)}.";
                    box.Focus();
                    return;
                }
                if (q > 0) result.Add(new EmballageRetourLigne(s.ProductId, q));
            }
            // Brought back more than owed: kept as a credit when the shop accepts it - but a slip (the
            // consigne amount typed in a count box: "1500" for 1/2 caisse) must not pass silently.
            var plus = _lines.Sum(l => SaleCharges.TryParseCount(l.Box.Text, out var q) && q > l.Solde.Dus ? q - l.Solde.Dus : 0);
            if (plus > 0)
            {
                var unit = SaleCharges.SharedUnit(_lines.Select(l => l.Solde.Unite));
                var what = _settings?.Emballages(plus, unit) ?? SaleCharges.CountLabel(plus);
                if (MessageBox.Show(this,
                        $"Le client rapporte {what} de plus que ce qu'il doit.\nCe surplus sera gardé en crédit pour lui.\n\nC'est bien le nombre d'emballages (et non un montant) ?",
                        "Emballages en plus", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
            }
            Result = result;
            ModePaiement = (_mode?.SelectedItem as ComboBoxItem)?.Tag as string;
            if (ModePaiement == "mixte")
            {
                if (!Money.TryParse(_mobileBox.Text, out decimal mobile) || mobile < 0)
                {
                    error.Text = "Mixte : indiquez la part en mobile money.";
                    _mobileBox.Focus();
                    return;
                }
                MontantMobile = mobile;
            }
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        Content = root;
        UpdateTotal();
        Loaded += (_, _) => _lines.FirstOrDefault().Box?.Focus();
    }

    /// <summary>The unit set on the product in Stock ("caisse"), else the generic "emballage".</summary>
    private static string UnitWord(string? productUnit) =>
        (SaleCharges.RealUnit(productUnit) ?? "emballage").ToLower(System.Globalization.CultureInfo.CurrentCulture);

    private void UpdateTotal()
    {
        _error.Text = string.Empty; // the red message goes as soon as the numbers are changed
        var count = 0m;
        var extra = 0m;
        decimal refund = 0;
        foreach (var (s, box) in _lines)
        {
            if (!SaleCharges.TryParseCount(box.Text, out var q) || q <= 0) continue;
            count += q;
            extra += Math.Max(0, q - s.Dus);
            refund += s.Remboursement(q);
        }
        // All the products in "caisse" -> "2 caisses"; a mix of units -> "emballages".
        var unit = SaleCharges.SharedUnit(_lines.Select(l => l.Solde.Unite));
        var what = _settings?.Emballages(count, unit) ?? $"{count} emballage(s)";
        // The consigne still held for the client once these empties are refunded.
        var held = _lines.Sum(l => l.Solde.ConsigneRestante);
        var left = Math.Max(0, held - refund);
        _total.Text = $"Total : {what} · consigne à rendre : {Money.Format(refund)}"
            + (held > 0 ? $"\nConsigne versée restante : {Money.Format(left)} (sur {Money.Format(held)})" : "")
            + (extra > 0 ? $"\nEn plus de ce qui est dû : {_settings?.Emballages(extra, unit) ?? SaleCharges.CountLabel(extra)}{(_settings?.EmballagesCreditAccepte == true ? " → crédit pour le client" : " → refusé (emballages en avance non acceptés)")}" : "");
        _total.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
    }
}
