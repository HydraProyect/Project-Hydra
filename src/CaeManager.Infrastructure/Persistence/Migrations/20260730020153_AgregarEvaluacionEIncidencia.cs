using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgregarEvaluacionEIncidencia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Evaluaciones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CentroId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Fecha = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Puntuacion = table.Column<int>(type: "INTEGER", nullable: false),
                    Observaciones = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "INTEGER", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Evaluaciones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Incidencias",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CentroId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Tipo = table.Column<string>(type: "TEXT", nullable: false),
                    Gravedad = table.Column<string>(type: "TEXT", nullable: false),
                    FechaOcurrencia = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Descripcion = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Resuelta = table.Column<bool>(type: "INTEGER", nullable: false),
                    ResueltaEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "INTEGER", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidencias", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Evaluaciones_CentroId",
                table: "Evaluaciones",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_Evaluaciones_TrabajadorId",
                table: "Evaluaciones",
                column: "TrabajadorId");

            migrationBuilder.CreateIndex(
                name: "IX_Incidencias_CentroId",
                table: "Incidencias",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_Incidencias_TrabajadorId",
                table: "Incidencias",
                column: "TrabajadorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Evaluaciones");

            migrationBuilder.DropTable(
                name: "Incidencias");
        }
    }
}
