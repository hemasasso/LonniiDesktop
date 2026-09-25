using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Microsoft.Win32;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// "Paramètre Reçu et Facture": the editor for everything printed on a reçu or a facture -
/// logo, QR code, company name, the two titles, the boxed notices and the footers.
///
/// <para>
/// The preview on the right is rebuilt from the form on every keystroke, using the same
/// <see cref="ReceiptTypography"/> sizes and the same layout as
/// <see cref="VenteReceiptDialog"/>'s paper. That is deliberate: a shop configures this
/// once and then trusts it on every customer-facing document, and a preview that only
/// approximates the print would be worse than showing none at all.
/// </para>
/// <para>
/// Nothing is sent until "Enregistrer" - including the images, which are staged in memory.
/// Lonnii Business deletes an image the moment the button is clicked, so closing its screen
/// cannot undo it; here "Fermer" always leaves the workspace exactly as it was found.
/// </para>
/// </summary>
public partial class ReceiptSettingsDialog : Window
{
    private readonly AppSession _session;
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>What the dialog loaded, and what a failed save leaves in place.</summary>
    private ReceiptSettingsDto _loaded = null!;

    /// <summary>Which document the form and the preview are showing.</summary>
    private bool _showingFacture;

    /// <summary>Suppresses the preview rebuild while the form is being filled from a DTO,
    /// which would otherwise run once per field assigned.</summary>
    private bool _loading = true;

    // Image state. _logoBytes is what the preview shows; _pendingLogo is a newly picked
    // file waiting to be uploaded; _logoRemoved records a removal with no replacement.
    private byte[]? _logoBytes;
    private byte[]? _pendingLogo;
    private string? _pendingLogoName;
    private bool _logoRemoved;

    private byte[]? _qrBytes;
    private byte[]? _pendingQr;
    private string? _pendingQrName;
    private bool _qrRemoved;

    /// <summary>Stand-in sale used by the preview - three lines, so wrapping and the
    /// column widths are visible before anything is printed for real.</summary>
    private static readonly (string Name, int Qty, decimal Price)[] SampleItems =
    [
        ("Chemise Oxford", 2, 8500m),
        ("Pantalon Chino", 1, 12500m),
        ("Ceinture Cuir", 1, 4000m),
    ];

    private const decimal SampleAvoir = 5000m;

