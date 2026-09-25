using Lonnii.Data;
using Lonnii.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Tests;

/// <summary>
/// Pins the decisions taken when the model was reconciled against the live PostgreSQL
/// schema on 2026-09-24. The model had been built from SQL files in the repo that were
/// never applied to production, so these are the points where it now follows the real
/// database rather than the files.
/// </summary>
public class LiveSchemaCompatibilityTests
{
    private static LonniiDbContext Context() =>
        new(new DbContextOptionsBuilder<LonniiDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options);

    private static string ColumnOf<TEntity>(LonniiDbContext db, string property) =>
        db.Model.FindEntityType(typeof(TEntity))!.FindProperty(property)!.GetColumnName();

    /// <summary>
    /// The live ventes table uses the older English column names. The French property names
    /// are kept for the rest of the desktop code, so the mapping is what keeps the two
    /// reconciled - and a rename that quietly dropped it would only fail against production.
    /// </summary>
    [Theory]
    [InlineData(nameof(Vente.NumeroVente), "sale_number")]
    [InlineData(nameof(Vente.DateVente), "date")]
    [InlineData(nameof(Vente.ClientNom), "customer_name")]
    [InlineData(nameof(Vente.ClientTelephone), "customer_phone")]
    [InlineData(nameof(Vente.ClientEmail), "customer_email")]
    [InlineData(nameof(Vente.MontantTotal), "total_amount")]
    [InlineData(nameof(Vente.StatutPaiement), "payment_status")]
    [InlineData(nameof(Vente.ModePaiement), "payment_method")]
    [InlineData(nameof(Vente.CreatedBy), "user_id")]
    public void Vente_maps_onto_the_live_column_names(string property, string column)
    {
        using var db = Context();

        Assert.Equal(column, ColumnOf<Vente>(db, property));
    }

    [Fact]
    public void Groupe_prestations_flag_maps_onto_prestations_access()
    {
        using var db = Context();

        Assert.Equal("prestations_access", ColumnOf<Groupe>(db, nameof(Groupe.PrestationsEnabled)));
    }

    /// <summary>
    /// No montant_paye or montant_restant column exists live - both are summed from
    /// paiements_ventes. Mapping them would create a second source of truth for the same
    /// number, so they must stay off the model.
    /// </summary>
    [Fact]
    public void Paid_and_outstanding_amounts_are_not_stored_columns()
    {
        using var db = Context();
        var vente = db.Model.FindEntityType(typeof(Vente))!;

        Assert.Null(vente.FindProperty(nameof(Vente.MontantPaye)));
        Assert.Null(vente.FindProperty(nameof(Vente.MontantRestant)));
    }

    [Fact]
    public void Paid_amount_is_the_sum_of_its_payments()
    {
        var vente = new Vente { MontantTotal = 45_000m };
        vente.Paiements.Add(new PaiementVente { Montant = 20_000m });
        vente.Paiements.Add(new PaiementVente { Montant = 5_000m });

        Assert.Equal(25_000m, vente.MontantPaye);
        Assert.Equal(20_000m, vente.MontantRestant);
    }

    [Fact]
    public void A_sale_with_no_payments_owes_everything()
    {
        var vente = new Vente { MontantTotal = 45_000m };

        Assert.Equal(0m, vente.MontantPaye);
        Assert.Equal(45_000m, vente.MontantRestant);
    }

    /// <summary>
    /// Live payment_status holds English values, plus a French "annule", and the column
    /// DEFAULT is "completed" - which no row holds and which Lonnii Business's own
    /// converter does not translate.
    /// </summary>
    [Theory]
    [InlineData("paid", StatutPaiement.Paye)]
    [InlineData("completed", StatutPaiement.Paye)]
    [InlineData("partial", StatutPaiement.Partiel)]
    [InlineData("pending", StatutPaiement.EnAttente)]
    [InlineData("cancelled", StatutPaiement.Annule)]
    [InlineData("PAID", StatutPaiement.Paye)]
    public void Legacy_payment_statuses_normalise(string stored, string expected)
    {
        Assert.Equal(expected, StatutPaiement.Normalise(stored));
    }

