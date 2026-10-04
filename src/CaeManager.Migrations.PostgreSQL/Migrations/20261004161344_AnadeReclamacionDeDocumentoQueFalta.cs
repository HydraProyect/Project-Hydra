using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeReclamacionDeDocumentoQueFalta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "DocumentoId",
                table: "ReclamacionesDocumentalesDocumentos",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "TipoDocumentoId",
                table: "ReclamacionesDocumentalesDocumentos",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TrabajadorId",
                table: "ReclamacionesDocumentalesDocumentos",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReclamacionesDocumentalesDocumentos_Forma",
                table: "ReclamacionesDocumentalesDocumentos",
                sql: "(\"DocumentoId\" IS NOT NULL AND \"TipoDocumentoId\" IS NULL AND \"TrabajadorId\" IS NULL) OR (\"DocumentoId\" IS NULL AND \"TipoDocumentoId\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ReclamacionesDocumentalesDocumentos_Forma",
                table: "ReclamacionesDocumentalesDocumentos");

            migrationBuilder.DropColumn(
                name: "TipoDocumentoId",
                table: "ReclamacionesDocumentalesDocumentos");

            migrationBuilder.DropColumn(
                name: "TrabajadorId",
                table: "ReclamacionesDocumentalesDocumentos");

            migrationBuilder.AlterColumn<Guid>(
                name: "DocumentoId",
                table: "ReclamacionesDocumentalesDocumentos",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
