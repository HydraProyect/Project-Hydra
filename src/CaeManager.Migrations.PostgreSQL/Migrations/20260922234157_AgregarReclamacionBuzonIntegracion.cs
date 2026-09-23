using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarReclamacionBuzonIntegracion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReclamacionesBuzonIntegracion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BuzonEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    TenantPropietarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConexionIntegracionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReclamadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReclamacionesBuzonIntegracion", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReclamacionesBuzonIntegracion_BuzonEmail",
                table: "ReclamacionesBuzonIntegracion",
                column: "BuzonEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReclamacionesBuzonIntegracion_ConexionIntegracionId",
                table: "ReclamacionesBuzonIntegracion",
                column: "ConexionIntegracionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReclamacionesBuzonIntegracion");
        }
    }
}