    [Theory]
    [InlineData(StatutPaiement.Paye)]
    [InlineData(StatutPaiement.Partiel)]
    [InlineData(StatutPaiement.EnAttente)]
    [InlineData(StatutPaiement.Annule)]
    public void The_desktops_own_statuses_pass_through_unchanged(string statut)
    {
        Assert.Equal(statut, StatutPaiement.Normalise(statut));
    }

    /// <summary>
    /// An unrecognised value must surface rather than be guessed at. Defaulting it to
    /// "paid" would quietly mark unpaid sales settled; defaulting to "unpaid" would hide
    /// money already taken. Returning it unchanged makes it visible.
    /// </summary>
    [Fact]
    public void An_unknown_status_is_not_guessed_at()
    {
        Assert.Equal("quelque_chose", StatutPaiement.Normalise("quelque_chose"));
    }

    /// <summary>
    /// Receipt settings are written by both applications into the same row, so every
    /// property has to land on the column Lonnii Business's migrations created. Three of
    /// these are the ones an obvious rename would get wrong - the font columns are
    /// <c>receipt_font_*</c>, not <c>font_*</c>, and the facture notice is a pair rather
    /// than the single <c>facture_header_text</c> the model used to carry.
    /// </summary>
    [Theory]
    [InlineData(nameof(VentesParametres.GroupeId), "groupe_id")]
    [InlineData(nameof(VentesParametres.CompanyName), "company_name")]
    [InlineData(nameof(VentesParametres.NoteUnderQr), "note_under_qr")]
    [InlineData(nameof(VentesParametres.LogoPath), "logo_path")]
    [InlineData(nameof(VentesParametres.QrCodePath), "qr_code_path")]
    [InlineData(nameof(VentesParametres.FactureTitle), "facture_title")]
    [InlineData(nameof(VentesParametres.FactureNoticeTitle), "facture_notice_title")]
    [InlineData(nameof(VentesParametres.FactureNoticeText), "facture_notice_text")]
    [InlineData(nameof(VentesParametres.FactureFooterText), "facture_footer_text")]
    [InlineData(nameof(VentesParametres.FactureTitleFontSize), "facture_title_font_size")]
    [InlineData(nameof(VentesParametres.ReceiptTitle), "receipt_title")]
    [InlineData(nameof(VentesParametres.ReceiptFooterText), "receipt_footer_text")]
    [InlineData(nameof(VentesParametres.ReceiptTitleFontSize), "receipt_title_font_size")]
    [InlineData(nameof(VentesParametres.SellerLabel), "seller_label")]
    [InlineData(nameof(VentesParametres.AvoirNoticeTitle), "avoir_notice_title")]
    [InlineData(nameof(VentesParametres.AvoirNoticeText), "avoir_notice_text")]
    [InlineData(nameof(VentesParametres.ReceiptFontFamily), "receipt_font_family")]
    [InlineData(nameof(VentesParametres.ReceiptFontSize), "receipt_font_size")]
    public void VentesParametres_maps_onto_the_web_apps_column_names(string property, string column)
    {
        using var db = Context();
        Assert.Equal(column, ColumnOf<VentesParametres>(db, property));
    }

    /// <summary>
    /// The live table's primary key is a SERIAL <c>id</c>, but the model keys on
    /// <c>groupe_id</c> - which is UNIQUE there and the only column anything looks a row up
    /// by. Leaving <c>id</c> off the model is what makes an INSERT omit it so the sequence
    /// default applies; mapping it as the key would have EF try to supply a value.
    /// </summary>
    [Fact]
    public void VentesParametres_is_keyed_on_the_group_and_does_not_map_the_serial_id()
    {
        using var db = Context();
        var entity = db.Model.FindEntityType(typeof(VentesParametres))!;

        Assert.Equal(["groupe_id"], entity.FindPrimaryKey()!.Properties.Select(p => p.GetColumnName()));
        Assert.Null(entity.FindProperty("Id"));
    }
}
