using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Lonnii.Client.Printing;
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

    private sealed record TemplateOption(string Value, string Label);

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

        var templates = ReceiptTemplates.All.Select(t => new TemplateOption(t, ReceiptTemplates.Label(t))).ToList();
        ReceiptTemplateBox.ItemsSource = templates;
        FactureTemplateBox.ItemsSource = templates;

        BuildSectionChecks(ReceiptSectionsPanel, ReceiptSections.All, facture: false);
        BuildSectionChecks(FactureSectionsPanel, ReceiptSections.Facture, facture: true);

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
            CompanyAddressBox, CompanyPhoneBox, CompanyEmailBox, CompanyLegalInfoBox, LegalFooterBox, TvaRateBox,
        ];
        foreach (var box in boxes) box.TextChanged += (_, _) => RenderPreview();

        FontFamilyBox.SelectionChanged += (_, _) => RenderPreview();
        ReceiptTemplateBox.SelectionChanged += (_, _) => RenderPreview();
        TvaIncluseRadio.Checked += (_, _) => RenderPreview();
        TvaAjouteeRadio.Checked += (_, _) => RenderPreview();
        FactureTemplateBox.SelectionChanged += (_, _) => RenderPreview();

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

        CompanyAddressBox.Text = s.CompanyAddress ?? string.Empty;
        CompanyPhoneBox.Text = s.CompanyPhone ?? string.Empty;
        CompanyEmailBox.Text = s.CompanyEmail ?? string.Empty;
        CompanyLegalInfoBox.Text = s.CompanyLegalInfo ?? string.Empty;
        LegalFooterBox.Text = s.LegalFooterText ?? string.Empty;
        TvaRateBox.Text = s.TvaRate is { } rate ? rate.ToString("0.##", French) : string.Empty;
        TvaAjouteeRadio.IsChecked = s.TvaMode == TvaModes.Ajoutee;
        TvaIncluseRadio.IsChecked = s.TvaMode != TvaModes.Ajoutee;
        ReceiptPrintAfterSaleCheck.IsChecked = s.ReceiptPrintAfterSale;
        FacturePrintAfterSaleCheck.IsChecked = s.FacturePrintAfterSale;

        ReceiptTemplateBox.SelectedValue = s.ReceiptTemplate;
        FactureTemplateBox.SelectedValue = s.FactureTemplate;
        SetSectionChecks(ReceiptSectionsPanel, s.HiddenSections(facture: false));
        SetSectionChecks(FactureSectionsPanel, s.HiddenSections(facture: true));

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
        if (!TryReadTvaRate(out _))
        {
            ShowStatus("Le taux de TVA doit être un nombre entre 0 et 100, ex. 19,25.", error: true);
            TvaRateBox.Focus();
            return;
        }

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
        FactureTitleFontSize: (int)FactureTitleSizeSlider.Value,
        ReceiptTemplate: SelectedTemplate(ReceiptTemplateBox),
        FactureTemplate: SelectedTemplate(FactureTemplateBox),
        ReceiptHiddenSections: HiddenSections(ReceiptSectionsPanel),
        FactureHiddenSections: HiddenSections(FactureSectionsPanel),
        CompanyAddress: Blank(CompanyAddressBox.Text),
        CompanyPhone: Blank(CompanyPhoneBox.Text),
        CompanyEmail: Blank(CompanyEmailBox.Text),
        CompanyLegalInfo: Blank(CompanyLegalInfoBox.Text),
        LegalFooterText: Blank(LegalFooterBox.Text),
        TvaRate: TryReadTvaRate(out var rate) ? rate : null,
        TvaMode: SelectedTvaMode,
        ReceiptPrintAfterSale: ReceiptPrintAfterSaleCheck.IsChecked == true,
        FacturePrintAfterSale: FacturePrintAfterSaleCheck.IsChecked == true);

    // --- Sections ----------------------------------------------------------------

    private void BuildSectionChecks(WrapPanel panel, IReadOnlyList<string> sections, bool facture)
    {
        foreach (var section in sections)
        {
            var check = new CheckBox
            {
                Content = ReceiptSections.Label(section, facture),
                Tag = section,
                Width = 210,
                Margin = new Thickness(0, 0, 8, 6),
                ToolTip = SectionHint(section, facture),
            };
            check.Checked += (_, _) => RenderPreview();
            check.Unchecked += (_, _) => RenderPreview();
            panel.Children.Add(check);
        }
    }

    /// <summary>Why a checked section might still not print - otherwise ticking "Détail TVA"
    /// with no rate set looks like the switch is broken.</summary>
    private static string? SectionHint(string section, bool facture) => section switch
    {
        ReceiptSections.CompanyContact => "Imprimé seulement si l'adresse, le téléphone ou l'email est rempli.",
        ReceiptSections.CompanyLegal => "Imprimé seulement si les mentions légales sont remplies.",
        ReceiptSections.Tva => "Imprimé seulement si un taux de TVA est renseigné. Toujours imprimé quand la TVA est ajoutée au prix, pour que le total s'explique.",
        ReceiptSections.LegalFooter => "Imprimé seulement si le texte de bas de page est rempli.",
        ReceiptSections.Qr => "Imprimé seulement si un QR code ou une note est configuré.",
        ReceiptSections.Cashier => "Imprimé seulement quand l'encaissement a été fait par une autre personne que le vendeur.",
        ReceiptSections.PaymentHistory => "Imprimé seulement quand la vente a été réglée en plusieurs fois.",
        ReceiptSections.Notice when !facture => "Imprimé seulement quand le client a trop payé et repart avec un avoir.",
        _ => null,
    };

    private static void SetSectionChecks(WrapPanel panel, IReadOnlyList<string> hidden)
    {
        foreach (var check in panel.Children.OfType<CheckBox>())
            check.IsChecked = !hidden.Contains((string)check.Tag);
    }

    private static List<string> HiddenSections(WrapPanel panel) =>
        panel.Children.OfType<CheckBox>().Where(c => c.IsChecked != true).Select(c => (string)c.Tag).ToList();

    private static string SelectedTemplate(ComboBox box) => box.SelectedValue as string ?? ReceiptTemplates.Ticket;

    /// <summary>Accepts "19,25" as well as "19.25". Blank is valid and means no TVA.</summary>
    private bool TryReadTvaRate(out decimal? rate)
    {
        rate = null;
        var text = TvaRateBox.Text.Trim().Replace(" ", string.Empty);
        if (text.Length == 0) return true;

        if (!decimal.TryParse(text.Replace('.', ','), NumberStyles.Number, French, out var value)
            || value < 0 || value > 100)
            return false;

        rate = value;
        return true;
    }

    // --- Live preview ------------------------------------------------------------

    /// <summary>The configuration as the form currently stands, unsaved - what the preview
    /// and the full-size test print show.</summary>
    private ReceiptSettingsDto Draft() => _loaded with
    {
        CompanyName = Blank(CompanyNameBox.Text),
        NoteUnderQr = Blank(NoteUnderQrBox.Text),
        SellerLabel = Blank(SellerLabelBox.Text) ?? ReceiptSettingsDefaults.SellerLabel,
        ReceiptTitle = Blank(ReceiptTitleBox.Text) ?? ReceiptSettingsDefaults.ReceiptTitle,
        ReceiptFooterText = ReceiptFooterBox.Text,
        AvoirNoticeTitle = AvoirNoticeTitleBox.Text,
        AvoirNoticeText = AvoirNoticeTextBox.Text,
        FactureTitle = Blank(FactureTitleBox.Text) ?? ReceiptSettingsDefaults.FactureTitle,
        FactureNoticeTitle = FactureNoticeTitleBox.Text,
        FactureNoticeText = FactureNoticeTextBox.Text,
        FactureFooterText = FactureFooterBox.Text,
        FontFamily = FontFamilyBox.SelectedItem as string ?? ReceiptSettingsDefaults.FontFamily,
        FontSize = (int)FontSizeSlider.Value,
        ReceiptTitleFontSize = (int)ReceiptTitleSizeSlider.Value,
        FactureTitleFontSize = (int)FactureTitleSizeSlider.Value,
        ReceiptTemplate = SelectedTemplate(ReceiptTemplateBox),
        FactureTemplate = SelectedTemplate(FactureTemplateBox),
        ReceiptHiddenSections = HiddenSections(ReceiptSectionsPanel),
        FactureHiddenSections = HiddenSections(FactureSectionsPanel),
        CompanyAddress = Blank(CompanyAddressBox.Text),
        CompanyPhone = Blank(CompanyPhoneBox.Text),
        CompanyEmail = Blank(CompanyEmailBox.Text),
        CompanyLegalInfo = Blank(CompanyLegalInfoBox.Text),
        LegalFooterText = Blank(LegalFooterBox.Text),
        TvaRate = TryReadTvaRate(out var rate) ? rate : null,
        TvaMode = SelectedTvaMode,
        ReceiptPrintAfterSale = ReceiptPrintAfterSaleCheck.IsChecked == true,
        FacturePrintAfterSale = FacturePrintAfterSaleCheck.IsChecked == true,
    };

    private string SelectedTvaMode => TvaAjouteeRadio.IsChecked == true ? TvaModes.Ajoutee : TvaModes.Incluse;

    /// <summary>
    /// Redraws the sample document from the current form values. Cheap enough to run on
    /// every keystroke: a few dozen elements, nothing the printer will ever see.
    /// </summary>
    private void RenderPreview()
    {
        if (_loading) return;

        // Sliders carry their current value in the label, the way the web app's do - a bare
        // slider gives no way to tell 12 from 13.
        FontSizeLabel.Text = $"Taille du texte ({(int)FontSizeSlider.Value} px)";
        ReceiptTitleSizeLabel.Text = $"Taille du titre ({(int)ReceiptTitleSizeSlider.Value} px)";
        FactureTitleSizeLabel.Text = $"Taille du titre ({(int)FactureTitleSizeSlider.Value} px)";

        var draft = Draft();
        PreviewBox.Child = ReceiptDocument.Build(
            ReceiptData.Sample(_showingFacture, draft.AddedTvaRate), draft, _logoBytes, _qrBytes, FallbackCompany,
            forPrint: false);
    }

    private void FullPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var draft = Draft();
        new VenteReceiptDialog(ReceiptData.Sample(_showingFacture, draft.AddedTvaRate), draft, _logoBytes, _qrBytes,
            FallbackCompany)
        {
            Owner = this,
        }.ShowDialog();
    }

    private string FallbackCompany => _session.Groupe?.Nom ?? "Lonnii";

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
