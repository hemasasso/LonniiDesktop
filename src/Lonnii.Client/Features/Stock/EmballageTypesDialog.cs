using System.Windows;
using System.Windows.Controls;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Stock;

/// <summary>
/// Interchangeable packaging: groups the products whose crates are the same (all SODIBO crates,
/// Brafaso and Brafort...) under one emballage type. The Emballages tab then counts their empties
/// and what clients owe as one. Left: the types; right: the chosen type's name and products.
/// </summary>
public sealed class EmballageTypesDialog : Window
{
    private sealed record ProductItem(string Id, string Name, string? Unite, int? Contenance);

    private readonly LonniiApiClient _api;
    private readonly List<ProductItem> _products;
    private List<EmballageTypeDto> _types = [];
    private string? _editingId; // null = a new type

    private readonly ListBox _list = new() { BorderThickness = new Thickness(1) };
    private readonly TextBox _name = new() { Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(6, 4, 6, 4) };
    private readonly StackPanel _checks = new();
    private readonly Dictionary<string, CheckBox> _boxes = [];
    private readonly TextBlock _editorTitle = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock _warning = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _save = new() { Content = "Enregistrer", MinWidth = 110 };
    private readonly Button _delete = new() { Content = "Supprimer ce type", Margin = new Thickness(0, 0, 8, 0) };

    /// <summary>True once a type was saved or deleted: the Emballages tab must reload.</summary>
    public bool Changed { get; private set; }

