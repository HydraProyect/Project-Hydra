using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeTiposDocumentoReaLibroSubcontratacionReta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "TiposDocumento",
                columns: new[] { "Id", "AmbitoAplicacion", "AplicaVencimientoAutomatico", "CriteriosValidacion", "Descripcion", "DeteccionTrabajadoresActiva", "LecturaIaActiva", "Naturaleza", "Nombre", "Notas", "Observaciones", "Orden", "PerfilDocumentoOficial", "Requerido", "SeSolicitaA", "Sensibilidad", "TenantId", "VerificacionIaActiva", "VigenciaMeses" },
                values: new object[,]
                {
                    { new Guid("80000000-0000-0000-0000-000000000001"), "Empresa", true, null, null, false, true, "ObligacionCondicionada", "Certificado de inscripción en el REA", "Registro de Empresas Acreditadas (Ley 32/2006 y RD 1109/2007). Solo aplica a obras de construcción. La inscripción tiene validez de 3 años y se renueva.", null, 54, "Ninguno", "Condicional", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 36 },
                    { new Guid("80000000-0000-0000-0000-000000000002"), "Proyecto", false, null, null, false, true, "ObligacionCondicionada", "Libro de Subcontratación", "Art. 8 Ley 32/2006. Uno por obra: lo lleva el contratista principal y recoge cada subcontratación. Sin caducidad mientras dure la obra.", null, 1, "Ninguno", "Condicional", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("80000000-0000-0000-0000-000000000003"), "Empresa", true, null, null, false, true, "RequisitoCliente", "Recibo de cuota de autónomos (RETA)", "Justificante mensual de pago de la cuota del Régimen Especial de Trabajadores Autónomos. El autónomo es su propia Empresa.", null, 55, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 1 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000003"));
        }
    }
}
