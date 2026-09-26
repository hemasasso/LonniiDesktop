using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lonnii.Client.Services;
using Lonnii.Client.Views.Dialogs;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Lonnii.Client.Views.Modules;

/// <summary>
/// Gestion des Charges: business expenses, their categories, recurring "fixe" charges that
/// re-create themselves every month, and a year's analytics. Mirrors Lonnii Business's own
/// Charges module (backend/routes/gestionCharges.js) against the same <c>charges</c> and
/// <c>charges_categories</c> tables.
/// </summary>
public partial class ChargesView : UserControl
{
    private readonly AppSession _session;
    private string _activeTab = "charges";

    private List<ChargeCategoryDto> _categories = [];
    private List<ChargeDto> _charges = [];
    private bool _categoriesLoaded;

    private string _dateFilterTag = "month";
    private DateOnly? _dateDebut;
    private DateOnly? _dateFin;
    private bool _suppressDateFilterEvent;

    private readonly bool _canAdd;
    private readonly bool _canEdit;
    private readonly bool _canDelete;
    private readonly bool _canExport;
    private readonly bool _canViewAnalytics;
    private readonly bool _canManageCategories;

    private const string ModuleKey = "charges";
    private const string DefaultCategoryColor = "#94a3b8";

    /// <summary>Same 8-colour fallback palette as Stock/Ventes' own charts, used only for a
    /// category name that no longer matches any live <see cref="ChargeCategoryDto"/> (renamed
    /// or deleted since some older charge used it) - every category that still exists uses
    /// its own stored <see cref="ChargeCategoryDto.Color"/> instead, which is what makes a
    /// category read the same colour in every chart and the list, without needing the
    /// index-based matching Stock/Ventes rely on for product categories.</summary>
    private static readonly SKColor[] FallbackPalette =
    [
        new(0x25, 0x63, 0xEB), new(0x10, 0xB9, 0x81), new(0xF5, 0x9E, 0x0B), new(0xEF, 0x44, 0x44),
        new(0x8B, 0x5C, 0xF6), new(0xEC, 0x48, 0x99), new(0x06, 0xB6, 0xD4), new(0x84, 0xCC, 0x16),
    ];

    private sealed record ChargeRow(ChargeDto Charge, Brush CategoryBrush, Visibility CanEdit, Visibility CanDelete)
    {
        public string Description => Charge.Description;
        public DateTime Date => Charge.Date;
        public string Categorie => Charge.Categorie;
        public string TypeDisplay => Charge.TypeCharge == "fixe" ? "Fixe" : "Variable";
        public string MontantDisplay => Money.Format(Charge.Montant);
        public string RecurringDisplay => Charge.RecurringSourceId is not null ? "Auto"
            : Charge.IsRecurring ? "Source" : "—";
        public string? CreatedByName => Charge.CreatedByName;
    }

    private sealed record RecurringRow(ChargeDto Charge, Visibility CanEdit)
    {
        public string Description => Charge.Description;
        public string Categorie => Charge.Categorie;
        public string MontantDisplay => Money.Format(Charge.Montant);
        public string ScheduleDisplay => $"Création {DescribeRecurringDay(Charge.RecurringDay)}";
        public string EndDateDisplay => Charge.RecurringEndDate?.ToString("dd/MM/yyyy") ?? "—";
        public string StatusDisplay => Charge.RecurringActive ? "Active" : "Arrêtée";
        public string ToggleLabel => Charge.RecurringActive ? "Arrêter" : "Réactiver";
    }

