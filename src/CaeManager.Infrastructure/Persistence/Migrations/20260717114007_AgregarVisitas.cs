using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgregarVisitas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Visitas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CentroId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FechaInicio = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    FechaFin = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    NotificadoCliente = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notas = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "INTEGER", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Visitas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VisitasTrabajadores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VisitaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisitasTrabajadores", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Visitas_CentroId",
                table: "Visitas",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_Visitas_FechaFin",
                table: "Visitas",
                column: "FechaFin");

            migrationBuilder.CreateIndex(
                name: "IX_VisitasTrabajadores_TrabajadorId",
                table: "VisitasTrabajadores",
                column: "TrabajadorId");

            migrationBuilder.CreateIndex(
                name: "IX_VisitasTrabajadores_VisitaId",
                table: "VisitasTrabajadores",
                column: "VisitaId");

            migrationBuilder.CreateIndex(
                name: "IX_VisitasTrabajadores_VisitaId_TrabajadorId",
                table: "VisitasTrabajadores",
                columns: new[] { "VisitaId", "TrabajadorId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Visitas");

            migrationBuilder.DropTable(
                name: "VisitasTrabajadores");
        }
    }
}
