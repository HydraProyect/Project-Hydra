using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarClasificacionRuidoDetalleGestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClasificacionesRuidoDetalleGestion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DetalleSugerenciaGestionCorreoId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReclamacionDocumentalDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfirmadaManualmente = table.Column<bool>(type: "boolean", nullable: false),
                    CreadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClasificacionesRuidoDetalleGestion", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClasificacionesRuidoDetalleGestion_DetalleSugerenciaGestion~",
                table: "ClasificacionesRuidoDetalleGestion",
                column: "DetalleSugerenciaGestionCorreoId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClasificacionesRuidoDetalleGestion");
        }
    }
}
