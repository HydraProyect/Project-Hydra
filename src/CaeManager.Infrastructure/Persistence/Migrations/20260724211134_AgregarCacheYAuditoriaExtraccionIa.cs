using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgregarCacheYAuditoriaExtraccionIa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditoriasExtraccionIa",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HashSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TipoEsperado = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    ProveedorCodigo = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TiempoProcesamientoMs = table.Column<long>(type: "INTEGER", nullable: false),
                    CosteEstimado = table.Column<decimal>(type: "TEXT", precision: 10, scale: 6, nullable: true),
                    NumeroPaginas = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfianzaGeneral = table.Column<int>(type: "INTEGER", nullable: false),
                    Incidencias = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreadaEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditoriasExtraccionIa", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExtraccionesIaCache",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HashSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExtraccionJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreadaEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtraccionesIaCache", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditoriasExtraccionIa_TenantId_CreadaEnUtc",
                table: "AuditoriasExtraccionIa",
                columns: new[] { "TenantId", "CreadaEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExtraccionesIaCache_TenantId_HashSha256",
                table: "ExtraccionesIaCache",
                columns: new[] { "TenantId", "HashSha256" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditoriasExtraccionIa");

            migrationBuilder.DropTable(
                name: "ExtraccionesIaCache");
        }
    }
}