    public ReceiptSettingsDialog(AppSession session)
    {
        _session = session;
        InitializeComponent();

        FontFamilyBox.ItemsSource = ReceiptSettingsDefaults.FontFamilies;

        FontSizeSlider.Minimum = ReceiptSettingsDefaults.MinFontSize;
        FontSizeSlider.Maximum = ReceiptSettingsDefaults.MaxFontSize;
        ReceiptTitleSizeSlider.Minimum = ReceiptSettingsDefaults.MinTitleFontSize;
        ReceiptTitleSizeSlider.Maximum = ReceiptSettingsDefaults.MaxTitleFontSize;
        FactureTitleSizeSlider.Minimum = ReceiptSettingsDefaults.MinTitleFontSize;
        FactureTitleSizeSlider.Maximum = ReceiptSettingsDefaults.MaxTitleFontSize;

        HookLivePreview();
        ShowDocument(facture: false);

        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Every control that feeds the preview, wired to rebuild it as it changes.</summary>
    private void HookLivePreview()
    {
        TextBox[] boxes =
        [
            CompanyNameBox, SellerLabelBox, NoteUnderQrBox,
            ReceiptTitleBox, ReceiptFooterBox, AvoirNoticeTitleBox, AvoirNoticeTextBox,
            FactureTitleBox, FactureNoticeTitleBox, FactureNoticeTextBox, FactureFooterBox,
        ];
        foreach (var box in boxes) box.TextChanged += (_, _) => RenderPreview();

        FontFamilyBox.SelectionChanged += (_, _) => RenderPreview();

        Slider[] sliders = [FontSizeSlider, ReceiptTitleSizeSlider, FactureTitleSizeSlider];
        foreach (var slider in sliders) slider.ValueChanged += (_, _) => RenderPreview();
    }

    private async Task LoadAsync()
    {
        SetBusy(true);
        try
        {
            _loaded = await _session.GetReceiptSettingsAsync();
            _logoBytes = _session.ReceiptLogo;
            _qrBytes = _session.ReceiptQrCode;

            Fill(_loaded);
            ShowStatus(null);
            SetBusy(false);
        }
        catch (ApiException ex)
        {
            // Nothing was loaded, so there is nothing to edit: saving now would write the
            // built-in defaults over whatever the workspace actually has stored. The form
            // stays locked - deliberately after SetBusy, which would otherwise re-enable it.
            ShowStatus(ex.Message, error: true);
            SetBusy(false);
            SaveButton.IsEnabled = false;
        }
    }

    private void Fill(ReceiptSettingsDto s)
    {
        _loading = true;

        CompanyNameBox.Text = s.CompanyName ?? string.Empty;
        SellerLabelBox.Text = s.SellerLabel;
        NoteUnderQrBox.Text = s.NoteUnderQr ?? string.Empty;

        ReceiptTitleBox.Text = s.ReceiptTitle;
        ReceiptFooterBox.Text = s.ReceiptFooterText;
        AvoirNoticeTitleBox.Text = s.AvoirNoticeTitle;
        AvoirNoticeTextBox.Text = s.AvoirNoticeText;

        FactureTitleBox.Text = s.FactureTitle;
        FactureNoticeTitleBox.Text = s.FactureNoticeTitle;
        FactureNoticeTextBox.Text = s.FactureNoticeText;
        FactureFooterBox.Text = s.FactureFooterText;

        FontFamilyBox.SelectedItem = ReceiptSettingsDefaults.FontFamilies
            .FirstOrDefault(f => string.Equals(f, s.FontFamily, StringComparison.OrdinalIgnoreCase))
            ?? ReceiptSettingsDefaults.FontFamily;

        FontSizeSlider.Value = s.FontSize;
        ReceiptTitleSizeSlider.Value = s.ReceiptTitleFontSize;
        FactureTitleSizeSlider.Value = s.FactureTitleFontSize;

        ShowLogo(_logoBytes);
        ShowQr(_qrBytes);

        _loading = false;
        RenderPreview();
    }

    // --- Document toggle ---------------------------------------------------------

    private void ShowRecu_Click(object sender, RoutedEventArgs e) => ShowDocument(facture: false);

    private void ShowFacture_Click(object sender, RoutedEventArgs e) => ShowDocument(facture: true);

    private void ShowDocument(bool facture)
    {
        _showingFacture = facture;

        RecuCard.Visibility = facture ? Visibility.Collapsed : Visibility.Visible;
        FactureCard.Visibility = facture ? Visibility.Visible : Visibility.Collapsed;

        // Same active-tab treatment as the Ventes module's own tabs (VentesView.ApplyTabVisuals):
        // ModuleTabButton carries no selected state of its own.
        var accent = (Brush)FindResource("Accent");
        var onAccent = (Brush)FindResource("TextOnAccent");
        var muted = (Brush)FindResource("TextMuted");

        Apply(RecuTabButton, !facture);
        Apply(FactureTabButton, facture);

        RenderPreview();

        void Apply(Button button, bool active)
        {
            button.Background = active ? accent : Brushes.Transparent;
            button.Foreground = active ? onAccent : muted;
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // --- Images ------------------------------------------------------------------

    private void ChooseLogo_Click(object sender, RoutedEventArgs e)
    {
        if (Pick("Choisir un logo") is not { } picked) return;

        _pendingLogo = picked.Bytes;
        _pendingLogoName = picked.Name;
        _logoRemoved = false;
        _logoBytes = picked.Bytes;
        ShowLogo(picked.Bytes);
        RenderPreview();
    }

    private void RemoveLogo_Click(object sender, RoutedEventArgs e)
    {
        _pendingLogo = null;
        _pendingLogoName = null;
        // Only a removal to send if something was actually stored; discarding a pick that
        // was never uploaded needs no request.
        _logoRemoved = _loaded.LogoUrl is not null;
        _logoBytes = null;
        ShowLogo(null);
        RenderPreview();
    }

    private void ChooseQr_Click(object sender, RoutedEventArgs e)
    {
        if (Pick("Choisir un QR Code") is not { } picked) return;

        _pendingQr = picked.Bytes;
        _pendingQrName = picked.Name;
        _qrRemoved = false;
        _qrBytes = picked.Bytes;
        ShowQr(picked.Bytes);
        RenderPreview();
    }

    private void RemoveQr_Click(object sender, RoutedEventArgs e)
    {
        _pendingQr = null;
        _pendingQrName = null;
        _qrRemoved = _loaded.QrCodeUrl is not null;
        _qrBytes = null;
        ShowQr(null);
        RenderPreview();
    }

    /// <summary>Asks for an image file and reads it. Null when the user cancelled or the
    /// file could not be read - the message is already on screen in the latter case.</summary>
    private (byte[] Bytes, string Name)? Pick(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.webp",
        };

        if (dialog.ShowDialog(this) != true) return null;

        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);

            if (bytes.LongLength > MaxUploadBytes)
            {
                ShowStatus($"L'image dépasse la taille maximale de {MaxUploadBytes / (1024 * 1024)} Mo.", error: true);
                return null;
            }

            ShowStatus(null);
            return (bytes, Path.GetFileName(dialog.FileName));
        }
        catch (IOException ex)
        {
            ShowStatus($"Impossible de lire ce fichier : {ex.Message}", error: true);
            return null;
        }
    }

