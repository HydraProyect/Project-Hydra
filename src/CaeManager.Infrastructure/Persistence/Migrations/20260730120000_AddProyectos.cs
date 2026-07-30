using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProyectos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProyectoId",
                table: "Documentos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Proyectos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClienteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CentroId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FechaInicio = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    FechaFinPrevista = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    FechaCierreReal = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Notas = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "INTEGER", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Proyectos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProyectosTecnicos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProyectoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FechaAlta = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    FechaBaja = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProyectosTecnicos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_ProyectoId_TipoDocumentoId",
                table: "Documentos",
                columns: new[] { "ProyectoId", "TipoDocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_Proyectos_CentroId",
                table: "Proyectos",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_Proyectos_TenantId_ClienteId_Nombre",
                table: "Proyectos",
                columns: new[] { "TenantId", "ClienteId", "Nombre" },
                unique: true,
                filter: "\"EstaEliminado\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_ProyectosTecnicos_TrabajadorId",
                table: "ProyectosTecnicos",
                column: "TrabajadorId");

            migrationBuilder.CreateIndex(
                name: "IX_ProyectosTecnicos_TenantId_ProyectoId_TrabajadorId_FechaAlta",
                table: "ProyectosTecnicos",
                columns: new[] { "TenantId", "ProyectoId", "TrabajadorId", "FechaAlta" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProyectosTecnicos");

            migrationBuilder.DropTable(
                name: "Proyectos");

            migrationBuilder.DropIndex(
                name: "IX_Documentos_ProyectoId_TipoDocumentoId",
                table: "Documentos");

            migrationBuilder.DropColumn(
                name: "ProyectoId",
                table: "Documentos");
        }
    }
}
