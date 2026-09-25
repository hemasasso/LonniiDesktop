using Lonnii.Api.Security;
using Lonnii.Api.Services;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// "Paramètre Reçu et Facture": the one <c>ventes_parametres</c> row per workspace that
/// decides what a printed reçu or facture says and looks like - logo, QR code, company
/// name, the two document titles, the boxed notices and the footers.
///
/// Ported from Lonnii Business's <c>backend/routes/ventesParametres.js</c>, with three
/// deliberate differences:
/// <list type="bullet">
/// <item>The group comes from the session scope rather than a <c>:groupe</c> path segment.
/// The web route takes the id from the URL and never checks the caller belongs to that
/// group, so any signed-in user there can read or rewrite another shop's receipt.</item>
/// <item>The text and the two images are saved by separate requests, instead of one
/// multipart POST carrying both. Editing the wording is much the more common action, and
/// it should not mean re-uploading a logo that has not changed.</item>
/// <item>Images go through <see cref="ImageStorageService"/>, like product and category
/// photos, so they are re-encoded, size-capped, and served only to the owning group.</item>
/// </list>
/// </summary>
public static class ParametresEndpoints
{
    public static void MapParametresEndpoints(this IEndpointRouteBuilder app)
    {
        var parametres = app.MapGroup("/api/parametres").WithTags("Paramètres");

        // Readable by anyone in the workspace, with no further privilege: printing a
        // receipt needs these settings, and a cashier who may ring up a sale but not change
        // the shop's configuration still has to be able to print what they sold.
        parametres.MapGet("/recu", GetAsync).RequireGroupScope();

        // Writes are admin-only, for the same reason the currency label is
        // (GroupEndpoints.UpdateCurrencyAsync): the company name and the payment QR code on
        // a customer-facing document are not something a till operator should be able to
        // change quietly.
        parametres.MapPut("/recu", UpdateAsync).RequireGroupScope().RequireGroupAdmin();

        parametres.MapPost("/recu/logo", UploadLogoAsync)
            .RequireGroupScope().RequireGroupAdmin().DisableAntiforgery();
        parametres.MapDelete("/recu/logo", DeleteLogoAsync)
            .RequireGroupScope().RequireGroupAdmin();

        parametres.MapPost("/recu/qrcode", UploadQrCodeAsync)
            .RequireGroupScope().RequireGroupAdmin().DisableAntiforgery();
        parametres.MapDelete("/recu/qrcode", DeleteQrCodeAsync)
            .RequireGroupScope().RequireGroupAdmin();
    }

    /// <summary>The workspace's receipt configuration, defaults filled in. A workspace that
    /// has never opened this screen has no row at all, which is not an error - it gets the
    /// defaults, exactly as if it had saved them.</summary>
    private static async Task<IResult> GetAsync(GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var row = await db.VentesParametres
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.GroupeId == scope.GroupId, ct);

