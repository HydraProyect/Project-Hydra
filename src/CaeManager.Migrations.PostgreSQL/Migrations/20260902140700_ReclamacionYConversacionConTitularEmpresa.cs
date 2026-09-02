using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ReclamacionYConversacionConTitularEmpresa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ClienteId",
                table: "ReclamacionesDocumentales",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "EmpresaId",
                table: "ReclamacionesDocumentales",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EmpresaId",
                table: "Conversaciones",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReclamacionesDocumentales_TenantId_EmpresaId",
                table: "ReclamacionesDocumentales",
                columns: new[] { "TenantId", "EmpresaId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReclamacionesDocumentales_TitularUnico",
                table: "ReclamacionesDocumentales",
                sql: "num_nonnulls(\"ClienteId\", \"EmpresaId\") = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Conversaciones_TenantId_EmpresaId",
                table: "Conversaciones",
                columns: new[] { "TenantId", "EmpresaId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Conversaciones_AnclaUnica",
                table: "Conversaciones",
                sql: "num_nonnulls(\"ClienteId\", \"EmpresaId\") <= 1");

            migrationBuilder.AddForeignKey(
                name: "FK_ReclamacionesDocumentales_Empresas_TenantId_EmpresaId",
                table: "ReclamacionesDocumentales",
                columns: new[] { "TenantId", "EmpresaId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ReclamacionesDocumentales_Empresas_TenantId_EmpresaId",
                table: "ReclamacionesDocumentales");

            migrationBuilder.DropIndex(
                name: "IX_ReclamacionesDocumentales_TenantId_EmpresaId",
                table: "ReclamacionesDocumentales");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReclamacionesDocumentales_TitularUnico",
                table: "ReclamacionesDocumentales");

            migrationBuilder.DropIndex(
                name: "IX_Conversaciones_TenantId_EmpresaId",
                table: "Conversaciones");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Conversaciones_AnclaUnica",
                table: "Conversaciones");

            migrationBuilder.DropColumn(
                name: "EmpresaId",
                table: "ReclamacionesDocumentales");

            migrationBuilder.DropColumn(
                name: "EmpresaId",
                table: "Conversaciones");

            migrationBuilder.AlterColumn<Guid>(
                name: "ClienteId",
                table: "ReclamacionesDocumentales",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
