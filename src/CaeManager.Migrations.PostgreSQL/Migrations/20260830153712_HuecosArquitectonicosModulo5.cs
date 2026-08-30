using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class HuecosArquitectonicosModulo5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Proyectos_Centros_TenantId_CentroId",
                table: "Proyectos");

            migrationBuilder.DropIndex(
                name: "IX_Proyectos_TenantId_CentroId",
                table: "Proyectos");

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "ProyectosTecnicos",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Centros_TenantId_Id_ClienteId",
                table: "Centros",
                columns: new[] { "TenantId", "Id", "ClienteId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Trabajadores_EmpresaXorSubcontrata",
                table: "Trabajadores",
                sql: "(\"EmpresaId\" IS NULL) <> (\"SubcontrataId\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_Proyectos_TenantId_CentroId_ClienteId",
                table: "Proyectos",
                columns: new[] { "TenantId", "CentroId", "ClienteId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Proyectos_Centros_TenantId_CentroId_ClienteId",
                table: "Proyectos",
                columns: new[] { "TenantId", "CentroId", "ClienteId" },
                principalTable: "Centros",
                principalColumns: new[] { "TenantId", "Id", "ClienteId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Proyectos_Centros_TenantId_CentroId_ClienteId",
                table: "Proyectos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Trabajadores_EmpresaXorSubcontrata",
                table: "Trabajadores");

            migrationBuilder.DropIndex(
                name: "IX_Proyectos_TenantId_CentroId_ClienteId",
                table: "Proyectos");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Centros_TenantId_Id_ClienteId",
                table: "Centros");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ProyectosTecnicos");

            migrationBuilder.CreateIndex(
                name: "IX_Proyectos_TenantId_CentroId",
                table: "Proyectos",
                columns: new[] { "TenantId", "CentroId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Proyectos_Centros_TenantId_CentroId",
                table: "Proyectos",
                columns: new[] { "TenantId", "CentroId" },
                principalTable: "Centros",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
