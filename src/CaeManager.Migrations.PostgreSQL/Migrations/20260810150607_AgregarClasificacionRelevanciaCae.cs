using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarClasificacionRelevanciaCae : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClasificacionesRelevanciaCae",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EsAccionableCae = table.Column<bool>(type: "boolean", nullable: false),
                    Resumen = table.Column<string>(type: "text", nullable: false),
                    Confianza = table.Column<int>(type: "integer", nullable: false),
                    ActualizadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClasificacionesRelevanciaCae", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClasificacionesRelevanciaCae_ConversacionId",
                table: "ClasificacionesRelevanciaCae",
                column: "ConversacionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClasificacionesRelevanciaCae");
        }
    }
}
