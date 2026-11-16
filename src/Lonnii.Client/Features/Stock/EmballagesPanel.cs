using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Lonnii.Client.Common.Controls;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Features.Stock;

/// <summary>
/// Stock > Emballages: per returnable product, the full ones in stock (Pleins), the empties in
/// the shop (Vides), those still with clients, and the total the shop owns - each drawn in the
/// product's own packaging (caisse, bouteille, carton, fût). Then every client holding empties,
/// and the movement history. Buttons record inventaire, retour fournisseur, casse / perte.
/// </summary>
public sealed class EmballagesPanel : UserControl
{
    private readonly AppSession _session;
    private readonly UniformGrid _tiles = new() { Columns = 4, Margin = new Thickness(0, 0, 0, 2) };
    private readonly WrapPanel _productChips = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly ContentControl _repartition = new() { Margin = new Thickness(0, 0, 12, 22) };
    private readonly StackPanel _products = new();
    private readonly StackPanel _clientsList = new();
    private readonly StackPanel _suppliersSection = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _suppliersList = new();
    private List<EmballageFournisseurDto> _fournisseurs = [];
    private readonly TextBlock _clientsSummary = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 10), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _clientSearch = new() { Width = 240, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _supplierSearch = new() { Width = 240, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _supplierSearchHint = new() { Text = "🔍 Rechercher un fournisseur…", Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly TextBlock _clientSearchHint = new() { Text = "🔍 Rechercher un client…", Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly StackPanel _history = new();
    private readonly TextBlock _historyTitle = new();
    private readonly Button _historyAll = new() { Content = "Tout l'historique", Visibility = Visibility.Collapsed, Padding = new Thickness(10, 3, 10, 3), FontSize = 12 };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed };
    // Long lists page instead of stretching the tab: clients 8 at a time (two per row when wide),
    // history 12 rows at a time.
    private const int ClientsPageSize = 8;
    private const int HistoryPageSize = 12;
    private readonly PaginationBar _clientsPager = new();
    private readonly TextBlock _clientsRange = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly DockPanel _clientsPagerRow = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly PaginationBar _historyPager = new();
    private readonly TextBlock _historyRange = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly DockPanel _historyPagerRow = new() { Margin = new Thickness(0, 8, 0, 0) };
    private List<EmballageMouvementDto> _historyRows = [];
    private int _clientColumns = 2;
    private List<EmballageStockDto> _rows = [];
    private List<EmballageClientDto> _clients = [];
    /// <summary>The product shown (BRAFASO, Castel...); null = all.</summary>
    private string? _productFilter;

    // Date filter: what moved in the period (flux tiles) and the history. The stock counts above
    // stay "à ce jour" - a date cannot change how many crates are in the shop now.
    private readonly ComboBox _periodCombo = new() { Width = 190, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _periodText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly UniformGrid _flux = new() { Columns = 5, Margin = new Thickness(0, 10, 0, 0) };
    private List<EmballageFluxDto> _fluxRows = [];
    private DateOnly? _dateDebut;
    private DateOnly? _dateFin;
    private string _periodTag = "month";
    private bool _suppressPeriodEvent;
    private bool _loading;
    private readonly DockPanel _historyHeader = new() { Margin = new Thickness(0, 16, 0, 10) };
    /// <summary>Soft colour glows behind the page: what the frosted (glass) cards let through.</summary>
    private readonly Grid _backdrop = new() { ClipToBounds = true, IsHitTestVisible = false };

    public EmballagesPanel(AppSession session)
    {
        _session = session;
        ApplyGlass();
        EventHandler onTheme = (_, _) => ApplyGlass();
        Loaded += (_, _) => { ThemeManager.Changed -= onTheme; ThemeManager.Changed += onTheme; ApplyGlass(); };
        Unloaded += (_, _) => ThemeManager.Changed -= onTheme;

        var root = new StackPanel();
        root.Children.Add(BuildHero());
        _message.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        root.Children.Add(_message);
        root.Children.Add(_productChips);
        root.Children.Add(_tiles);
        root.Children.Add(_repartition);

        root.Children.Add(SectionHeader("Par produit", "Répartition de chaque emballage entre le stock, le magasin et les clients."));
        root.Children.Add(_products);

        root.Children.Add(BuildClientsHeader());
        _clientsSummary.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        root.Children.Add(_clientsSummary);
        root.Children.Add(_clientsList);
        root.Children.Add(PagerRow(_clientsPagerRow, _clientsRange, _clientsPager, () => RenderClients(resetPage: false)));

        _suppliersSection.Children.Add(BuildSuppliersHeader());
        _suppliersSection.Children.Add(_suppliersList);
        root.Children.Add(_suppliersSection);

        root.Children.Add(BuildPeriodHeader());
        root.Children.Add(_flux);

        var historyHeader = _historyHeader;
        _historyAll.SetResourceReference(StyleProperty, "SecondaryButton");
        _historyAll.Click += (_, _) =>
        {
            // Back to everything: drop the client search and the product chosen.
            _clientSearch.Text = string.Empty;
            _supplierSearch.Text = string.Empty;
            SelectProduct(null);
        };
        DockPanel.SetDock(_historyAll, Dock.Right);
        historyHeader.Children.Add(_historyAll);
        _historyTitle.FontSize = 16;
        _historyTitle.FontWeight = FontWeights.SemiBold;
        _historyTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        historyHeader.Children.Add(_historyTitle);
        root.Children.Add(historyHeader);
        root.Children.Add(Card(_history, new Thickness(0)));
        root.Children.Add(PagerRow(_historyPagerRow, _historyRange, _historyPager, () => RenderHistoryPage(resetPage: false)));

        var page = new Grid();
        page.Children.Add(_backdrop);
        page.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(22, 18, 22, 28),
            Content = root,
        });
        Content = page;
        SizeChanged += (_, e) =>
        {
            _tiles.Columns = e.NewSize.Width < 820 ? 2 : 4;
            _flux.Columns = e.NewSize.Width < 820 ? 2 : e.NewSize.Width < 1250 ? 3 : 6;
            var columns = e.NewSize.Width < 1100 ? 1 : 2;
            if (columns == _clientColumns) return;
            _clientColumns = columns;
            RenderClients(resetPage: false);
        };
    }

    /// <summary>"1-8 sur 23" on the left, the page buttons on the right; hidden while one page holds everything.</summary>
    private static DockPanel PagerRow(DockPanel row, TextBlock range, PaginationBar pager, Action onPage)
    {
        range.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        pager.PageChanged += (_, _) => onPage();
        DockPanel.SetDock(pager, Dock.Right);
        row.Children.Add(pager);
        row.Children.Add(range);
        row.Visibility = Visibility.Collapsed;
        return row;
    }

    private static void ConfigurePager(DockPanel row, TextBlock range, PaginationBar pager, int page, int total, int pageSize)
    {
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        pager.Configure(page, pages);
        row.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        var first = (pager.CurrentPage - 1) * pageSize + 1;
        range.Text = $"{first}–{Math.Min(total, first + pageSize - 1)} sur {total}";
    }

    // ---- glass look -------------------------------------------------------------------------

    /// <summary>Frosted-glass brushes for the cards (EmbGlass, EmbGlassHero, EmbGlassBorder) and the
    /// coloured glows behind them, for the current theme. Rebuilt when the theme changes.</summary>
    private void ApplyGlass()
    {
        var dark = ThemeManager.IsDark;
        static Color W(byte a) => Color.FromArgb(a, 255, 255, 255);
        static Color Tint(string hex, byte a) { var c = EmballageArt.Rgb(hex); return Color.FromArgb(a, c.R, c.G, c.B); }

        var glass = new LinearGradientBrush(dark ? W(0x24) : W(0xE6), dark ? W(0x0C) : W(0x9E), new Point(0, 0), new Point(1, 1));
        var hero = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        hero.GradientStops.Add(new GradientStop(dark ? W(0x2A) : W(0xF5), 0));
        hero.GradientStops.Add(new GradientStop(dark ? W(0x10) : Tint("#F1F5F9", 0xC8), 1));
        var border = new LinearGradientBrush(dark ? W(0x4A) : W(0xFF), dark ? W(0x10) : Tint("#94A3B8", 0x55), new Point(0, 0), new Point(1, 1));
        foreach (var b in new Freezable[] { glass, hero, border }) b.Freeze();
        Resources["EmbGlass"] = glass;
        Resources["EmbGlassHero"] = hero;
        Resources["EmbGlassBorder"] = border;

        _backdrop.Background = EmballageArt.Brush(EmballageArt.Rgb(dark ? "#181A23" : "#EEF2F7"));
        _backdrop.Children.Clear();
        // Two faint grey-blue glows only: enough for the glass to read, no colour show.
        var a = (byte)(dark ? 0x22 : 0x40);
        _backdrop.Children.Add(Glow(dark ? "#475569" : "#CBD5E1", a, 900, 600, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(-260, -200, 0, 0)));
        _backdrop.Children.Add(Glow(dark ? "#334155" : "#BFD3EA", a, 900, 600, HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, -260, -200)));
    }

    private static FrameworkElement Glow(string hex, byte alpha, double width, double height,
        HorizontalAlignment h, VerticalAlignment v, Thickness margin)
    {
        var c = EmballageArt.Rgb(hex);
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alpha / 2), c.R, c.G, c.B), 0.45));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        brush.Freeze();
        return new System.Windows.Shapes.Ellipse { Width = width, Height = height, Fill = brush, HorizontalAlignment = h, VerticalAlignment = v, Margin = margin };
    }

    /// <summary>A frosted card: translucent fill over the glows, a light rim catching the top-left.</summary>
    private static void Glassify(Border b, string background = "EmbGlass")
    {
        b.SetResourceReference(Border.BackgroundProperty, background);
        b.SetResourceReference(Border.BorderBrushProperty, "EmbGlassBorder");
    }

    // ---- layout pieces ---------------------------------------------------------------------

    private UIElement BuildHero()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var art = EmballageArt.StackArt(92, EmballageArt.Steel);
        art.Margin = new Thickness(0, 0, 20, 0);
        art.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(art);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = "Emballages", FontSize = 24, FontWeight = FontWeights.Bold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var subtitle = new TextBlock
        {
            Text = "Suivez chaque caisse, casier ou bouteille consignée : ce qui est plein en stock, les vides au magasin "
                + "et ce que vos clients doivent encore rapporter.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12), FontSize = 13, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left,
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        text.Children.Add(title);
        text.Children.Add(subtitle);

        var canAdjust = _session.Can(Priv.Gestion.AdjustStock);
        var actions = new WrapPanel();
        actions.Children.Add(ActionButton("🚚  Retour fournisseur", EmballageMouvementTypes.RetourFournisseur, canAdjust, primary: true));
        actions.Children.Add(ActionButton("📦  Pleins reçus du fournisseur", EmballageMouvementTypes.RecuFournisseur, canAdjust, primary: false));
        actions.Children.Add(ActionButton("📋  Inventaire des vides", EmballageMouvementTypes.Inventaire, canAdjust, primary: false));
        actions.Children.Add(ActionButton("💥  Casse / perte", EmballageMouvementTypes.Casse, canAdjust, primary: false));
        var refresh = new Button { Content = "⟳  Actualiser", Margin = new Thickness(0, 0, 8, 4) };
        refresh.SetResourceReference(StyleProperty, "SecondaryButton");
        refresh.Click += async (_, _) => await LoadAsync();
        if (_session.Can(Priv.Gestion.EditProducts))
        {
            var types = new Button { Content = "🔗  Emballages interchangeables", Margin = new Thickness(0, 0, 8, 4), ToolTip = "Regrouper les produits dont les caisses sont les mêmes (SODIBO, Brafaso et Brafort…)" };
            types.SetResourceReference(StyleProperty, "SecondaryButton");
            types.Click += async (_, _) =>
            {
                var dialog = new EmballageTypesDialog(Window.GetWindow(this), _rows, _session.Api);
                dialog.ShowDialog();
                if (dialog.Changed) await LoadAsync();
            };
            actions.Children.Add(types);
        }
        actions.Children.Add(refresh);
        text.Children.Add(actions);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var hero = new Border
        {
            CornerRadius = new CornerRadius(18), Padding = new Thickness(22, 18, 22, 18), Margin = new Thickness(0, 0, 0, 18),
            BorderThickness = new Thickness(1), Child = grid,
        };
        Glassify(hero, "EmbGlassHero");
        return hero;
    }

    private UIElement BuildClientsHeader()
    {
        var dock = new DockPanel { Margin = new Thickness(0, 26, 0, 0) };
        var search = new Grid { VerticalAlignment = VerticalAlignment.Center };
        _clientSearchHint.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        _clientSearch.TextChanged += (_, _) =>
        {
            _clientSearchHint.Visibility = _clientSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderClients(resetPage: true);
            // The movements follow the search: only that client's returns.
            RenderHistoryPage(resetPage: true);
        };
        search.Children.Add(_clientSearch);
        search.Children.Add(_clientSearchHint);
        DockPanel.SetDock(search, Dock.Right);
        dock.Children.Add(search);
        dock.Children.Add(SectionHeader("Clients avec emballages", null, margin: new Thickness(0)));
        return dock;
    }

    /// <summary>"Fournisseurs" with its own search box (by supplier name), separate from the clients one.</summary>
    private UIElement BuildSuppliersHeader()
    {
        var dock = new DockPanel { Margin = new Thickness(0, 26, 0, 10) };
        var search = new Grid { VerticalAlignment = VerticalAlignment.Center };
        _supplierSearchHint.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        _supplierSearch.TextChanged += (_, _) =>
        {
            _supplierSearchHint.Visibility = _supplierSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderSuppliers();
            RenderHistoryPage(resetPage: true);
        };
        search.Children.Add(_supplierSearch);
        search.Children.Add(_supplierSearchHint);
        DockPanel.SetDock(search, Dock.Right);
        dock.Children.Add(search);
        dock.Children.Add(SectionHeader("Fournisseurs",
            "Pleins reçus de chaque fournisseur, vides rendus, et ce qu'il reste à leur rendre.", new Thickness(0)));
        return dock;
    }

    private static UIElement SectionHeader(string title, string? subtitle, Thickness? margin = null)
    {
        var stack = new StackPanel { Margin = margin ?? new Thickness(0, 0, 0, 10) };
        var t = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        stack.Children.Add(t);
        if (subtitle is not null)
        {
            var s = new TextBlock { Text = subtitle, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
            s.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            stack.Children.Add(s);
        }
        return stack;
    }

    private Button ActionButton(string text, string type, bool enabled, bool primary)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 4), IsEnabled = enabled };
        b.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : "SecondaryButton");
        if (!enabled) b.ToolTip = "Réservé aux membres qui peuvent ajuster le stock";
        b.Click += async (_, _) => await RecordAsync(type, null);
        return b;
    }

    private static Border Card(UIElement child, Thickness? padding = null, Thickness? margin = null)
    {
        var card = new Border
        {
            Padding = padding ?? new Thickness(16), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
            Margin = margin ?? new Thickness(0), Child = child,
        };
        Glassify(card);
        return card;
    }

    private static TextBlock Text(string text, double size = 13, string brush = "TextPrimary", FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private static string Count(decimal value) =>
        value < 0 ? "-" + SaleCharges.CountLabel(-value) : SaleCharges.CountLabel(value);

    private static string WithUnit(decimal value, string? unit) => unit is null ? Count(value) : $"{Count(value)} {unit}";

    // ---- data ------------------------------------------------------------------------------

    public async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        _message.Visibility = Visibility.Collapsed;
        try
        {
            var rows = _session.Api.GetEmballagesAsync();
            var clients = _session.Api.GetEmballageClientsAsync();
            _rows = await rows;
            _clients = await clients;
            if (_productFilter is not null && _rows.All(r => r.ProductId != _productFilter)) _productFilter = null;
            RenderProductChips();
            RenderTiles();
            RenderRepartition();
            RenderProducts();
            RenderClients(resetPage: false);
            try { _fournisseurs = await _session.Api.GetEmballageFournisseursAsync(); }
            catch (ApiException) { _fournisseurs = []; }
            RenderSuppliers();
            _fluxRows = await _session.Api.GetEmballageFluxAsync(_dateDebut, _dateFin);
            RenderFlux();
            await LoadHistoryAsync();
        }
        catch (ApiException ex) { ShowError(ex.Message); }
        catch (Exception ex) { ShowError($"Impossible de charger les emballages : {ex.Message}"); }
        finally { _loading = false; }
    }

    private async Task SafeLoadHistoryAsync()
    {
        try { await LoadHistoryAsync(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task LoadHistoryAsync()
    {
        // The server filters by product, so the 300 latest rows are all that product's.
        _historyRows = await _session.Api.GetEmballageMouvementsAsync(_productFilter, _dateDebut, _dateFin);
        RenderHistoryPage(resetPage: true);
    }

    private void RenderHistoryPage(bool resetPage)
    {
        var name = _productFilter is null ? null : _rows.FirstOrDefault(r => r.ProductId == _productFilter)?.NomProduit;
        var query = _clientSearch.Text.Trim();
        var supplier = _supplierSearch.Text.Trim();
        var title = "Mouvements des vides";
        if (name is not null) title += $" · {name}";
        if (query.Length > 0) title += $" · client « {query} »";
        if (supplier.Length > 0) title += $" · fournisseur « {supplier} »";
        _historyTitle.Text = title;
        _historyAll.Visibility = name is null && query.Length == 0 && supplier.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var page = resetPage ? 1 : _historyPager.CurrentPage;
        var rows = ShownHistory;
        ConfigurePager(_historyPagerRow, _historyRange, _historyPager, page, rows.Count, HistoryPageSize);
        RenderHistory(rows.Skip((_historyPager.CurrentPage - 1) * HistoryPageSize).Take(HistoryPageSize).ToList());
    }

    private void ShowError(string text)
    {
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
    }

    // ---- product filter -------------------------------------------------------------------

    private List<EmballageStockDto> Shown =>
        _productFilter is null ? _rows : _rows.Where(r => r.ProductId == _productFilter).ToList();

    /// <summary>Clients narrowed to the chosen product: only its line, totals redone, clients without it dropped.</summary>
    private List<EmballageClientDto> ShownClients
    {
        get
        {
            if (_productFilter is null) return _clients;
            return _clients
                .Select(c =>
                {
                    var lignes = c.Lignes.Where(l => l.ProductId == _productFilter).ToList();
                    return c with { Lignes = lignes, TotalDus = lignes.Sum(l => Math.Max(0, l.Dus)), ConsigneVersee = lignes.Sum(l => l.ConsigneVersee) };
                })
                .Where(c => c.Lignes.Count > 0)
                .OrderByDescending(c => c.TotalDus)
                .ToList();
        }
    }

    /// <summary>The movements for the chosen product and, while a client is searched, only that client's
    /// returns (or the movements of a product whose name matches the search).</summary>
    private List<EmballageMouvementDto> ShownHistory
    {
        get
        {
            IEnumerable<EmballageMouvementDto> rows = _historyRows;
            if (_productFilter is not null) rows = rows.Where(m => m.ProductId == _productFilter);
            var query = _clientSearch.Text.Trim();
            if (query.Length > 0)
                rows = rows.Where(m =>
                    (m.Type == "retour_client" && (Has(m.ClientNom ?? m.Note, query) || Has(m.ClientTelephone, query)))
                    || Has(m.NomProduit, query));
            var supplier = _supplierSearch.Text.Trim();
            if (supplier.Length > 0) rows = rows.Where(m => Has(m.SupplierName, supplier));
            return rows.ToList();
        }
    }

    private static bool Has(string? text, string query) =>
        text?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false;

    private void SelectProduct(string? productId, bool showHistory = false)
    {
        if (_productFilter != productId)
        {
            _productFilter = productId;
            RenderProductChips();
            RenderTiles();
            RenderRepartition();
            RenderProducts();
            RenderClients(resetPage: true);
            RenderSuppliers();
            RenderFlux();
        }
        _ = ReloadHistoryAsync(showHistory);
    }

    private async Task ReloadHistoryAsync(bool showHistory)
    {
        await SafeLoadHistoryAsync();
        if (showHistory) _historyHeader.BringIntoView();
    }

    /// <summary>"Tous" and one chip per product, each with its drawing and the total the shop owns of it.</summary>
    private void RenderProductChips()
    {
        _productChips.Children.Clear();
        if (_rows.Count == 0) return;
        var label = Text("Emballage", 12, "TextSecondary", FontWeights.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 12, 6);
        _productChips.Children.Add(label);
        var shared = SaleCharges.SharedUnit(_rows.Select(r => r.Unite));
        var productCount = _rows.Sum(r => r.Produits?.Count ?? 1);
        _productChips.Children.Add(ProductChip(null, "Tous", null,
            $"{WithUnit(_rows.Sum(r => r.Total), shared)} · {productCount} produit{(productCount > 1 ? "s" : "")}"));
        foreach (var r in _rows.OrderBy(r => r.NomProduit, StringComparer.CurrentCultureIgnoreCase))
            _productChips.Children.Add(ProductChip(r.ProductId, (r.Produits is { Count: > 0 } ? "🔗 " : "") + r.NomProduit, r.Unite,
                $"{WithUnit(r.Total, r.Unite)} au total" + (r.Produits is { Count: > 0 } ps ? $" · {ps.Count} produits" : "")));
    }

    private Button ProductChip(string? productId, string label, string? unit, string sub)
    {
        var on = _productFilter == productId;
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        var art = productId is null ? EmballageArt.StackArt(30, EmballageArt.Steel) : EmballageArt.For(unit, 30, EmballageArt.ForKey(productId));
        art.Margin = new Thickness(0, 0, 8, 0);
        art.VerticalAlignment = VerticalAlignment.Center;
        stack.Children.Add(art);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Text(label, 13, on ? "TextOnAccent" : "TextPrimary", FontWeights.SemiBold));
        texts.Children.Add(Text(sub, 11, on ? "TextOnAccent" : "TextSecondary"));
        stack.Children.Add(texts);
        var b = new Button { Content = stack, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(10, 5, 12, 5) };
        b.SetResourceReference(StyleProperty, on ? "PrimaryButton" : "SecondaryButton");
        b.Click += (_, _) => SelectProduct(productId);
        return b;
    }

    /// <summary>How the chosen product (or all) splits between stock, shop and clients, in numbers and
    /// percent; with "Tous" and several products, one bar per product underneath to compare them.</summary>
    private void RenderRepartition()
    {
        var rows = Shown;
        if (rows.Count == 0)
        {
            _repartition.Content = null;
            return;
        }
        var unit = SaleCharges.SharedUnit(rows.Select(r => r.Unite));
        var pleins = rows.Sum(r => r.Pleins ?? 0);
        var vides = Math.Max(0, rows.Sum(r => r.Vides));
        var clients = rows.Sum(r => r.ChezClients);

        var stack = new StackPanel();
        var head = new DockPanel();
        var totalText = new TextBlock { Text = WithUnit(rows.Sum(r => r.Total), unit), FontSize = 15, FontWeight = FontWeights.Bold };
        totalText.Foreground = EmballageArt.Brush(EmballageArt.TotalColor);
        DockPanel.SetDock(totalText, Dock.Right);
        head.Children.Add(totalText);
        var title = _productFilter is null ? "Répartition de tous les emballages" : $"Répartition · {rows[0].NomProduit}";
        head.Children.Add(Text(title, 14, "TextPrimary", FontWeights.SemiBold));
        stack.Children.Add(head);
        var piles = CratePiles(pleins, vides, clients, 21, 30, 16, unit, withLabels: true);
        piles.Margin = new Thickness(0, 12, 0, 4);
        stack.Children.Add(piles);

        if (_productFilter is null && rows.Count > 1)
        {
            var sep = new Border { Height = 1, Margin = new Thickness(0, 14, 0, 6) };
            sep.SetResourceReference(Border.BackgroundProperty, "Border");
            stack.Children.Add(sep);
            foreach (var r in rows.OrderByDescending(r => r.Total))
                stack.Children.Add(ProductRow(r));
        }
        _repartition.Content = Card(stack, new Thickness(18, 14, 18, 14));
    }

    /// <summary>One product in the "Tous" répartition: drawing, name, its own split bar, its total. Click = filter.</summary>
    private UIElement ProductRow(EmballageStockDto r)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        var art = EmballageArt.For(r.Unite, 30, EmballageArt.ForKey(r.ProductId));
        art.Margin = new Thickness(0, 0, 10, 0);
        art.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(art);
        var name = Text(r.NomProduit, 13, "TextPrimary", FontWeights.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var pleins = r.Pleins ?? 0;
        var vides = Math.Max(0, r.Vides);
        var bar = CratePiles(pleins, vides, r.ChezClients, 10, 16, 11, r.Unite, withLabels: false);
        bar.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(bar, 2);
        grid.Children.Add(bar);
        var total = Text(WithUnit(r.Total, r.Unite), 13, "TextPrimary", FontWeights.SemiBold);
        total.Foreground = EmballageArt.Brush(EmballageArt.TotalColor);
        total.HorizontalAlignment = HorizontalAlignment.Right;
        total.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(total, 3);
        grid.Children.Add(total);
        grid.MouseLeftButtonUp += (_, _) => SelectProduct(r.ProductId);
        return grid;
    }

    // ---- date filter ----------------------------------------------------------------------

    private UIElement BuildPeriodHeader()
    {
        foreach (var (label, tag) in new[]
        {
            ("Aujourd'hui", "today"), ("7 derniers jours", "week"), ("Ce mois", "month"),
            ("Cette année", "year"), ("Depuis le début", "all"), ("Période personnalisée", "custom"),
        })
            _periodCombo.Items.Add(new ComboBoxItem { Content = label, Tag = tag, IsSelected = tag == _periodTag });
        (_dateDebut, _dateFin) = PresetRange(_periodTag);
        _periodCombo.SelectionChanged += async (_, _) => await PeriodChangedAsync();
        _periodText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        UpdatePeriodText();

        var dock = new DockPanel { Margin = new Thickness(0, 26, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_periodText);
        right.Children.Add(_periodCombo);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(SectionHeader("Activité sur la période",
            "Ce qui a bougé : emballages pris et rapportés par les clients, rendus au fournisseur, cassés. L'historique ci-dessous suit la même période.",
            new Thickness(0)));
        return dock;
    }

    private static (DateOnly? Debut, DateOnly? Fin) PresetRange(string tag)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return tag switch
        {
            "today" => (today, today),
            "week" => (today.AddDays(-6), today),
            "month" => (new DateOnly(today.Year, today.Month, 1), today),
            "year" => (new DateOnly(today.Year, 1, 1), today),
            _ => (null, null),
        };
    }

    private void UpdatePeriodText() =>
        _periodText.Text = _dateDebut is null ? "toutes dates"
            : _dateDebut == _dateFin ? $"{_dateDebut:dd/MM/yyyy}"
            : $"du {_dateDebut:dd/MM/yyyy} au {_dateFin:dd/MM/yyyy}";

    /// <summary>Same presets, custom dialog and revert-on-cancel as Stock > Analyse's period.</summary>
    private async Task PeriodChangedAsync()
    {
        if (_suppressPeriodEvent || _periodCombo.SelectedItem is not ComboBoxItem item) return;
        var tag = (string)item.Tag;
        if (tag == "custom")
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var dialog = new Lonnii.Client.Common.CustomDateRangeDialog(_dateDebut ?? today, _dateFin ?? today) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true)
            {
                _suppressPeriodEvent = true;
                _periodCombo.SelectedItem = _periodCombo.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == _periodTag);
                _suppressPeriodEvent = false;
                return;
            }
            (_dateDebut, _dateFin) = (dialog.DateDebut, dialog.DateFin);
        }
        else (_dateDebut, _dateFin) = PresetRange(tag);
        _periodTag = tag;
        UpdatePeriodText();
        try
        {
            _fluxRows = await _session.Api.GetEmballageFluxAsync(_dateDebut, _dateFin);
            RenderFlux();
            await LoadHistoryAsync();
        }
        catch (ApiException ex) { ShowError(ex.Message); }
        catch (Exception ex) { ShowError($"Impossible de charger la période : {ex.Message}"); }
    }

    /// <summary>The period's movements for the products shown (type filter applies).</summary>
    private void RenderFlux()
    {
        _flux.Children.Clear();
        var ids = Shown.Select(r => r.ProductId).ToHashSet();
        var rows = _fluxRows.Where(f => ids.Contains(f.ProductId)).ToList();
        if (rows.Count == 0 && _rows.Count == 0) return;
        var unit = SaleCharges.SharedUnit(rows.Select(r => r.Unite));
        var inventaire = rows.Sum(r => r.Inventaire);
        _flux.Children.Add(Tile("PRIS PAR LES CLIENTS", WithUnit(rows.Sum(r => r.PrisClients), unit), "Emportés à la vente",
            EmballageArt.ClientArt(46), EmballageArt.ClientsColor));
        _flux.Children.Add(Tile("RAPPORTÉS PAR LES CLIENTS", WithUnit(rows.Sum(r => r.RapportesClients), unit), "Revenus au magasin",
            EmballageArt.CrateArt(46, EmballageArt.Slate, full: false), EmballageArt.VidesColor));
        _flux.Children.Add(Tile("REÇUS DU FOURNISSEUR", WithUnit(rows.Sum(r => r.RecusFournisseur), unit), "Pleins livrés",
            EmballageArt.CrateArt(46, EmballageArt.Red, full: true), EmballageArt.PleinsColor));
        _flux.Children.Add(Tile("RENDUS AU FOURNISSEUR", WithUnit(rows.Sum(r => r.RetourFournisseur), unit), "Vides retournés",
            EmballageArt.CrateArt(46, EmballageArt.Slate, full: false), EmballageArt.VidesColor));
        _flux.Children.Add(Tile("CASSE / PERTE", WithUnit(rows.Sum(r => r.Casse), unit),
            inventaire == 0 ? "Vides perdus ou cassés" : $"Écart d'inventaire : {(inventaire > 0 ? "+" : "")}{Count(inventaire)}",
            EmballageArt.CrateArt(46, EmballageArt.Slate, full: false), EmballageArt.CasseColor));
        _flux.Children.Add(Tile("CONSIGNE FOURNISSEUR", Money.Format(rows.Sum(r => r.ConsigneFournisseur)), "Reçue pour les vides rendus",
            CoinArt(), EmballageArt.TotalColor));
    }

    private static FrameworkElement CoinArt() => EmballageArt.MoneyArt(46);

    // ---- tiles -----------------------------------------------------------------------------

    private void RenderTiles()
    {
        _tiles.Children.Clear();
        var rows = Shown;
        // Units differ between products (caisse, casier, bouteille), so the totals only carry a
        // unit when all products share one.
        var unit = SaleCharges.SharedUnit(rows.Select(r => r.Unite));
        _tiles.Children.Add(Tile("PLEINS", WithUnit(rows.Sum(r => r.Pleins ?? 0), unit), "En stock, avec produit",
            EmballageArt.CrateArt(58, EmballageArt.Red, full: true), EmballageArt.PleinsColor));
        _tiles.Children.Add(Tile("VIDES", WithUnit(rows.Sum(r => r.Vides), unit), "Rapportés, au magasin",
            EmballageArt.CrateArt(58, EmballageArt.Slate, full: false), EmballageArt.VidesColor));
        _tiles.Children.Add(Tile("CHEZ LES CLIENTS", WithUnit(rows.Sum(r => r.ChezClients), unit), "À récupérer",
            EmballageArt.ClientArt(58), EmballageArt.ClientsColor));
        _tiles.Children.Add(Tile("TOTAL POSSÉDÉ", WithUnit(rows.Sum(r => r.Total), unit), "Pleins + vides + clients",
            EmballageArt.StackArt(58, EmballageArt.Emerald), EmballageArt.TotalColor));
    }

    private static Border Tile(string label, string value, string caption, FrameworkElement art, Color accent)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var stack = new StackPanel();
        var l = new StackPanel { Orientation = Orientation.Horizontal };
        l.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = EmballageArt.Brush(accent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        l.Children.Add(Text(label, 11, "TextSecondary", FontWeights.SemiBold));
        var v = new TextBlock { Text = value, FontSize = 24, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 2), TextWrapping = TextWrapping.Wrap, Foreground = EmballageArt.Brush(accent) };
        stack.Children.Add(l);
        stack.Children.Add(v);
        stack.Children.Add(Text(caption, 11.5, "TextMuted"));
        grid.Children.Add(stack);
        art.VerticalAlignment = VerticalAlignment.Center;
        art.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(art, 1);
        grid.Children.Add(art);

        var body = new Border { Padding = new Thickness(16, 14, 16, 14), Child = grid };

        var tile = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 12, 12), Child = body };
        Glassify(tile);
        return tile;
    }

    // ---- products --------------------------------------------------------------------------

    private void RenderProducts()
    {
        _products.Children.Clear();
        if (_rows.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 10) };
            var art = EmballageArt.CrateArt(110, EmballageArt.Slate, full: false);
            art.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(art);
            var title = Text("Aucun produit à emballage récupérable", 15, "TextPrimary", FontWeights.SemiBold);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.Margin = new Thickness(0, 10, 0, 4);
            empty.Children.Add(title);
            var hint = Text("Dans Stock, modifiez un produit et cochez « Emballage récupérable » pour le suivre ici.", 12.5, "TextSecondary");
            hint.TextAlignment = TextAlignment.Center;
            empty.Children.Add(hint);
            _products.Children.Add(Card(empty));
            return;
        }
        foreach (var r in Shown) _products.Children.Add(ProductCard(r));
    }

    private Border ProductCard(EmballageStockDto r)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var art = EmballageArt.For(r.Unite, 66, EmballageArt.ForKey(r.ProductId));
        art.Margin = new Thickness(0, 0, 18, 0);
        art.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(art);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Text(r.NomProduit, 15, "TextPrimary", FontWeights.SemiBold));
        var unitWord = r.Unite ?? "emballage";
        var caption = new List<string>();
        if (r.Contenance is > 0) caption.Add($"{unitWord} de {r.Contenance}");
        if (r.ConsigneMontant is > 0) caption.Add($"consigne {Money.Format(r.ConsigneMontant.Value)} / {unitWord}");
        if (caption.Count > 0)
        {
            var c = Text(string.Join(" · ", caption), 12, "TextSecondary");
            c.Margin = new Thickness(0, 2, 0, 0);
            info.Children.Add(c);
        }
        if (r.Produits is { Count: > 0 } produits)
        {
            // A shared type: which products' crates it groups, with each one's full stock.
            var names = produits.Select(p => p.Pleins is { } pl ? $"{p.NomProduit} ({Count(pl)})" : p.NomProduit);
            var shared = Text("🔗 Interchangeables : " + string.Join(" · ", names), 12, "TextSecondary");
            shared.Margin = new Thickness(0, 2, 0, 0);
            shared.ToolTip = "Un client peut rapporter la caisse de n'importe lequel de ces produits. Les chiffres entre parenthèses sont les pleins de chacun.";
            info.Children.Add(shared);
        }
        var crates = CratePiles(r.Pleins ?? 0, Math.Max(0, r.Vides), r.ChezClients, 15, 20, 12, r.Unite, withLabels: false);
        crates.Margin = new Thickness(0, 8, 0, 6);
        info.Children.Add(crates);
        var legend = new WrapPanel();
        legend.Children.Add(Legend(EmballageArt.PleinsColor, "Pleins", r.Pleins is { } p ? WithUnit(p, r.Unite) : "illimité"));
        legend.Children.Add(Legend(EmballageArt.VidesColor, "Vides", WithUnit(r.Vides, r.Unite), r.Vides < 0));
        legend.Children.Add(Legend(EmballageArt.ClientsColor, "Chez les clients", WithUnit(r.ChezClients, r.Unite)));
        info.Children.Add(legend);
        if (r.Vides < 0)
        {
            var warn = Text("⚠ Plus de vides sortis que comptés : faites un inventaire des vides pour corriger.", 12, "Danger");
            warn.Margin = new Thickness(0, 4, 0, 0);
            info.Children.Add(warn);
        }
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var total = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 20, 0), MinWidth = 110 };
        var tl = Text("TOTAL", 11, "TextSecondary", FontWeights.SemiBold);
        tl.HorizontalAlignment = HorizontalAlignment.Right;
        var tv = new TextBlock { Text = Count(r.Total), FontSize = 26, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right };
        tv.Foreground = EmballageArt.Brush(EmballageArt.TotalColor);
        var tu = Text(unitWord, 12, "TextMuted");
        tu.HorizontalAlignment = HorizontalAlignment.Right;
        total.Children.Add(tl);
        total.Children.Add(tv);
        total.Children.Add(tu);
        Grid.SetColumn(total, 2);
        grid.Children.Add(total);

        var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var hist = SmallButton("Historique", "Voir les mouvements de ce produit");
        hist.Click += (_, _) => SelectProduct(r.ProductId, showHistory: true);
        buttons.Children.Add(hist);
        if (_session.Can(Priv.Gestion.AdjustStock))
        {
            var edit = SmallButton("Mouvement…", "Inventaire, retour fournisseur ou casse pour ce produit");
            edit.Click += async (_, _) => await RecordAsync(null, r);
            buttons.Children.Add(edit);
        }
        Grid.SetColumn(buttons, 3);
        grid.Children.Add(buttons);

        return Card(grid, new Thickness(18, 14, 18, 14), new Thickness(0, 0, 0, 10));
    }

    /// <summary>The split drawn as three piles of crates spread across the width - red full ones
    /// (pleins), grey empties (vides), blue full ones (chez les clients) - each stacked like a pyramid
    /// (5, then 4, 3, 2, 1 on top), its percentage above. A bigger share always has more crates (see
    /// <see cref="CratesFor"/>). Each pile keeps its own column, so rows of products line up.</summary>
    private static FrameworkElement CratePiles(decimal pleins, decimal vides, decimal clients, int slots, double crate,
        double pctSize, string? unit, bool withLabels)
    {
        var total = pleins + vides + clients;
        var panel = new Grid
        {
            Background = Brushes.Transparent,
            ToolTip = $"Pleins {WithUnit(pleins, unit)} · Vides {WithUnit(vides, unit)} · Chez les clients {WithUnit(clients, unit)}",
        };
        if (total <= 0)
        {
            panel.Children.Add(Text("Aucun emballage", 12, "TextMuted"));
            return panel;
        }
        var parts = new[]
        {
            (Value: pleins, Palette: EmballageArt.Red, Full: true, Color: EmballageArt.PleinsColor, Label: "Pleins"),
            (Value: vides, Palette: EmballageArt.Slate, Full: false, Color: EmballageArt.VidesColor, Label: "Vides"),
            (Value: clients, Palette: EmballageArt.Blue, Full: true, Color: EmballageArt.ClientsColor, Label: "Chez les clients"),
        };
        for (var i = 0; i < parts.Length; i++)
        {
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var part = parts[i];
            var share = part.Value / total * 100;
            var count = CratesFor(share, slots);
            if (count == 0) continue;
            var pile = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(pile, i);
            pile.Children.Add(new TextBlock
            {
                Text = share is > 0 and < 1 ? "<1 %" : $"{Math.Round(share):0} %",
                FontSize = pctSize, FontWeight = FontWeights.Bold, Foreground = EmballageArt.Brush(part.Color),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 3),
            });
            foreach (var length in Pyramid(count))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                for (var k = 0; k < length; k++) row.Children.Add(EmballageArt.CrateArt(crate, part.Palette, part.Full));
                pile.Children.Add(row);
            }
            if (withLabels)
            {
                var caption = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
                caption.Children.Add(Text(part.Label + "  ", 12, "TextSecondary"));
                var value = Text(WithUnit(part.Value, unit), 12.5, "TextPrimary", FontWeights.SemiBold);
                value.TextWrapping = TextWrapping.NoWrap;
                caption.Children.Add(value);
                pile.Children.Add(caption);
            }
            panel.Children.Add(pile);
        }
        return panel;
    }

    /// <summary>Row lengths, top to bottom, of a pile of <paramref name="count"/> crates: the smallest
    /// full pyramid base that holds them (15 = 5, 4, 3, 2, 1), the top row taking what is left.</summary>
    private static List<int> Pyramid(int count)
    {
        var baseRow = 1;
        while (baseRow * (baseRow + 1) / 2 < count) baseRow++;
        var rows = new List<int>();
        for (var length = baseRow; count > 0; length--)
        {
            var take = Math.Min(length, count);
            rows.Add(take);
            count -= take;
        }
        rows.Reverse();
        return rows;
    }

    /// <summary>How many crates a share of <paramref name="percent"/> % gets: its part of
    /// <paramref name="slots"/>, but never fewer than the square root of the percentage, so small
    /// shares still differ (1 % = 1 crate, 4 % = 2, 6 % = 3) and a bigger share always has more.</summary>
    private static int CratesFor(decimal percent, int slots)
    {
        if (percent <= 0) return 0;
        var proportional = (int)Math.Round((double)percent / 100 * slots);
        var small = (int)Math.Ceiling(Math.Sqrt((double)percent));
        return Math.Max(1, Math.Max(proportional, small));
    }

    private static UIElement Legend(Color color, string label, string value, bool danger = false)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
        stack.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = EmballageArt.Brush(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var l = Text(label + " ", 12, "TextSecondary");
        l.TextWrapping = TextWrapping.NoWrap;
        var v = Text(value, 12, danger ? "Danger" : "TextPrimary", FontWeights.SemiBold);
        v.TextWrapping = TextWrapping.NoWrap;
        stack.Children.Add(l);
        stack.Children.Add(v);
        return stack;
    }

    private static Button SmallButton(string text, string tip)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(10, 3, 10, 3), ToolTip = tip, FontSize = 12, MinWidth = 104 };
        b.SetResourceReference(StyleProperty, "SecondaryButton");
        return b;
    }

    // ---- clients ---------------------------------------------------------------------------

    private static readonly Color[] AvatarColors =
        new[] { "#64748B" }.Select(EmballageArt.Rgb).ToArray();

    private void RenderClients(bool resetPage)
    {
        _clientsList.Children.Clear();
        _clientsPagerRow.Visibility = Visibility.Collapsed;
        var clients = ShownClients;
        var owing = clients.Where(c => c.TotalDus > 0).ToList();
        var unit = SaleCharges.SharedUnit(Shown.Select(r => r.Unite));
        var versee = clients.Sum(c => c.ConsigneVersee);
        _clientsSummary.Text = owing.Count == 0
            ? "Aucun client ne doit d'emballage."
            : $"{owing.Count} client{(owing.Count > 1 ? "s" : "")} · {WithUnit(owing.Sum(c => c.TotalDus), unit)} à récupérer"
              + (versee > 0 ? $" · consigne versée {Money.Format(versee)}" : "");

        var query = _clientSearch.Text.Trim();
        var list = clients
            .Where(c => query.Length == 0
                || c.ClientNom.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || (c.Telephone?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || c.Lignes.Any(l => l.NomProduit.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();

        if (list.Count == 0)
        {
            if (clients.Count > 0)
            {
                // Only clients still owing empties (or holding an advance) are listed: one who brought
                // everything back drops out, which reads like "not found" without saying so.
                var returned = _historyRows.Any(m => m.Type == "retour_client"
                    && (Has(m.ClientNom ?? m.Note, query) || Has(m.ClientTelephone, query)));
                var why = returned
                    ? $"« {query} » ne doit plus d'emballage : tout a été rapporté. Ses retours sont dans les mouvements ci-dessous."
                    : $"Aucun client ne doit d'emballage sous « {query} ». Un client qui a tout rapporté n'apparaît plus dans cette liste.";
                _clientsList.Children.Add(Card(Text(why, 13, returned ? "Success" : "TextMuted")));
            }
            else
            {
                var empty = new StackPanel { Orientation = Orientation.Horizontal };
                var art = EmballageArt.ClientArt(54);
                art.Margin = new Thickness(0, 0, 14, 0);
                empty.Children.Add(art);
                var t = Text("Tous les emballages sont rentrés. Les clients qui en emportent à la vente apparaîtront ici.", 13, "TextSecondary");
                t.VerticalAlignment = VerticalAlignment.Center;
                empty.Children.Add(t);
                _clientsList.Children.Add(Card(empty));
            }
            return;
        }

        ConfigurePager(_clientsPagerRow, _clientsRange, _clientsPager, resetPage ? 1 : _clientsPager.CurrentPage, list.Count, ClientsPageSize);
        var grid = new UniformGrid { Columns = _clientColumns };
        foreach (var c in list.Skip((_clientsPager.CurrentPage - 1) * ClientsPageSize).Take(ClientsPageSize))
        {
            var card = ClientCard(c);
            card.Margin = new Thickness(0, 0, _clientColumns > 1 ? 10 : 0, 10);
            grid.Children.Add(card);
        }
        _clientsList.Children.Add(grid);
    }

    /// <summary>Suppliers with emballages, narrowed by the search box (supplier or emballage name) and
    /// the emballage chip chosen above. Hidden while no supplier movement was ever recorded.</summary>
    private void RenderSuppliers()
    {
        _suppliersList.Children.Clear();
        _suppliersSection.Visibility = _fournisseurs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_fournisseurs.Count == 0) return;
        var query = _supplierSearch.Text.Trim();
        var shown = _fournisseurs
            .Select(f => _productFilter is null ? f : f with { Lignes = f.Lignes.Where(l => l.ProductId == _productFilter).ToList() })
            .Where(f => f.Lignes.Count > 0)
            .Where(f => query.Length == 0 || Has(f.Nom, query))
            .ToList();
        if (shown.Count == 0)
        {
            _suppliersList.Children.Add(Card(Text(query.Length > 0 ? $"Aucun fournisseur ne correspond à « {query} »." : "Aucun fournisseur pour cet emballage.", 13, "TextMuted")));
            return;
        }
        foreach (var f in shown) _suppliersList.Children.Add(SupplierCard(f));
    }

    private static Border SupplierCard(EmballageFournisseurDto f)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var art = EmballageArt.CrateArt(40, EmballageArt.Red, full: true);
        art.Margin = new Thickness(0, 0, 14, 0);
        art.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(art);

        var info = new StackPanel();
        info.Children.Add(Text("🚚 " + f.Nom, 14, "TextPrimary", FontWeights.SemiBold));
        var lines = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var l in f.Lignes)
        {
            var t = Text($"{l.NomProduit} · reçus {WithUnit(l.Recus, l.Unite)} · rendus {Count(l.Rendus)}"
                + (l.ARendre > 0 ? $" · à rendre {Count(l.ARendre)}" : l.ARendre < 0 ? $" · {Count(-l.ARendre)} rendus en plus" : " · soldé"), 12, "TextSecondary");
            t.TextWrapping = TextWrapping.NoWrap;
            lines.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(9, 3, 10, 3), Margin = new Thickness(0, 0, 6, 4), BorderThickness = new Thickness(1),
                Background = EmballageArt.Brush(Color.FromArgb(22, 220, 38, 38)), BorderBrush = EmballageArt.Brush(Color.FromArgb(70, 220, 38, 38)), Child = t,
            });
        }
        info.Children.Add(lines);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 140 };
        var owed = Math.Max(0, f.ARendre);
        var big = new TextBlock { Text = Count(owed), FontSize = 22, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right };
        if (owed > 0) big.Foreground = EmballageArt.Brush(EmballageArt.PleinsColor);
        else big.SetResourceReference(TextBlock.ForegroundProperty, "Success");
        right.Children.Add(big);
        var sub = Text(owed > 0 ? "à rendre au fournisseur" : "rien à rendre", 12, "TextMuted");
        sub.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(sub);
        if (f.Dernier is { } d)
        {
            var when = Text($"dernier mouvement {d.ToLocalTime():dd/MM/yyyy}", 11.5, "TextMuted");
            when.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(when);
        }
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return Card(grid, new Thickness(16, 12, 16, 12), new Thickness(0, 0, 0, 8));
    }

    private static Border ClientCard(EmballageClientDto c)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var initials = string.Concat(c.ClientNom.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpper(w[0], CultureInfo.CurrentCulture)));
        var color = AvatarColors[(int)((uint)StableHash(c.ClientNom) % AvatarColors.Length)];
        var avatar = new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(21), Background = EmballageArt.Brush(color), VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock { Text = initials.Length > 0 ? initials : "?", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        grid.Children.Add(avatar);

        var info = new StackPanel();
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(Text(c.ClientNom, 14, "TextPrimary", FontWeights.SemiBold));
        if (!string.IsNullOrWhiteSpace(c.Telephone))
        {
            var phone = Text("  ·  " + c.Telephone, 12.5, "TextSecondary");
            phone.VerticalAlignment = VerticalAlignment.Center;
            name.Children.Add(phone);
        }
        info.Children.Add(name);
        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var l in c.Lignes) chips.Children.Add(Chip(l));
        info.Children.Add(chips);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 140 };
        if (c.TotalDus > 0)
        {
            var unit = SaleCharges.SharedUnit(c.Lignes.Where(l => l.Dus > 0).Select(l => l.Unite));
            var due = new TextBlock { Text = Count(c.TotalDus), FontSize = 22, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right };
            due.Foreground = EmballageArt.Brush(EmballageArt.ClientsColor);
            right.Children.Add(due);
            var u = Text(unit is null ? "à rapporter" : $"{unit} à rapporter", 12, "TextMuted");
            u.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(u);
        }
        else
        {
            var adv = Text("D'avance", 13, "Success", FontWeights.SemiBold);
            adv.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(adv);
        }
        if (c.ConsigneVersee > 0)
        {
            var v = Text($"consigne versée {Money.Format(c.ConsigneVersee)}", 11.5, "TextSecondary");
            v.HorizontalAlignment = HorizontalAlignment.Right;
            v.Margin = new Thickness(0, 4, 0, 0);
            right.Children.Add(v);
        }
        if (c.DernierAchat is { } d)
        {
            var when = Text($"dernier achat {d.ToLocalTime():dd/MM/yyyy}", 11.5, "TextMuted");
            when.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(when);
        }
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        return Card(grid, new Thickness(16, 12, 16, 12), new Thickness(0, 0, 0, 8));
    }

    private static Border Chip(EmballageClientLigne l)
    {
        var advance = l.Dus < 0;
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        var art = EmballageArt.For(l.Unite, 22, advance ? EmballageArt.Emerald : EmballageArt.Blue);
        art.Margin = new Thickness(0, 0, 6, 0);
        art.VerticalAlignment = VerticalAlignment.Center;
        stack.Children.Add(art);
        var text = advance
            ? $"{l.NomProduit} · {WithUnit(-l.Dus, l.Unite)} d'avance"
            : $"{l.NomProduit} · {WithUnit(l.Dus, l.Unite)}" + (l.Contenance is > 0 ? $" de {l.Contenance}" : "");
        var t = Text(text, 12.5, advance ? "Success" : "TextPrimary");
        t.TextWrapping = TextWrapping.NoWrap;
        t.VerticalAlignment = VerticalAlignment.Center;
        stack.Children.Add(t);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(8, 3, 10, 3), Margin = new Thickness(0, 0, 6, 4), BorderThickness = new Thickness(1), Child = stack,
            Background = EmballageArt.Brush(advance ? Color.FromArgb(28, 16, 185, 129) : Color.FromArgb(26, 37, 99, 235)),
            BorderBrush = EmballageArt.Brush(advance ? Color.FromArgb(90, 16, 185, 129) : Color.FromArgb(80, 37, 99, 235)),
            ToolTip = l.ConsigneVersee > 0 ? $"Consigne versée : {Money.Format(l.ConsigneVersee)}" : null,
        };
        return chip;
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 17;
            foreach (var ch in s.ToLowerInvariant()) h = h * 31 + ch;
            return h;
        }
    }

    // ---- history ---------------------------------------------------------------------------

    private static (Color Color, string Icon) TypeStyle(string type) => type switch
    {
        "retour_client" => (EmballageArt.ClientsColor, "↩"),
        EmballageMouvementTypes.RecuFournisseur => (EmballageArt.PleinsColor, "📦"),
        EmballageMouvementTypes.RetourFournisseur => (EmballageArt.VidesColor, "🚚"),
        EmballageMouvementTypes.Casse => (EmballageArt.CasseColor, "💥"),
        _ => (EmballageArt.Rgb("#64748B"), "📋"),
    };

    private void RenderHistory(List<EmballageMouvementDto> rows)
    {
        _history.Children.Clear();
        if (rows.Count == 0)
        {
            var filtered = _productFilter is not null || _clientSearch.Text.Trim().Length > 0 || _supplierSearch.Text.Trim().Length > 0;
            var empty = Text(filtered ? "Aucun mouvement pour cette sélection sur la période." : "Aucun mouvement sur la période.", 13, "TextMuted");
            empty.Margin = new Thickness(16);
            _history.Children.Add(empty);
            return;
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var m = rows[i];
            var grid = new Grid { Margin = new Thickness(16, 9, 16, 9) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

            var (color, icon) = TypeStyle(m.Type);
            var badge = new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(9, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Background = EmballageArt.Brush(Color.FromArgb(34, color.R, color.G, color.B)),
                Child = new TextBlock { Text = $"{icon}  {EmballageMouvementTypes.Label(m.Type)}", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = EmballageArt.Brush(color) },
            };
            grid.Children.Add(badge);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(Text(m.NomProduit ?? "", 13, "TextPrimary", FontWeights.SemiBold));
            // Say who is who: the client who brought the empties back, the supplier, and the member who recorded it.
            var client = m.Type == "retour_client" ? m.ClientNom ?? m.Note : null;
            var note = m.Type == "retour_client" ? null : m.Note;
            var detail = string.Join(" · ", new[]
                {
                    m.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                    client is null ? null : $"Client : {client}",
                    m.SupplierName is null ? null : $"Fournisseur : {m.SupplierName}",
                    note,
                    m.CreatedByName is null ? null : $"par {m.CreatedByName}",
                }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            info.Children.Add(Text(detail, 11.5, "TextSecondary"));
            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            if (m.Montant is { } mt and not 0)
            {
                var money = Text((mt > 0 ? "+" : "−") + Money.Format(Math.Abs(mt)), 12.5, mt > 0 ? "Success" : "TextSecondary");
                money.ToolTip = mt > 0 ? "Consigne reçue du fournisseur" : "Consigne rendue au client";
                money.VerticalAlignment = VerticalAlignment.Center;
                money.Margin = new Thickness(12, 0, 12, 0);
                Grid.SetColumn(money, 2);
                grid.Children.Add(money);
            }

            var qty = new TextBlock
            {
                Text = (m.Quantite > 0 ? "+" : "") + Count(m.Quantite), FontSize = 15, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };
            qty.SetResourceReference(TextBlock.ForegroundProperty, m.Quantite >= 0 ? "Success" : "Danger");
            Grid.SetColumn(qty, 3);
            grid.Children.Add(qty);

            var row = new Border { Child = grid, BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0) };
            row.SetResourceReference(Border.BorderBrushProperty, "Border");
            _history.Children.Add(row);
        }
    }

    // ---- actions ---------------------------------------------------------------------------

    private async Task RecordAsync(string? type, EmballageStockDto? product)
    {
        if (_rows.Count == 0)
        {
            ShowError("Aucun produit à emballage récupérable.");
            return;
        }
        List<SupplierDto> suppliers = [];
        try { suppliers = await _session.Api.GetSuppliersAsync(); }
        catch { /* the supplier list is optional */ }

        var dialog = new EmballageMouvementDialog(Window.GetWindow(this), _rows, product?.ProductId, type, suppliers, _session.Api);
        if (dialog.ShowDialog() == true) await LoadAsync();
    }
}