    public ChargesView(AppSession session)
    {
        _session = session;
        _canAdd = _session.Can(Priv.Gestion.AddCharges);
        _canEdit = _session.Can(Priv.Gestion.EditCharges);
        _canDelete = _session.Can(Priv.Gestion.DeleteCharges);
        _canExport = _session.Can(Priv.Gestion.ExportCharges);
        _canViewAnalytics = _session.Can(Priv.Gestion.ViewChargesAnalytics);
        _canManageCategories = _session.Can(Priv.Gestion.ManageChargesCategories);
        InitializeComponent();

        SubtitleText.Text = _session.Groupe?.Nom;
        AddButton.Visibility = _canAdd ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.Visibility = _canExport ? Visibility.Visible : Visibility.Collapsed;
        AnalyticsTabButton.Visibility = _canViewAnalytics ? Visibility.Visible : Visibility.Collapsed;
        AddCategoryButton.Visibility = _canManageCategories ? Visibility.Visible : Visibility.Collapsed;

        var today = DateOnly.FromDateTime(DateTime.Now);
        _dateDebut = new DateOnly(today.Year, today.Month, 1);
        _dateFin = today;

        BuildYearCombo();
        ApplyTabVisuals();

        Loaded += async (_, _) =>
        {
            var initialTab = UiState.For(_session).Tabs.GetValueOrDefault(ModuleKey) is { } saved
                && (saved != "analytics" || _canViewAnalytics)
                ? saved : "charges";
            await SetActiveTabAsync(initialTab);
        };
    }

    // --- Tabs ---

    private async void Tab_Click(object sender, RoutedEventArgs e) =>
        await SetActiveTabAsync(((Button)sender).Tag as string ?? "charges");

    private async Task SetActiveTabAsync(string tab)
    {
        _activeTab = tab;
        ApplyTabVisuals();

        UiState.For(_session).Tabs[ModuleKey] = tab;
        UiState.Save();

        if (!_categoriesLoaded) await LoadCategoriesAsync();

        switch (tab)
        {
            case "charges": await LoadChargesAsync(); break;
            case "recurring": await LoadRecurringAsync(); break;
            case "categories": RenderCategories(); break;
            case "analytics": await LoadAnalyticsAsync(); break;
        }
    }