    /// <summary>Mirrors <c>ImageStorageService.MaxUploadBytes</c>, so an oversized file is
    /// refused before it is sent rather than after.</summary>
    private const long MaxUploadBytes = 8 * 1024 * 1024;

    private void ShowLogo(byte[]? bytes)
    {
        LogoImage.Source = bytes is null ? null : ImageHelper.FromBytes(bytes);
        LogoPlaceholder.Visibility = bytes is null ? Visibility.Visible : Visibility.Collapsed;
        RemoveLogoButton.Visibility = bytes is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowQr(byte[]? bytes)
    {
        QrImage.Source = bytes is null ? null : ImageHelper.FromBytes(bytes);
        QrPlaceholder.Visibility = bytes is null ? Visibility.Visible : Visibility.Collapsed;
        RemoveQrButton.Visibility = bytes is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // --- Saving ------------------------------------------------------------------

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        ShowStatus(null);

        try
        {
            var saved = await _session.Api.UpdateReceiptSettingsAsync(BuildRequest());

            // The images go after the text, and each only when it actually changed - a shop
            // correcting a typo should not be re-uploading its logo.
            var logoUrl = saved.LogoUrl;
            if (_logoRemoved)
            {
                await _session.Api.DeleteReceiptLogoAsync();
                logoUrl = null;
            }
            else if (_pendingLogo is { } logo)
            {
                logoUrl = (await _session.Api.UploadReceiptLogoAsync(logo, _pendingLogoName!)).ImageUrl;
            }

            var qrUrl = saved.QrCodeUrl;
            if (_qrRemoved)
            {
                await _session.Api.DeleteReceiptQrCodeAsync();
                qrUrl = null;
            }
            else if (_pendingQr is { } qr)
            {
                qrUrl = (await _session.Api.UploadReceiptQrCodeAsync(qr, _pendingQrName!)).ImageUrl;
            }

            _loaded = saved with { LogoUrl = logoUrl, QrCodeUrl = qrUrl };
            _pendingLogo = _pendingQr = null;
            _logoRemoved = _qrRemoved = false;

            // The bytes are already in hand, so the cache is updated without refetching -
            // the next receipt printed on this machine uses the new configuration at once.
            _session.ApplyReceiptSettingsChange(_loaded, _logoBytes, _qrBytes);

            // Re-filled from the server's answer, not from the form: the API trims, clamps
            // the sizes and substitutes a default for anything blanked, and the screen
            // should show what was actually stored.
            Fill(_loaded);
            ShowStatus("Enregistré.");
        }
        catch (ApiException ex)
        {
            ShowStatus(ex.Message, error: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private UpdateReceiptSettingsRequest BuildRequest() => new(
        CompanyName: Blank(CompanyNameBox.Text),
        NoteUnderQr: Blank(NoteUnderQrBox.Text),
        ReceiptTitle: ReceiptTitleBox.Text,
        FactureTitle: FactureTitleBox.Text,
        FactureNoticeTitle: FactureNoticeTitleBox.Text,
        FactureNoticeText: FactureNoticeTextBox.Text,
        FactureFooterText: FactureFooterBox.Text,
        ReceiptFooterText: ReceiptFooterBox.Text,
        SellerLabel: SellerLabelBox.Text,
        AvoirNoticeTitle: AvoirNoticeTitleBox.Text,
        AvoirNoticeText: AvoirNoticeTextBox.Text,
        FontFamily: FontFamilyBox.SelectedItem as string ?? ReceiptSettingsDefaults.FontFamily,
        FontSize: (int)FontSizeSlider.Value,
        ReceiptTitleFontSize: (int)ReceiptTitleSizeSlider.Value,
        FactureTitleFontSize: (int)FactureTitleSizeSlider.Value);

    // --- Live preview ------------------------------------------------------------

    /// <summary>
    /// Redraws the sample document from the current form values. Cheap enough to run on
    /// every keystroke: a few dozen TextBlocks, no layout the printer will ever see.
    /// </summary>
    private void RenderPreview()
    {
        if (_loading) return;

        var fontSize = (int)FontSizeSlider.Value;
        var titleSize = (int)(_showingFacture ? FactureTitleSizeSlider.Value : ReceiptTitleSizeSlider.Value);
        var family = new FontFamily(FontFamilyBox.SelectedItem as string ?? ReceiptSettingsDefaults.FontFamily);

        // Attached property: the paper is a Border, which has no FontFamily of its own.
        TextElement.SetFontFamily(PreviewPaper, family);

        // Sliders carry their current value in the label, the way the web app's do - a bare
        // slider gives no way to tell 12 from 13.
        FontSizeLabel.Text = $"Taille du texte ({fontSize} px)";
        ReceiptTitleSizeLabel.Text = $"Taille du titre ({(int)ReceiptTitleSizeSlider.Value} px)";
        FactureTitleSizeLabel.Text = $"Taille du titre ({(int)FactureTitleSizeSlider.Value} px)";

        PreviewLogo.Source = _logoBytes is null ? null : ImageHelper.FromBytes(_logoBytes);
        PreviewLogo.Visibility = _logoBytes is null ? Visibility.Collapsed : Visibility.Visible;

        PreviewCompany.Text = Blank(CompanyNameBox.Text) ?? _session.Groupe?.Nom ?? "Lonnii";
        PreviewCompany.FontSize = ReceiptTypography.Company(fontSize);

        PreviewTitle.Text = _showingFacture ? FactureTitleBox.Text : ReceiptTitleBox.Text;
        PreviewTitle.FontSize = titleSize;

        var now = DateTime.Now;
        PreviewNumero.Text = "N° V2026-00042";
        PreviewDate.Text = $"{now.ToString("d MMMM yyyy", French)} à {now:HH:mm}";
        PreviewNumero.FontSize = PreviewDate.FontSize = ReceiptTypography.Meta(fontSize);

        PreviewSellerLabel.Text = $"{Blank(SellerLabelBox.Text) ?? ReceiptSettingsDefaults.SellerLabel}:";
        foreach (var block in new[] { PreviewClientLabel, PreviewClient, PreviewSellerLabel, PreviewSeller })
            block.FontSize = ReceiptTypography.Body(fontSize);

        foreach (var block in new[] { PreviewColArticle, PreviewColQte, PreviewColPu, PreviewColTotal })
            block.FontSize = ReceiptTypography.Table(fontSize);

        RenderPreviewItems(fontSize);

        var total = SampleItems.Sum(i => i.Price * i.Qty);
        PreviewTotal.Text = Money.Format(total);
        PreviewTotalLabel.FontSize = PreviewTotal.FontSize = ReceiptTypography.Total(fontSize);

        PreviewFactureNotice.Visibility = _showingFacture ? Visibility.Visible : Visibility.Collapsed;
        PreviewFactureNoticeTitle.Text = FactureNoticeTitleBox.Text;
        PreviewFactureNoticeText.Text = FactureNoticeTextBox.Text;
        PreviewFactureNoticeTitle.FontSize = ReceiptTypography.Body(fontSize);
        PreviewFactureNoticeText.FontSize = ReceiptTypography.Table(fontSize);

        // Shown on the receipt side only, and only as a sample - a real receipt carries it
        // just when the client actually overpaid.
        PreviewAvoirNotice.Visibility = _showingFacture ? Visibility.Collapsed : Visibility.Visible;
        PreviewAvoirNoticeTitle.Text = AvoirNoticeTitleBox.Text;
        PreviewAvoirNoticeText.Text = $"{AvoirNoticeTextBox.Text} {Money.Format(SampleAvoir)}";
        PreviewAvoirNoticeTitle.FontSize = ReceiptTypography.Body(fontSize);
        PreviewAvoirNoticeText.FontSize = ReceiptTypography.Table(fontSize);

        PreviewFooter.Text = _showingFacture ? FactureFooterBox.Text : ReceiptFooterBox.Text;
        PreviewFooter.FontSize = ReceiptTypography.Body(fontSize);

        var note = Blank(NoteUnderQrBox.Text);
        PreviewQrPanel.Visibility = _qrBytes is null && note is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewQr.Source = _qrBytes is null ? null : ImageHelper.FromBytes(_qrBytes);
        PreviewQr.Visibility = _qrBytes is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewQrNote.Text = note ?? string.Empty;
        PreviewQrNote.FontSize = ReceiptTypography.Table(fontSize);
    }

    private void RenderPreviewItems(int fontSize)
    {
        PreviewItems.Items.Clear();

        foreach (var (name, qty, price) in SampleItems)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });

            Add(grid, 0, name, TextAlignment.Left);
            Add(grid, 1, qty.ToString(), TextAlignment.Center);
            Add(grid, 2, Money.FormatPlain(price), TextAlignment.Right);
            Add(grid, 3, Money.FormatPlain(price * qty), TextAlignment.Right);

            PreviewItems.Items.Add(grid);
        }

        void Add(Grid grid, int column, string text, TextAlignment alignment)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = ReceiptTypography.Table(fontSize),
                Foreground = Brushes.Black,
                TextAlignment = alignment,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }
    }

    // --- Chrome ------------------------------------------------------------------

    private void SetBusy(bool busy)
    {
        SaveButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void ShowStatus(string? message, bool error = false)
    {
        StatusText.Text = message ?? string.Empty;
        StatusText.Foreground = (Brush)FindResource(error ? "Danger" : "TextSecondary");
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
