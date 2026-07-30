using System;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(CaeManagerDbContext))]
    [Migration("20260726120000_AddTarifasCliente")]
    public partial class AddTarifasCliente : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TarifasCliente",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClienteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Concepto = table.Column<int>(type: "INTEGER", nullable: false),
                    PrecioUnitario = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    MonedaIso = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "INTEGER", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TarifasCliente", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TarifasCliente_ClienteId",
                table: "TarifasCliente",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_TarifasCliente_TenantId_ClienteId_Concepto",
                table: "TarifasCliente",
                columns: new[] { "TenantId", "ClienteId", "Concepto" },
                unique: true,
                filter: "\"EstaEliminado\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TarifasCliente");
        }
    }
}
