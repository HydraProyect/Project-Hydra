using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarEstadoAutomatizacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EstadosAutomatizacion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrabajoId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Activo = table.Column<bool>(type: "boolean", nullable: false),
                    UltimaEjecucionUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UltimoResultadoExitoso = table.Column<bool>(type: "boolean", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EstadosAutomatizacion", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EstadosAutomatizacion_TenantId_TrabajoId",
                table: "EstadosAutomatizacion",
                columns: new[] { "TenantId", "TrabajoId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EstadosAutomatizacion");
        }
    }
}
