using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Papel por Centro (DECISION_LOG, 2026-11-08): ancla opcional <c>Centros.RelacionEmpresarialEntradaId</c> hacia
    /// la Relación Empresarial por la que la Empresa del Centro entra como subcontrata. Aditiva: columna anulable,
    /// sin relleno (todos los Centros existentes quedan como contratista principal). FK compuesta con
    /// <c>TenantId</c>, como las demás de <c>Centros</c>: la base rechaza una arista de otro Tenant propietario.
    /// No toca políticas RLS ni privilegios: la columna hereda las políticas de fila de <c>Centros</c> y los
    /// privilegios de tabla de cada rol.
    /// </summary>
    public partial class AnadeAnclaPapelEntradaCentro : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RelacionEmpresarialEntradaId",
                table: "Centros",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Centros_TenantId_RelacionEmpresarialEntradaId",
                table: "Centros",
                columns: new[] { "TenantId", "RelacionEmpresarialEntradaId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Centros_RelacionesEmpresariales_TenantId_RelacionEmpresaria~",
                table: "Centros",
                columns: new[] { "TenantId", "RelacionEmpresarialEntradaId" },
                principalTable: "RelacionesEmpresariales",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Centros_RelacionesEmpresariales_TenantId_RelacionEmpresaria~",
                table: "Centros");

            migrationBuilder.DropIndex(
                name: "IX_Centros_TenantId_RelacionEmpresarialEntradaId",
                table: "Centros");

            migrationBuilder.DropColumn(
                name: "RelacionEmpresarialEntradaId",
                table: "Centros");
        }
    }
}