    public EmballageTypesDialog(Window? owner, IReadOnlyList<EmballageStockDto> rows, LonniiApiClient api)
    {
        Owner = owner;
        _api = api;
        // Every returnable product, whether it counts on its own or inside a type already.
        _products = rows
            .SelectMany(r => r.Produits is { Count: > 0 } ps
                ? ps.Select(p => new ProductItem(p.ProductId, p.NomProduit, r.Unite, p.Contenance))
                : [new ProductItem(r.ProductId, r.NomProduit, r.Unite, r.Contenance)])
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Title = "Emballages interchangeables";
        Width = 820;
        Height = 600;
        MinWidth = 640;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");

        var root = new DockPanel { Margin = new Thickness(20) };

        var intro = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var art = EmballageArt.StackArt(44);
        art.Margin = new Thickness(0, 0, 12, 0);
        head.Children.Add(art);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Text("Emballages interchangeables", 17, "TextPrimary", FontWeights.SemiBold));
        titles.Children.Add(Text("Regroupez les produits dont les caisses sont les mêmes : toutes les caisses SODIBO, Brafaso et Brafort…", 12.5, "TextSecondary"));
        head.Children.Add(titles);
        intro.Children.Add(head);
        var how = Text("Leurs vides, ce que les clients doivent et les mouvements se comptent ensemble : un client qui a pris une caisse "
            + "de Brafaso peut rapporter une caisse de Brafort. Seules des caisses de même contenance vont ensemble (SODIBO de 12 et SODIBO de 16 "
            + "font deux types). Ce qui est déjà enregistré est regroupé aussitôt.", 12.5, "TextMuted");
        how.Margin = new Thickness(56, 6, 0, 0);
        intro.Children.Add(how);
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);

        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var close = new Button { Content = "Fermer", IsCancel = true, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right };
        close.SetResourceReference(StyleProperty, "SecondaryButton");
        close.Click += (_, _) => Close();
        footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left: the types, and a button for a new one.
        var left = new DockPanel();
        var add = new Button { Content = "＋  Nouveau type", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        add.SetResourceReference(StyleProperty, "SecondaryButton");
        add.Click += (_, _) => Edit(null);
        DockPanel.SetDock(add, Dock.Bottom);
        left.Children.Add(add);
        _list.SetResourceReference(Control.BackgroundProperty, "SurfaceAlt");
        _list.SetResourceReference(Control.BorderBrushProperty, "Border");
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem { Tag: string id } && id != _editingId) Edit(id);
        };
        left.Children.Add(_list);
        body.Children.Add(left);

        // Right: name + products of the type being edited.
        var editor = new DockPanel();
        DockPanel.SetDock(_editorTitle, Dock.Top);
        _editorTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        editor.Children.Add(_editorTitle);
        var nameLabel = Text("Nom du type", 12.5, "TextSecondary");
        nameLabel.Margin = new Thickness(0, 0, 0, 4);
        DockPanel.SetDock(nameLabel, Dock.Top);
        editor.Children.Add(nameLabel);
        DockPanel.SetDock(_name, Dock.Top);
        editor.Children.Add(_name);
        var productsLabel = Text("Produits qui partagent cette caisse", 12.5, "TextSecondary");
        productsLabel.Margin = new Thickness(0, 0, 0, 4);
        DockPanel.SetDock(productsLabel, Dock.Top);
        editor.Children.Add(productsLabel);

        var actions = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        _save.SetResourceReference(StyleProperty, "PrimaryButton");
        _save.Click += async (_, _) => await SaveAsync();
        DockPanel.SetDock(_save, Dock.Right);
        actions.Children.Add(_save);
        _delete.SetResourceReference(StyleProperty, "SecondaryButton");
        _delete.SetResourceReference(ForegroundProperty, "Danger");
        _delete.HorizontalAlignment = HorizontalAlignment.Left;
        _delete.Click += async (_, _) => await DeleteAsync();
        actions.Children.Add(_delete);
        DockPanel.SetDock(actions, Dock.Bottom);
        editor.Children.Add(actions);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        DockPanel.SetDock(_error, Dock.Bottom);
        editor.Children.Add(_error);
        _warning.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        DockPanel.SetDock(_warning, Dock.Bottom);
        editor.Children.Add(_warning);

        var checksBorder = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6) };
        checksBorder.SetResourceReference(Border.BorderBrushProperty, "Border");
        checksBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        checksBorder.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _checks };
        editor.Children.Add(checksBorder);
        Grid.SetColumn(editor, 2);
        body.Children.Add(editor);
        root.Children.Add(body);
        Content = root;

        if (_products.Count == 0)
        {
            _checks.Children.Add(Text("Aucun produit à emballage récupérable. Cochez « Emballage récupérable » dans la fiche d'un produit d'abord.", 12.5, "TextMuted"));
            _save.IsEnabled = false;
        }
        Loaded += async (_, _) => await LoadAsync(null);
    }

    private static TextBlock Text(string text, double size, string brush, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = weight ?? FontWeights.Normal };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private async Task LoadAsync(string? select)
    {
        _error.Text = "";
        try { _types = await _api.GetEmballageTypesAsync(); }
        catch (Exception ex)
        {
            _error.Text = ex is ApiException api ? api.Message : $"Impossible de charger les types : {ex.Message}";
            return;
        }
        _list.Items.Clear();
        foreach (var t in _types)
        {
            var item = new StackPanel { Margin = new Thickness(4, 5, 4, 5) };
            item.Children.Add(Text(t.Nom, 13.5, "TextPrimary", FontWeights.SemiBold));
            var names = t.ProductIds.Select(id => _products.FirstOrDefault(p => p.Id == id)?.Name).Where(n => n is not null).ToList();
            item.Children.Add(Text(names.Count == 0 ? "Aucun produit" : string.Join(" · ", names), 11.5, "TextSecondary"));
            _list.Items.Add(new ListBoxItem { Content = item, Tag = t.Id });
        }
        if (_list.Items.Count == 0)
            _list.Items.Add(new ListBoxItem { Content = Text("Aucun type pour l'instant. Créez-en un avec « Nouveau type ».", 12, "TextMuted"), IsEnabled = false, Margin = new Thickness(4, 6, 4, 6) });
        Edit(select is not null && _types.Any(t => t.Id == select) ? select : _types.FirstOrDefault()?.Id);
    }

    /// <summary>Shows the type <paramref name="id"/> in the editor (null = a new, empty one).</summary>
    private void Edit(string? id)
    {
        _editingId = id;
        _error.Text = "";
        var type = _types.FirstOrDefault(t => t.Id == id);
        _editorTitle.Text = type is null ? "Nouveau type d'emballage" : $"Modifier « {type.Nom} »";
        _name.Text = type?.Nom ?? "";
        _delete.Visibility = type is null ? Visibility.Collapsed : Visibility.Visible;
        foreach (ListBoxItem item in _list.Items)
            if (item.Tag is string tag) item.IsSelected = tag == id;
        if (type is null) _list.SelectedItem = null;

        if (_products.Count == 0) return;
        _checks.Children.Clear();
        _boxes.Clear();
        foreach (var p in _products)
        {
            var other = _types.FirstOrDefault(t => t.Id != id && t.ProductIds.Contains(p.Id));
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var art = EmballageArt.For(p.Unite, 22, EmballageArt.ForKey(p.Id));
            art.Margin = new Thickness(0, 0, 8, 0);
            content.Children.Add(art);
            content.Children.Add(Text(p.Name, 13, "TextPrimary"));
            // The crate and how many it holds: only crates of the same contenance are interchangeable.
            var size = p.Contenance is > 0 ? $"{p.Unite ?? "emballage"} de {p.Contenance}" : p.Unite;
            if (size is not null)
            {
                var unit = Text($"  ·  {size}", 12, "TextSecondary");
                unit.VerticalAlignment = VerticalAlignment.Center;
                content.Children.Add(unit);
            }
            if (other is not null)
            {
                var moved = Text($"  (déjà dans « {other.Nom} »)", 12, "TextMuted");
                moved.ToolTip = "Cocher ce produit le retire de l'autre type.";
                moved.VerticalAlignment = VerticalAlignment.Center;
                content.Children.Add(moved);
            }
            var box = new CheckBox { Content = content, Margin = new Thickness(0, 4, 0, 4), VerticalContentAlignment = VerticalAlignment.Center, IsChecked = type?.ProductIds.Contains(p.Id) == true };
            box.Checked += (_, _) => UpdateWarning();
            box.Unchecked += (_, _) => UpdateWarning();
            _boxes[p.Id] = box;
            _checks.Children.Add(box);
        }
        UpdateWarning();
        if (type is null) _name.Focus();
    }

    private List<string> Checked => _boxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();

    /// <summary>Different contenance (12 vs 16) can't share a type - the server refuses it, say it
    /// before. Different units only lose the unit on the totals: a warning.</summary>
    private void UpdateWarning()
    {
        var chosen = _products.Where(p => Checked.Contains(p.Id)).ToList();
        string? text = null;
        if (chosen.Select(p => p.Contenance).Distinct().Count() > 1)
            text = "⚠ Contenances différentes : " + string.Join(", ", chosen.Select(p => $"{p.Name} ({(p.Contenance is { } c ? $"de {c}" : "non indiquée")})"))
                + ". Seules des caisses de même contenance sont interchangeables (12 n'est pas 16) : faites un type par contenance.";
        else if (chosen.Count > 1 && SaleCharges.SharedUnit(chosen.Select(p => p.Unite)) is null)
            text = "⚠ Ces produits n'ont pas la même unité d'affichage : les totaux de ce type n'afficheront pas d'unité. Vérifiez qu'il s'agit bien de la même caisse.";
        _warning.Text = text ?? "";
        _warning.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task SaveAsync()
    {
        _error.Text = "";
        var nom = _name.Text.Trim();
        if (nom.Length == 0)
        {
            _error.Text = "Donnez un nom au type, par exemple « Caisse SODIBO ».";
            _name.Focus();
            return;
        }
        var ids = Checked;
        if (ids.Count < 2 && _editingId is null)
        {
            _error.Text = "Cochez au moins deux produits dont les caisses sont les mêmes.";
            return;
        }
        _save.IsEnabled = false;
        try
        {
            var request = new SaveEmballageTypeRequest(nom, ids);
            var saved = _editingId is null
                ? await _api.CreateEmballageTypeAsync(request)
                : await _api.UpdateEmballageTypeAsync(_editingId, request);
            Changed = true;
            await LoadAsync(saved.Id);
        }
        catch (ApiException ex) { _error.Text = ex.Message; }
        catch (Exception ex) { _error.Text = $"Échec de l'enregistrement : {ex.Message}"; }
        finally { _save.IsEnabled = true; }
    }

    private async Task DeleteAsync()
    {
        var type = _types.FirstOrDefault(t => t.Id == _editingId);
        if (type is null) return;
        var answer = MessageBox.Show(this,
            $"Supprimer le type « {type.Nom} » ?\n\nSes produits seront de nouveau comptés chacun à part. Les inventaires, retours fournisseur "
            + "et casses enregistrés sur le type lui-même ne compteront plus : refaites un inventaire des vides de chaque produit ensuite.",
            "Supprimer le type d'emballage", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await _api.DeleteEmballageTypeAsync(type.Id);
            Changed = true;
            await LoadAsync(null);
        }
        catch (ApiException ex) { _error.Text = ex.Message; }
        catch (Exception ex) { _error.Text = $"Échec de la suppression : {ex.Message}"; }
    }
}
