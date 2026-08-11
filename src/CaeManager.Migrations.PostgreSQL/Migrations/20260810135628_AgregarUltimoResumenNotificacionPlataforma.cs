using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarUltimoResumenNotificacionPlataforma : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UltimosResumenesNotificacionPlataforma",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProveedorPlataformaCaeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Pendientes = table.Column<int>(type: "integer", nullable: false),
                    Vencidos = table.Column<int>(type: "integer", nullable: false),
                    Rechazados = table.Column<int>(type: "integer", nullable: false),
                    ActualizadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UltimosResumenesNotificacionPlataforma", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UltimosResumenesNotificacionPlataforma_TenantId_ClienteId_P~",
                table: "UltimosResumenesNotificacionPlataforma",
                columns: new[] { "TenantId", "ClienteId", "ProveedorPlataformaCaeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UltimosResumenesNotificacionPlataforma");
        }
    }
}
