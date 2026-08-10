using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarRegistroTiempoGestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegistrosTiempoGestion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: true),
                    InicioUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SegundosActivos = table.Column<int>(type: "integer", nullable: false),
                    Motivo = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrosTiempoGestion", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosTiempoGestion_ConversacionId",
                table: "RegistrosTiempoGestion",
                column: "ConversacionId");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosTiempoGestion_TenantId_ClienteId_FinUtc",
                table: "RegistrosTiempoGestion",
                columns: new[] { "TenantId", "ClienteId", "FinUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosTiempoGestion_TenantId_UsuarioId_FinUtc",
                table: "RegistrosTiempoGestion",
                columns: new[] { "TenantId", "UsuarioId", "FinUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegistrosTiempoGestion");
        }
    }
}
