using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgrupadorXorRegistroActividadSoporte : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "DelegacionTenantId",
                table: "RegistrosActividadSoporte",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "SesionPrivilegiadaId",
                table: "RegistrosActividadSoporte",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosActividadSoporte_SesionPrivilegiadaId_OcurridaEnUtc",
                table: "RegistrosActividadSoporte",
                columns: new[] { "SesionPrivilegiadaId", "OcurridaEnUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_RegistrosActividadSoporte_UnSoloAgrupador",
                table: "RegistrosActividadSoporte",
                sql: "(\"DelegacionTenantId\" IS NULL) <> (\"SesionPrivilegiadaId\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RegistrosActividadSoporte_SesionPrivilegiadaId_OcurridaEnUtc",
                table: "RegistrosActividadSoporte");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RegistrosActividadSoporte_UnSoloAgrupador",
                table: "RegistrosActividadSoporte");

            migrationBuilder.DropColumn(
                name: "SesionPrivilegiadaId",
                table: "RegistrosActividadSoporte");

            migrationBuilder.AlterColumn<Guid>(
                name: "DelegacionTenantId",
                table: "RegistrosActividadSoporte",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
