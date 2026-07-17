using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgregarAmbitoDocumentos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AmbitoAplicacion",
                table: "TiposDocumento",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<Guid>(
                name: "TrabajadorId",
                table: "Documentos",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<Guid>(
                name: "ClienteId",
                table: "Documentos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EmpresaId",
                table: "Documentos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                column: "AmbitoAplicacion",
                value: "Trabajador");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                column: "AmbitoAplicacion",
                value: "Trabajador");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000003"),
                column: "AmbitoAplicacion",
                value: "Trabajador");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000004"),
                column: "AmbitoAplicacion",
                value: "Trabajador");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000005"),
                column: "AmbitoAplicacion",
                value: "Trabajador");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000006"),
                column: "AmbitoAplicacion",
                value: "Trabajador");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000007"),
                column: "AmbitoAplicacion",
                value: "Trabajador");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000008"),
                column: "AmbitoAplicacion",
                value: "Trabajador");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000009"),
                column: "AmbitoAplicacion",
                value: "Trabajador");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000a"),
                column: "AmbitoAplicacion",
                value: "Trabajador");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000b"),
                column: "AmbitoAplicacion",
                value: "Trabajador");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000c"),
                column: "AmbitoAplicacion",
                value: "Trabajador");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000d"),
                column: "AmbitoAplicacion",
                value: "Trabajador");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000e"),
                column: "AmbitoAplicacion",
                value: "Trabajador");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000f"),
                column: "AmbitoAplicacion",
                value: "Trabajador");

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_ClienteId_TipoDocumentoId",
                table: "Documentos",
                columns: new[] { "ClienteId", "TipoDocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_EmpresaId_TipoDocumentoId",
                table: "Documentos",
                columns: new[] { "EmpresaId", "TipoDocumentoId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Documentos_ClienteId_TipoDocumentoId",
                table: "Documentos");

            migrationBuilder.DropIndex(
                name: "IX_Documentos_EmpresaId_TipoDocumentoId",
                table: "Documentos");

            migrationBuilder.DropColumn(
                name: "AmbitoAplicacion",
                table: "TiposDocumento");

            migrationBuilder.DropColumn(
                name: "ClienteId",
                table: "Documentos");

            migrationBuilder.DropColumn(
                name: "EmpresaId",
                table: "Documentos");

            migrationBuilder.AlterColumn<Guid>(
                name: "TrabajadorId",
                table: "Documentos",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