    private void ApplyTabVisuals()
    {
        var accent = (Brush)FindResource("Accent");
        var onAccent = (Brush)FindResource("TextOnAccent");
        var muted = (Brush)FindResource("TextMuted");

        void Apply(Button button, bool active)
        {
            button.Background = active ? accent : Brushes.Transparent;
            button.Foreground = active ? onAccent : muted;
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        Apply(ChargesTabButton, _activeTab == "charges");
        Apply(RecurringTabButton, _activeTab == "recurring");
        Apply(CategoriesTabButton, _activeTab == "categories");
        Apply(AnalyticsTabButton, _activeTab == "analytics");

        ChargesPanel.Visibility = _activeTab == "charges" ? Visibility.Visible : Visibility.Collapsed;
        RecurringPanel.Visibility = _activeTab == "recurring" ? Visibility.Visible : Visibility.Collapsed;
        CategoriesPanel.Visibility = _activeTab == "categories" ? Visibility.Visible : Visibility.Collapsed;
        AnalyticsPanel.Visibility = _activeTab == "analytics" ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- Categories (also feeds the Charges tab's filter and every colour swatch) ---

    private async Task LoadCategoriesAsync()
    {
        try
        {
            _categories = await _session.Api.GetChargeCategoriesAsync();
            _categoriesLoaded = true;

            var sentinel = new ChargeCategoryDto(0, "Toutes catégories", null, DefaultCategoryColor);
            CategoryFilterCombo.ItemsSource = new[] { sentinel }.Concat(_categories).ToList();
            CategoryFilterCombo.SelectedIndex = 0;
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private void RenderCategories()
    {
        CategoriesWrap.Children.Clear();

        foreach (var category in _categories)
        {
            var card = new Border
            {
                Width = 220, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(14, 12, 14, 12),
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Background = (Brush)FindResource("Surface"), BorderBrush = (Brush)FindResource("Border"),
            };

            var content = new DockPanel();

            if (_canManageCategories)
            {
                var remove = new Button
                {
                    Style = (Style)FindResource("IconButton"), Content = "✕", Width = 22, Height = 22, FontSize = 10,
                    ToolTip = "Supprimer la catégorie",
                };
                remove.Click += async (_, _) => await DeleteCategoryAsync(category);
                DockPanel.SetDock(remove, Dock.Right);
                content.Children.Add(remove);
            }

            var swatch = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
                Background = BrushFromHex(category.Color), VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 10, 0),
            };
            DockPanel.SetDock(swatch, Dock.Left);
            content.Children.Add(swatch);

            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = category.Nom, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrWhiteSpace(category.Description))
            {
                text.Children.Add(new TextBlock
                {
                    Text = category.Description, TextWrapping = TextWrapping.Wrap, FontSize = 11,
                    Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(0, 2, 0, 0),
                });
            }
            content.Children.Add(text);

            card.Child = content;
            if (_canManageCategories)
            {
                card.Cursor = System.Windows.Input.Cursors.Hand;
                card.MouseLeftButtonUp += async (_, _) => await EditCategoryAsync(category);
            }
            CategoriesWrap.Children.Add(card);
        }
    }

    private async void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ChargeCategoryDialog(null) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is not { } request) return;

        try
        {
            await _session.Api.SaveChargeCategoryAsync(request);
            await LoadCategoriesAsync();
            RenderCategories();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Catégorie", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task EditCategoryAsync(ChargeCategoryDto category)
    {
        var dialog = new ChargeCategoryDialog(category) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is not { } request) return;

        // A rename creates a new category row rather than editing this one server-side
        // (SaveCategoryAsync upserts by name), which would orphan this category's colour on
        // every charge already using the old name - so a rename is refused here instead.
        if (request.Nom != category.Nom)
        {
            MessageBox.Show(Window.GetWindow(this),
                "Le nom d'une catégorie ne peut pas être modifié une fois créée, pour ne pas déconnecter les charges déjà classées avec ce nom. Créez une nouvelle catégorie à la place.",
                "Catégorie", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await _session.Api.SaveChargeCategoryAsync(request);
            await LoadCategoriesAsync();
            RenderCategories();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Catégorie", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task DeleteCategoryAsync(ChargeCategoryDto category)
    {
        var confirm = MessageBox.Show(Window.GetWindow(this),
            $"Supprimer la catégorie « {category.Nom} » ? Les charges déjà classées avec ce nom ne seront pas supprimées.",
            "Supprimer", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _session.Api.DeleteChargeCategoryAsync(category.Id);
            await LoadCategoriesAsync();
            RenderCategories();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Catégorie", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --- Charges ---

    private async Task LoadChargesAsync()
    {
        try
        {
            var categorie = CategoryFilterCombo.SelectedItem is ChargeCategoryDto { Id: not 0 } cat ? cat.Nom : null;
            var response = await _session.Api.GetChargesAsync(_dateDebut, _dateFin, categorie);
            _charges = response.Charges.ToList();
            RenderCharges();
            HideMessage();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private void RenderCharges()
    {
        ChargesGrid.ItemsSource = _charges.Select(c => new ChargeRow(
            c,
            BrushFromHex(_categories.FirstOrDefault(cat => cat.Nom == c.Categorie)?.Color ?? DefaultCategoryColor),
            _canEdit ? Visibility.Visible : Visibility.Collapsed,
            _canDelete && c.RecurringSourceId is null ? Visibility.Visible : Visibility.Collapsed))
            .ToList();

        ChargesEmptyPanel.Visibility = _charges.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshCharges_Click(object sender, RoutedEventArgs e) => await LoadChargesAsync();

    private async void Filters_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        await LoadChargesAsync();
    }

    private async void DateFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressDateFilterEvent) return;

        var tag = (string)((ComboBoxItem)DateFilterCombo.SelectedItem).Tag;
        var today = DateOnly.FromDateTime(DateTime.Now);

        if (tag == "custom")
        {
            var dialog = new CustomDateRangeDialog(_dateDebut ?? today, _dateFin ?? today)
            {
                Owner = Window.GetWindow(this),
            };
            if (dialog.ShowDialog() != true)
            {
                _suppressDateFilterEvent = true;
                foreach (ComboBoxItem item in DateFilterCombo.Items)
                {
                    if ((string)item.Tag != _dateFilterTag) continue;
                    DateFilterCombo.SelectedItem = item;
                    break;
                }
                _suppressDateFilterEvent = false;
                return;
            }

            _dateDebut = dialog.DateDebut;
            _dateFin = dialog.DateFin;
            _dateFilterTag = "custom";
            await LoadChargesAsync();
            return;
        }

        (_dateDebut, _dateFin) = tag switch
        {
            "week" => (today.AddDays(-6), today),
            "month" => (new DateOnly(today.Year, today.Month, 1), today),
            "year" => (new DateOnly(today.Year, 1, 1), today),
            "all" => ((DateOnly?)null, (DateOnly?)null),
            _ => (_dateDebut, _dateFin),
        };
        _dateFilterTag = tag;

        await LoadChargesAsync();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_categories.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this),
                "Créez d'abord au moins une catégorie de charge.", "Nouvelle charge",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new ChargeDialog(null, _categories) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is not { } request) return;

        try
        {
            await _session.Api.CreateChargeAsync(request);
            await LoadChargesAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Nouvelle charge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ChargesGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ChargesGrid.SelectedItem is ChargeRow { CanEdit: Visibility.Visible } row) _ = EditChargeAsync(row.Charge);
    }

    private async void EditCharge_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is ChargeRow row) await EditChargeAsync(row.Charge);
    }

    private async Task EditChargeAsync(ChargeDto charge)
    {
        var dialog = new ChargeDialog(charge, _categories) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is not { } request) return;

        try
        {
            await _session.Api.UpdateChargeAsync(charge.Id, request);
            await LoadChargesAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Modifier la charge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DeleteCharge_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not ChargeRow row) return;

        var confirm = MessageBox.Show(Window.GetWindow(this),
            $"Supprimer la charge « {row.Description} » ({row.MontantDisplay}) ?",
            "Supprimer", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _session.Api.DeleteChargeAsync(row.Charge.Id);
            await LoadChargesAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Supprimer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_charges.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "Aucune charge à exporter.", "Exporter",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var groupName = _session.Groupe?.Nom ?? "espace_groupe";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"charges_{groupName.Trim().ToLowerInvariant().Replace(' ', '_')}_{DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "Fichier CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            await System.IO.File.WriteAllTextAsync(dialog.FileName, BuildCsv(), new System.Text.UTF8Encoding(true));
        }
        catch (System.IO.IOException ex)
        {
            ShowMessage($"Erreur lors de l'export : {ex.Message}");
        }
    }

    private string BuildCsv()
    {
        static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(';', "Date", "Description", "Catégorie", "Type", "Montant", "Récurrente", "Créé par"));

        foreach (var c in _charges)
        {
            sb.AppendLine(string.Join(';',
                c.Date.ToString("dd/MM/yyyy"), Csv(c.Description), Csv(c.Categorie),
                c.TypeCharge == "fixe" ? "Fixe" : "Variable", Money.FormatPlain(c.Montant, 2),
                c.RecurringSourceId is not null ? "Auto" : c.IsRecurring ? "Source" : "Non",
                Csv(c.CreatedByName ?? "")));
        }

        sb.AppendLine();
        sb.AppendLine($"Total;;;;{Money.FormatPlain(_charges.Sum(c => c.Montant), 2)}");
        return sb.ToString();
    }

