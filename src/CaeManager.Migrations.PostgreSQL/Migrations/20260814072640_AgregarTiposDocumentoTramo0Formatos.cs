using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarTiposDocumentoTramo0Formatos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "TiposDocumento",
                columns: new[] { "Id", "AmbitoAplicacion", "AplicaVencimientoAutomatico", "CriteriosValidacion", "Descripcion", "DeteccionTrabajadoresActiva", "EsObligatorio", "LecturaIaActiva", "Nombre", "Notas", "Observaciones", "Orden", "PerfilDocumentoOficial", "SeSolicitaA", "TenantId", "VerificacionIaActiva", "VigenciaMeses" },
                values: new object[,]
                {
                    { new Guid("60000000-0000-0000-0000-000000000001"), "Trabajador", false, null, null, false, false, true, "Autorización de uso de equipo de trabajo", "F-27 del catálogo de formatos PRL — arts. 3.4 y 5 RD 1215/1997. Vigente hasta modificación — vencimiento manual.", null, 38, "Ninguno", null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("60000000-0000-0000-0000-000000000002"), "Empresa", false, null, null, false, false, true, "Acta de presencia del recurso preventivo", "F-41 del catálogo de formatos PRL — práctica probatoria del art. 32 bis.3 LPRL. Un acta por presencia — vencimiento manual.", null, 46, "Ninguno", null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("60000000-0000-0000-0000-000000000003"), "Empresa", false, null, null, false, false, true, "Acta de reunión de coordinación", "F-47 del catálogo de formatos PRL — art. 11.b y 11.c RD 171/2004. Un acta por reunión — vencimiento manual.", null, 47, "Ninguno", null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("60000000-0000-0000-0000-000000000004"), "Empresa", false, null, null, false, false, true, "Informe de investigación de accidente o incidente", "F-70 del catálogo de formatos PRL — art. 16.3 LPRL. Un informe por accidente o incidente — vencimiento manual.", null, 48, "Ninguno", null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("60000000-0000-0000-0000-000000000005"), "Empresa", false, null, null, false, false, true, "Protocolo frente al acoso sexual y por razón de sexo", "F-93 del catálogo de formatos PRL — art. 48 LO 3/2007. Obligatorio para todas las empresas, sin umbral de plantilla. Vigente hasta revisión — vencimiento manual.", null, 49, "Ninguno", null, new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("60000000-0000-0000-0000-000000000006"), "Empresa", false, null, null, false, false, true, "Registro retributivo", "F-95 del catálogo de formatos PRL — RD 902/2020. Obligatorio para todas las empresas, sin umbral de plantilla. Vigente hasta revisión — vencimiento manual.", null, 50, "Ninguno", null, new Guid("00000000-0000-0000-0000-000000000001"), false, null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000006"));
        }
    }
}
