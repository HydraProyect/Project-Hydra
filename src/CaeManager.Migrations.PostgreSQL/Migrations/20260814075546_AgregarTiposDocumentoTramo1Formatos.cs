using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarTiposDocumentoTramo1Formatos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "TiposDocumento",
                columns: new[] { "Id", "AmbitoAplicacion", "AplicaVencimientoAutomatico", "CriteriosValidacion", "Descripcion", "DeteccionTrabajadoresActiva", "EsObligatorio", "LecturaIaActiva", "Nombre", "Notas", "Observaciones", "Orden", "PerfilDocumentoOficial", "SeSolicitaA", "TenantId", "VerificacionIaActiva", "VigenciaMeses" },
                values: new object[,]
                {
                    { new Guid("70000000-0000-0000-0000-000000000001"), "Empresa", false, null, null, false, false, true, "Información de riesgos propios aportados al centro", "F-44 del catálogo de formatos PRL — art. 4.2 RD 171/2004. El formato Outbound por excelencia: lo emite el contratista hacia el titular del centro. Vigente hasta modificación de los riesgos — vencimiento manual.", null, 51, "Ninguno", null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("70000000-0000-0000-0000-000000000002"), "Empresa", false, null, null, false, false, true, "Información y coordinación con trabajadores autónomos", "F-52 del catálogo de formatos PRL — art. 24.5 LPRL · art. 4.1 RD 171/2004. Vigente hasta modificación — vencimiento manual.", null, 52, "Ninguno", null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("70000000-0000-0000-0000-000000000003"), "Empresa", false, null, null, false, false, true, "Registro del deber de vigilancia sobre subcontratas", "F-50 del catálogo de formatos PRL — art. 24.3 LPRL · art. 10 RD 171/2004. El único de la familia D que la ley impone directamente al contratista principal sobre sus Subcontratas. Un registro por subcontrata/verificación — vencimiento manual.", null, 53, "Ninguno", null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("70000000-0000-0000-0000-000000000004"), "Trabajador", false, null, null, false, false, true, "Recibí de normas, procedimientos y plan de emergencia", "F-29 del catálogo de formatos PRL — arts. 18 y 20 LPRL. Vigente hasta modificación de normas/plan — vencimiento manual.", null, 39, "Ninguno", null, new Guid("00000000-0000-0000-0000-000000000001"), false, null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000004"));
        }
    }
}
