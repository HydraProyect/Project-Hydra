using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ObligatoriosCatalogoTrabajadorCae : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                columns: new[] { "EsObligatorio", "Notas" },
                values: new object[] { true, "Renovación anual estándar. Obligatorio por defecto (2026-08-09): sí o sí exigido en CAE." });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                columns: new[] { "EsObligatorio", "Notas" },
                values: new object[] { true, "Se firman cada año según nota de origen. Obligatorio por defecto (2026-08-09): justificante de entrega de EPIs." });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000004"),
                columns: new[] { "EsObligatorio", "Notas" },
                values: new object[] { true, "Recordatorio cada 3 años. Obligatorio por defecto (2026-08-09): formación PRL Art. 19." });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000008"),
                columns: new[] { "EsObligatorio", "Notas" },
                values: new object[] { true, "No consta periodicidad de renovación. Obligatorio por defecto (2026-08-09): registro de entrega de información de riesgos del puesto." });

            migrationBuilder.InsertData(
                table: "TiposDocumento",
                columns: new[] { "Id", "AmbitoAplicacion", "AplicaVencimientoAutomatico", "CriteriosValidacion", "Descripcion", "DeteccionTrabajadoresActiva", "EsObligatorio", "LecturaIaActiva", "Nombre", "Notas", "Observaciones", "Orden", "PerfilDocumentoOficial", "SeSolicitaA", "TenantId", "VerificacionIaActiva", "VigenciaMeses" },
                values: new object[] { new Guid("50000016-0000-0000-0000-000000000016"), "Trabajador", false, null, null, false, true, true, "DNI o NIE en vigor", "Verifica identidad y permiso de trabajo. Vigencia según DGT/Extranjería — vencimiento manual. Obligatorio por defecto (2026-08-09).", null, 37, "Ninguno", null, new Guid("00000000-0000-0000-0000-000000000001"), false, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000016-0000-0000-0000-000000000016"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                columns: new[] { "EsObligatorio", "Notas" },
                values: new object[] { false, "Renovación anual estándar." });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                columns: new[] { "EsObligatorio", "Notas" },
                values: new object[] { false, "Se firman cada año según nota de origen." });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000004"),
                columns: new[] { "EsObligatorio", "Notas" },
                values: new object[] { false, "Recordatorio cada 3 años." });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000008"),
                columns: new[] { "EsObligatorio", "Notas" },
                values: new object[] { false, "No consta periodicidad de renovación." });
        }
    }
}