    // --- Récurrentes ---

    private async Task LoadRecurringAsync()
    {
        try
        {
            var response = await _session.Api.GetRecurringChargesAsync();
            var rows = response.Charges
                .Select(c => new RecurringRow(c, _canEdit ? Visibility.Visible : Visibility.Collapsed))
                .ToList();

            RecurringGrid.ItemsSource = rows;
            RecurringEmptyPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HideMessage();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private async void ChargeDetails_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not RecurringRow row) return;

        try
        {
            var details = await _session.Api.GetChargeDetailsAsync(row.Charge.Id);
            new ChargeDetailsDialog(details) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Détails", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ToggleRecurring_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not RecurringRow row) return;

        try
        {
            if (row.Charge.RecurringActive)
                await _session.Api.StopRecurringChargeAsync(row.Charge.Id);
            else
                await _session.Api.ReactivateRecurringChargeAsync(row.Charge.Id, new ReactivateRecurringRequest());

            await LoadRecurringAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Charge récurrente", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string DescribeRecurringDay(string? day) => day switch
    {
        "debut" => "le 1er de chaque mois",
        "fin" or null or "" => "le dernier jour de chaque mois",
        _ when int.TryParse(day, out var n) && n >= 1 => $"le {n} de chaque mois",
        _ => "le dernier jour de chaque mois",
    };

    // --- Analyses ---

    private void BuildYearCombo()
    {
        var current = DateTime.Now.Year;
        YearCombo.ItemsSource = Enumerable.Range(current - 4, 5).Reverse().ToList();
        YearCombo.SelectedItem = current;
    }

    private async void Year_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _activeTab != "analytics") return;
        await LoadAnalyticsAsync();
    }

    private async void RefreshAnalytics_Click(object sender, RoutedEventArgs e) => await LoadAnalyticsAsync();

    private async Task LoadAnalyticsAsync()
    {
        try
        {
            var year = YearCombo.SelectedItem as int? ?? DateTime.Now.Year;
            var stats = await _session.Api.GetChargesStatsAsync(year);
            RenderAnalytics(stats);
            HideMessage();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private void RenderAnalytics(ChargesStatsResponse stats)
    {
        var axisPaint = new SolidColorPaint(CurrentTextColor());
        var separatorPaint = new SolidColorPaint(CurrentBorderColor()) { StrokeThickness = 1 };

        StatsTilesPanel.Children.Clear();
        StatsTilesPanel.Children.Add(StatTile("Total des Charges", Money.Format(stats.Total), "Danger"));
        StatsTilesPanel.Children.Add(StatTile("Moyenne Mensuelle", Money.Format(stats.MoyenneMensuelle), "Warning"));
        StatsTilesPanel.Children.Add(StatTile("Catégorie Principale", stats.CategoriePrincipale, "TextPrimary"));

        // Charges par Catégorie - each slice uses that category's own stored colour rather
        // than a position-based palette, so it always reads the same colour here as on the
        // Catégories tab and in the Charges list's swatch.
        var categorySlices = stats.ParCategorie
            .Select(kv => (Label: kv.Key, Value: kv.Value, Color: CategoryColor(kv.Key)))
            .OrderByDescending(c => c.Value)
            .ToList();
        CategoryEmptyText.Visibility = categorySlices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CategoryPieChart.Series = categorySlices.Select(c => (ISeries)new PieSeries<double>
        {
            Values = [(double)c.Value], Name = c.Label, Fill = new SolidColorPaint(c.Color),
        }).ToArray();
        RenderPieLegend(CategoryLegendPanel, categorySlices);

        // Charges par Trimestre
        var quarters = new[] { 1, 2, 3, 4 }
            .Select(q => (Label: $"T{q}", Value: stats.ParTrimestre.GetValueOrDefault(q)))
            .ToList();
        QuarterEmptyText.Visibility = quarters.All(q => q.Value == 0) ? Visibility.Visible : Visibility.Collapsed;
        QuarterChart.Series =
        [
            new ColumnSeries<double>
            {
                Values = quarters.Select(q => (double)q.Value).ToArray(),
                Fill = new SolidColorPaint(FallbackPalette[0]),
                YToolTipLabelFormatter = point => Money.Format((decimal)point.Coordinate.PrimaryValue),
            },
        ];
        QuarterChart.XAxes = [new Axis { Labels = quarters.Select(q => q.Label).ToArray(), LabelsPaint = axisPaint, SeparatorsPaint = separatorPaint }];
        QuarterChart.YAxes = [new Axis { LabelsPaint = axisPaint, SeparatorsPaint = separatorPaint, Labeler = v => Money.FormatPlain((decimal)v) }];

        // Évolution Mensuelle
        MonthChartTitle.Text = $"Évolution Mensuelle des Charges ({Money.Label})";
        var months = Enumerable.Range(1, 12)
            .Select(m => (Label: new DateTime(2000, m, 1).ToString("MMM"), Value: stats.ParMois.GetValueOrDefault(m)))
            .ToList();
        MonthEmptyText.Visibility = months.All(m => m.Value == 0) ? Visibility.Visible : Visibility.Collapsed;
        MonthChart.Series =
        [
            new ColumnSeries<double>
            {
                Values = months.Select(m => (double)m.Value).ToArray(),
                Fill = new SolidColorPaint(FallbackPalette[3]),
                Name = "Charges",
                YToolTipLabelFormatter = point => Money.Format((decimal)point.Coordinate.PrimaryValue),
            },
        ];
        MonthChart.XAxes = [new Axis { Labels = months.Select(m => m.Label).ToArray(), LabelsPaint = axisPaint, SeparatorsPaint = separatorPaint, TextSize = 10 }];
        MonthChart.YAxes = [new Axis { LabelsPaint = axisPaint, SeparatorsPaint = separatorPaint, Labeler = v => Money.FormatPlain((decimal)v) }];
    }

    private SKColor CategoryColor(string categorieName)
    {
        var hex = _categories.FirstOrDefault(c => c.Nom == categorieName)?.Color;
        return hex is not null ? SKColorFromHex(hex) : FallbackPalette[Math.Abs(categorieName.GetHashCode()) % FallbackPalette.Length];
    }

    private Border StatTile(string label, string value, string colorKey)
    {
        var labelText = new TextBlock
        {
            Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextSecondary"),
        };
        var valueText = new TextBlock
        {
            Text = value, FontSize = 20, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource(colorKey), TextTrimming = TextTrimming.CharacterEllipsis,
        };

        return new Border
        {
            Width = 220, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("SurfaceAlt"), BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { valueText, labelText } },
        };
    }

    /// <summary>Plain WPF pie legend - colour dot, label, share of the total in parentheses -
    /// same as Stock/Ventes' own <c>RenderPieLegend</c>.</summary>
    private static void RenderPieLegend(Panel container, IReadOnlyList<(string Label, decimal Value, SKColor Color)> slices)
    {
        container.Children.Clear();
        var total = slices.Sum(s => s.Value);

        foreach (var slice in slices)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(Color.FromArgb(slice.Color.Alpha, slice.Color.Red, slice.Color.Green, slice.Color.Blue)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            // Same "XX,XX" formatting as Stock/Ventes' own pie legends - computed with
            // InvariantCulture so the "." it starts from is predictable regardless of the
            // machine's own locale, then swapped for the comma this app always displays.
            var percent = total > 0 ? slice.Value / total * 100 : 0;
            var percentText = percent.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
            row.Children.Add(new TextBlock
            {
                Text = $"{slice.Label} ({percentText}%)", FontSize = 11, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });
            container.Children.Add(row);
        }
    }

    private SKColor CurrentTextColor() => ToSKColor((Brush)FindResource("TextSecondary"));
    private SKColor CurrentBorderColor() => ToSKColor((Brush)FindResource("Border"));

    private static SKColor ToSKColor(Brush brush)
    {
        var c = ((SolidColorBrush)brush).Color;
        return new SKColor(c.R, c.G, c.B, c.A);
    }

    private static Brush BrushFromHex(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    private static SKColor SKColorFromHex(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex)!;
        return new SKColor(color.R, color.G, color.B);
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void HideMessage() => MessagePanel.Visibility = Visibility.Collapsed;
}
