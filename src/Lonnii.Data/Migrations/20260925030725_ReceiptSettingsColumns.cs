using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <summary>
    /// Reconciles <c>ventes_parametres</c> with the columns Lonnii Business's own migrations
    /// gave it, so "Paramètre Reçu et Facture" writes the same row in both applications.
    ///
    /// <para>
    /// Four columns are dropped rather than renamed. The scaffolder proposed renames -
    /// <c>font_family</c> to <c>seller_label</c>, <c>font_size</c> to
    /// <c>receipt_title_font_size</c>, <c>facture_header_text</c> to <c>receipt_title</c>,
    /// <c>document_signatory</c> to <c>receipt_font_family</c> - purely because the types
    /// line up. They are different settings, and a rename would have moved "Courier New"
    /// into the field printed before the seller's name. The columns also never held
    /// anything: they existed in the model but no endpoint read or wrote the table before
    /// this change, so there is no data to preserve.
    /// </para>
    /// </summary>
    public partial class ReceiptSettingsColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Modelled but never written; see the class comment for why these are not renames.
            migrationBuilder.DropColumn(name: "show_date", table: "ventes_parametres");
            migrationBuilder.DropColumn(name: "show_document_signatory", table: "ventes_parametres");
            migrationBuilder.DropColumn(name: "document_signatory", table: "ventes_parametres");
            migrationBuilder.DropColumn(name: "facture_header_text", table: "ventes_parametres");
            migrationBuilder.DropColumn(name: "font_family", table: "ventes_parametres");
            migrationBuilder.DropColumn(name: "font_size", table: "ventes_parametres");

            migrationBuilder.AddColumn<string>(
                name: "facture_notice_title", table: "ventes_parametres", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "facture_notice_text", table: "ventes_parametres", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "facture_title_font_size", table: "ventes_parametres", type: "INTEGER", nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "receipt_title", table: "ventes_parametres", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "receipt_title_font_size", table: "ventes_parametres", type: "INTEGER", nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "seller_label", table: "ventes_parametres", type: "TEXT", nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "receipt_font_family", table: "ventes_parametres", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "receipt_font_size", table: "ventes_parametres", type: "INTEGER", nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "facture_notice_title", table: "ventes_parametres");
            migrationBuilder.DropColumn(name: "facture_notice_text", table: "ventes_parametres");
            migrationBuilder.DropColumn(name: "facture_title_font_size", table: "ventes_parametres");
            migrationBuilder.DropColumn(name: "receipt_title", table: "ventes_parametres");
            migrationBuilder.DropColumn(name: "receipt_title_font_size", table: "ventes_parametres");
            migrationBuilder.DropColumn(name: "seller_label", table: "ventes_parametres");
            migrationBuilder.DropColumn(name: "receipt_font_family", table: "ventes_parametres");
            migrationBuilder.DropColumn(name: "receipt_font_size", table: "ventes_parametres");

            migrationBuilder.AddColumn<string>(
                name: "document_signatory", table: "ventes_parametres", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "facture_header_text", table: "ventes_parametres", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "font_family", table: "ventes_parametres", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "font_size", table: "ventes_parametres", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<bool>(
                name: "show_date", table: "ventes_parametres", type: "INTEGER",
                nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(
                name: "show_document_signatory", table: "ventes_parametres", type: "INTEGER",
                nullable: false, defaultValue: false);
        }
    }
}
