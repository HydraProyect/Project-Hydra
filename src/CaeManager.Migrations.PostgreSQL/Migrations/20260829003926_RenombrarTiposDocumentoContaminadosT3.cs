using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RenombrarTiposDocumentoContaminadosT3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                column: "Nombre",
                value: "Certificado de aptitud médica");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                column: "Nombre",
                value: "Entrega de EPI");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000004"),
                column: "Nombre",
                value: "RLC");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000005"),
                column: "Notas",
                value: "Mismo criterio de vigencia que el RLC.");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000006"),
                column: "Notas",
                value: "Variante combinada — mismo criterio de vigencia que el RLC.");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000007"),
                columns: new[] { "Nombre", "Notas" },
                values: new object[] { "RNT", "Mismo criterio que el RLC." });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000a-0000-0000-0000-00000000000a"),
                column: "Nombre",
                value: "Servicio de Prevención Ajeno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000b-0000-0000-0000-00000000000b"),
                column: "Nombre",
                value: "Evaluación de Riesgos Laborales");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000c-0000-0000-0000-00000000000c"),
                column: "Nombre",
                value: "Planificación de la Actividad Preventiva");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000d-0000-0000-0000-00000000000d"),
                column: "Nombre",
                value: "Tarjeta de identificación fiscal");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000004"),
                column: "Notas",
                value: "Distinto de \"Entrega de EPI\" (antes \"EPIS (firma)\"; la entrega/firma de recepción) — esta es la formación de uso.");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000016-0000-0000-0000-000000000016"),
                column: "Nombre",
                value: "Documento de identidad");

            migrationBuilder.InsertData(
                table: "TiposDocumentoAlias",
                columns: new[] { "Id", "TenantId", "Texto", "TipoDocumentoId" },
                values: new object[,]
                {
                    { new Guid("90000000-0000-0000-0000-000000000001"), new Guid("00000000-0000-0000-0000-000000000001"), "Apto médico laboral", new Guid("10000000-0000-0000-0000-000000000001") },
                    { new Guid("90000000-0000-0000-0000-000000000002"), new Guid("00000000-0000-0000-0000-000000000001"), "EPIS (firma)", new Guid("10000000-0000-0000-0000-000000000002") },
                    { new Guid("90000000-0000-0000-0000-000000000003"), new Guid("00000000-0000-0000-0000-000000000001"), "DNI o NIE en vigor", new Guid("50000016-0000-0000-0000-000000000016") },
                    { new Guid("90000000-0000-0000-0000-000000000004"), new Guid("00000000-0000-0000-0000-000000000001"), "DNI/NIE/TIE", new Guid("50000016-0000-0000-0000-000000000016") },
                    { new Guid("90000000-0000-0000-0000-000000000005"), new Guid("00000000-0000-0000-0000-000000000001"), "RLC/TC1", new Guid("20000000-0000-0000-0000-000000000004") },
                    { new Guid("90000000-0000-0000-0000-000000000006"), new Guid("00000000-0000-0000-0000-000000000001"), "TC1", new Guid("20000000-0000-0000-0000-000000000004") },
                    { new Guid("90000000-0000-0000-0000-000000000007"), new Guid("00000000-0000-0000-0000-000000000001"), "RNT/TC2", new Guid("20000000-0000-0000-0000-000000000007") },
                    { new Guid("90000000-0000-0000-0000-000000000008"), new Guid("00000000-0000-0000-0000-000000000001"), "TC2", new Guid("20000000-0000-0000-0000-000000000007") },
                    { new Guid("90000000-0000-0000-0000-000000000009"), new Guid("00000000-0000-0000-0000-000000000001"), "SPA (Servicio de Prevención Ajeno)", new Guid("2000000a-0000-0000-0000-00000000000a") },
                    { new Guid("90000000-0000-0000-0000-000000000010"), new Guid("00000000-0000-0000-0000-000000000001"), "SPA", new Guid("2000000a-0000-0000-0000-00000000000a") },
                    { new Guid("90000000-0000-0000-0000-000000000011"), new Guid("00000000-0000-0000-0000-000000000001"), "EVR (Evaluación de Riesgos Laborales)", new Guid("2000000b-0000-0000-0000-00000000000b") },
                    { new Guid("90000000-0000-0000-0000-000000000012"), new Guid("00000000-0000-0000-0000-000000000001"), "EVR", new Guid("2000000b-0000-0000-0000-00000000000b") },
                    { new Guid("90000000-0000-0000-0000-000000000013"), new Guid("00000000-0000-0000-0000-000000000001"), "PAP (Planificación de la Actividad Preventiva)", new Guid("2000000c-0000-0000-0000-00000000000c") },
                    { new Guid("90000000-0000-0000-0000-000000000014"), new Guid("00000000-0000-0000-0000-000000000001"), "PAP", new Guid("2000000c-0000-0000-0000-00000000000c") },
                    { new Guid("90000000-0000-0000-0000-000000000015"), new Guid("00000000-0000-0000-0000-000000000001"), "Tarjeta CIF", new Guid("2000000d-0000-0000-0000-00000000000d") },
                    { new Guid("90000000-0000-0000-0000-000000000016"), new Guid("00000000-0000-0000-0000-000000000001"), "CIF", new Guid("2000000d-0000-0000-0000-00000000000d") }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000009"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000010"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000011"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000012"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000013"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000014"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000015"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000016"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                column: "Nombre",
                value: "Apto médico laboral");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                column: "Nombre",
                value: "EPIS (firma)");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000004"),
                column: "Nombre",
                value: "RLC/TC1");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000005"),
                column: "Notas",
                value: "Mismo criterio de vigencia que el RLC/TC1.");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000006"),
                column: "Notas",
                value: "Variante combinada — mismo criterio de vigencia que el RLC/TC1.");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000007"),
                columns: new[] { "Nombre", "Notas" },
                values: new object[] { "RNT/TC2", "Mismo criterio que el RLC/TC1." });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000a-0000-0000-0000-00000000000a"),
                column: "Nombre",
                value: "SPA (Servicio de Prevención Ajeno)");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000b-0000-0000-0000-00000000000b"),
                column: "Nombre",
                value: "EVR (Evaluación de Riesgos Laborales)");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000c-0000-0000-0000-00000000000c"),
                column: "Nombre",
                value: "PAP (Planificación de la Actividad Preventiva)");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000d-0000-0000-0000-00000000000d"),
                column: "Nombre",
                value: "Tarjeta CIF");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000004"),
                column: "Notas",
                value: "Distinto de \"EPIS (firma)\" (la entrega/firma de recepción) — esta es la formación de uso.");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000016-0000-0000-0000-000000000016"),
                column: "Nombre",
                value: "DNI o NIE en vigor");
        }
    }
}