        return Results.Ok(ToDto(row));
    }

    private static async Task<IResult> UpdateAsync(
        UpdateReceiptSettingsRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (Validate(request) is { } error) return error;

        var row = await LoadOrCreateAsync(scope.GroupId, db, ct);

        row.CompanyName = Blank(request.CompanyName);
        row.NoteUnderQr = Blank(request.NoteUnderQr);

        // Trimmed-to-empty falls back to the default rather than being stored as "". A
        // receipt with a blank title where "REÇU DE VENTE" belongs looks like a bug to the
        // customer holding it, and the editor offers no other way to restore the default.
        row.ReceiptTitle = OrDefault(request.ReceiptTitle, ReceiptSettingsDefaults.ReceiptTitle);
        row.FactureTitle = OrDefault(request.FactureTitle, ReceiptSettingsDefaults.FactureTitle);
        row.FactureNoticeTitle = OrDefault(request.FactureNoticeTitle, ReceiptSettingsDefaults.FactureNoticeTitle);
        row.FactureNoticeText = OrDefault(request.FactureNoticeText, ReceiptSettingsDefaults.FactureNoticeText);
        row.FactureFooterText = OrDefault(request.FactureFooterText, ReceiptSettingsDefaults.FactureFooterText);
        row.ReceiptFooterText = OrDefault(request.ReceiptFooterText, ReceiptSettingsDefaults.ReceiptFooterText);
        row.SellerLabel = OrDefault(request.SellerLabel, ReceiptSettingsDefaults.SellerLabel);
        row.AvoirNoticeTitle = OrDefault(request.AvoirNoticeTitle, ReceiptSettingsDefaults.AvoirNoticeTitle);
        row.AvoirNoticeText = OrDefault(request.AvoirNoticeText, ReceiptSettingsDefaults.AvoirNoticeText);

        // An unknown typeface is refused rather than stored: the host laptop and the till
        // that prints are different machines, and a font only one of them has would silently
        // print as something else.
        row.ReceiptFontFamily = ReceiptSettingsDefaults.FontFamilies
            .FirstOrDefault(f => string.Equals(f, request.FontFamily?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? ReceiptSettingsDefaults.FontFamily;

        row.ReceiptFontSize = ReceiptSettingsDefaults.ClampFontSize(request.FontSize);
        row.ReceiptTitleFontSize = ReceiptSettingsDefaults.ClampTitleFontSize(request.ReceiptTitleFontSize);
        row.FactureTitleFontSize = ReceiptSettingsDefaults.ClampTitleFontSize(request.FactureTitleFontSize);

        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToDto(row));
    }

    private static Task<IResult> UploadLogoAsync(
        IFormFile file, GroupScope scope, LonniiDbContext db, ImageStorageService images, CancellationToken ct) =>
        SaveImageAsync(file, scope, db, images, ImageStorageService.Folders.ReceiptLogos, lossless: false, ct);

    private static Task<IResult> UploadQrCodeAsync(
        IFormFile file, GroupScope scope, LonniiDbContext db, ImageStorageService images, CancellationToken ct) =>
        SaveImageAsync(file, scope, db, images, ImageStorageService.Folders.ReceiptQrCodes, lossless: true, ct);

    private static Task<IResult> DeleteLogoAsync(
        GroupScope scope, LonniiDbContext db, ImageStorageService images, CancellationToken ct) =>
        RemoveImageAsync(scope, db, images, ImageStorageService.Folders.ReceiptLogos, ct);

    private static Task<IResult> DeleteQrCodeAsync(
        GroupScope scope, LonniiDbContext db, ImageStorageService images, CancellationToken ct) =>
        RemoveImageAsync(scope, db, images, ImageStorageService.Folders.ReceiptQrCodes, ct);

    /// <summary>
    /// Stores an uploaded logo or QR code against the workspace. The stored filename is
    /// built from the group id, which is what <c>ImageEndpoints</c> later compares against
    /// the caller's own group before serving the bytes back.
    /// </summary>
    private static async Task<IResult> SaveImageAsync(
        IFormFile file,
        GroupScope scope,
        LonniiDbContext db,
        ImageStorageService images,
        string folder,
        bool lossless,
        CancellationToken ct)
    {
        if (Validate(file) is { } error) return error;

        var row = await LoadOrCreateAsync(scope.GroupId, db, ct);
        var isLogo = folder == ImageStorageService.Folders.ReceiptLogos;
        var previous = isLogo ? row.LogoPath : row.QrCodePath;

        string url;
        await using (var stream = file.OpenReadStream())
        {
            try
            {
                url = images.Save(folder, scope.GroupId, stream, previous, lossless);
            }
            catch (InvalidImageException)
            {
                return Results.BadRequest(new ApiError("Le fichier n'est pas une image valide"));
            }
        }

        if (isLogo) row.LogoPath = url; else row.QrCodePath = url;

        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new ImageUploadResponse(url));
    }

    private static async Task<IResult> RemoveImageAsync(
        GroupScope scope, LonniiDbContext db, ImageStorageService images, string folder, CancellationToken ct)
    {
        var row = await db.VentesParametres.FirstOrDefaultAsync(p => p.GroupeId == scope.GroupId, ct);

        // Nothing configured yet, so nothing to remove - and no reason to create an empty
        // row just to say so.
        if (row is null) return Results.NoContent();

        var isLogo = folder == ImageStorageService.Folders.ReceiptLogos;

        images.DeleteIfOwned(isLogo ? row.LogoPath : row.QrCodePath);
        if (isLogo) row.LogoPath = null; else row.QrCodePath = null;

        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    /// <summary>
    /// The workspace's row, added to the change tracker if it did not exist. Settings are
    /// created lazily: a workspace only gets a row the first time someone saves something,
    /// which is why <see cref="GetAsync"/> has to cope with there being none.
    /// </summary>
    private static async Task<VentesParametres> LoadOrCreateAsync(
        string groupId, LonniiDbContext db, CancellationToken ct)
    {
        var row = await db.VentesParametres.FirstOrDefaultAsync(p => p.GroupeId == groupId, ct);
        if (row is not null) return row;

        row = new VentesParametres { GroupeId = groupId };
        db.VentesParametres.Add(row);
        return row;
    }

    /// <summary>Caps the free-text fields at the widths the live columns actually have -
    /// <c>VARCHAR(100)</c> for the titles, <c>VARCHAR(255)</c> for the rest - so an
    /// over-long value fails here with a readable message instead of at the database.</summary>
    private static IResult? Validate(UpdateReceiptSettingsRequest r)
    {
        (string Label, string? Value, int Max)[] fields =
        [
            ("Le nom de l'entreprise", r.CompanyName, 255),
            ("La note sous le QR code", r.NoteUnderQr, 255),
            ("Le titre du reçu", r.ReceiptTitle, 100),
            ("Le titre de la facture", r.FactureTitle, 100),
            ("Le titre de l'encadré", r.FactureNoticeTitle, 255),
            ("Le texte de l'encadré", r.FactureNoticeText, 255),
            ("Le message de fin de la facture", r.FactureFooterText, 255),
            ("Le message de fin du reçu", r.ReceiptFooterText, 255),
            ("Le libellé du vendeur", r.SellerLabel, 255),
            ("Le titre de la notice d'avoir", r.AvoirNoticeTitle, 255),
            ("Le texte de la notice d'avoir", r.AvoirNoticeText, 255),
        ];

        foreach (var (label, value, max) in fields)
        {
            if (value is { Length: > 0 } && value.Trim().Length > max)
                return Results.BadRequest(new ApiError($"{label} ne peut pas dépasser {max} caractères."));
        }

        return null;
    }

    /// <summary>Same upload checks as the product and category photos.</summary>
    private static IResult? Validate(IFormFile file)
    {
        if (file.Length == 0)
            return Results.BadRequest(new ApiError("Aucun fichier reçu"));

        if (file.Length > ImageStorageService.MaxUploadBytes)
        {
            var maxMb = ImageStorageService.MaxUploadBytes / (1024 * 1024);
            return Results.BadRequest(new ApiError($"L'image dépasse la taille maximale de {maxMb} Mo"));
        }

        if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new ApiError("Le fichier doit être une image"));

        return null;
    }

    /// <summary>
    /// Turns the stored row - or the absence of one - into a DTO with every default already
    /// resolved. Doing it here rather than in each caller is what stops the printed receipt
    /// and the editor's preview drifting apart.
    /// </summary>
    internal static ReceiptSettingsDto ToDto(VentesParametres? p) => new(
        CompanyName: Blank(p?.CompanyName),
        NoteUnderQr: Blank(p?.NoteUnderQr),
        LogoUrl: Blank(p?.LogoPath),
        QrCodeUrl: Blank(p?.QrCodePath),
        ReceiptTitle: OrDefault(p?.ReceiptTitle, ReceiptSettingsDefaults.ReceiptTitle),
        FactureTitle: OrDefault(p?.FactureTitle, ReceiptSettingsDefaults.FactureTitle),
        FactureNoticeTitle: OrDefault(p?.FactureNoticeTitle, ReceiptSettingsDefaults.FactureNoticeTitle),
        FactureNoticeText: OrDefault(p?.FactureNoticeText, ReceiptSettingsDefaults.FactureNoticeText),
        FactureFooterText: OrDefault(p?.FactureFooterText, ReceiptSettingsDefaults.FactureFooterText),
        ReceiptFooterText: OrDefault(p?.ReceiptFooterText, ReceiptSettingsDefaults.ReceiptFooterText),
        SellerLabel: OrDefault(p?.SellerLabel, ReceiptSettingsDefaults.SellerLabel),
        AvoirNoticeTitle: OrDefault(p?.AvoirNoticeTitle, ReceiptSettingsDefaults.AvoirNoticeTitle),
        AvoirNoticeText: OrDefault(p?.AvoirNoticeText, ReceiptSettingsDefaults.AvoirNoticeText),
        FontFamily: OrDefault(p?.ReceiptFontFamily, ReceiptSettingsDefaults.FontFamily),
        FontSize: ReceiptSettingsDefaults.ClampFontSize(p?.ReceiptFontSize ?? ReceiptSettingsDefaults.FontSize),
        ReceiptTitleFontSize: ReceiptSettingsDefaults.ClampTitleFontSize(
            p?.ReceiptTitleFontSize ?? ReceiptSettingsDefaults.TitleFontSize),
        FactureTitleFontSize: ReceiptSettingsDefaults.ClampTitleFontSize(
            p?.FactureTitleFontSize ?? ReceiptSettingsDefaults.TitleFontSize));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string OrDefault(string? value, string fallback) => Blank(value) ?? fallback;
}
