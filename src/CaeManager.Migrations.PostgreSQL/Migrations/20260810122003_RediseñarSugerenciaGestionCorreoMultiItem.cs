using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RediseñarSugerenciaGestionCorreoMultiItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfianzaTipoDocumento",
                table: "SugerenciasGestionCorreo");

            migrationBuilder.DropColumn(
                name: "ConfianzaTrabajador",
                table: "SugerenciasGestionCorreo");

            migrationBuilder.DropColumn(
                name: "Resuelta",
                table: "SugerenciasGestionCorreo");

            migrationBuilder.DropColumn(
                name: "TipoDocumentoId",
                table: "SugerenciasGestionCorreo");

            migrationBuilder.DropColumn(
                name: "TrabajadorId",
                table: "SugerenciasGestionCorreo");

            migrationBuilder.CreateTable(
                name: "DetallesSugerenciaGestionCorreo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SugerenciaGestionCorreoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "uuid", nullable: true),
                    TipoDocumentoId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConfianzaTrabajador = table.Column<int>(type: "integer", nullable: false),
                    ConfianzaTipoDocumento = table.Column<int>(type: "integer", nullable: false),
                    Resuelta = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DetallesSugerenciaGestionCorreo", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DetallesSugerenciaGestionCorreo_SugerenciasGestionCorreo_Su~",
                        column: x => x.SugerenciaGestionCorreoId,
                        principalTable: "SugerenciasGestionCorreo",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DetallesSugerenciaGestionCorreo_SugerenciaGestionCorreoId",
                table: "DetallesSugerenciaGestionCorreo",
                column: "SugerenciaGestionCorreoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DetallesSugerenciaGestionCorreo");

            migrationBuilder.AddColumn<int>(
                name: "ConfianzaTipoDocumento",
                table: "SugerenciasGestionCorreo",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ConfianzaTrabajador",
                table: "SugerenciasGestionCorreo",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Resuelta",
                table: "SugerenciasGestionCorreo",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "TipoDocumentoId",
                table: "SugerenciasGestionCorreo",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TrabajadorId",
                table: "SugerenciasGestionCorreo",
                type: "uuid",
                nullable: true);
        }
    }
}
