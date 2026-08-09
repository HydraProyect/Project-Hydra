using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ModalidadPreventivaObligatoriaCae : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000006"),
                columns: new[] { "EsObligatorio", "Notas" },
                values: new object[] { true, "Vigente hasta cambios — vencimiento manual. Obligatorio por defecto (2026-08-09): un único tipo cubre la modalidad preventiva de la empresa, sea SPA, Propia o Mancomunada — no se modela como alternativas separadas (ver ROADMAP.md)." });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000006"),
                columns: new[] { "EsObligatorio", "Notas" },
                values: new object[] { false, "Vigente hasta cambios — vencimiento manual." });
        }
    }
}
