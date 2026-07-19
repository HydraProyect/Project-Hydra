using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AmpliarCatalogoTiposDocumento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "TiposDocumento",
                columns: new[] { "Id", "AmbitoAplicacion", "AplicaVencimientoAutomatico", "CriteriosValidacion", "Descripcion", "DeteccionTrabajadoresActiva", "EsObligatorio", "LecturaIaActiva", "Nombre", "Notas", "Observaciones", "Orden", "SeSolicitaA", "VigenciaMeses" },
                values: new object[,]
                {
                    { new Guid("40000000-0000-0000-0000-000000000001"), "Empresa", false, null, null, false, true, true, "Plan de Prevención", "Vigente con revisiones — vencimiento manual.", null, 29, null, null },
                    { new Guid("40000000-0000-0000-0000-000000000002"), "Empresa", false, null, null, false, true, true, "Designación de Recursos Preventivos", "Vigente hasta modificación — vencimiento manual.", null, 30, null, null },
                    { new Guid("40000000-0000-0000-0000-000000000003"), "Empresa", false, null, null, false, true, true, "Procedimiento de Coordinación de Actividades Empresariales", "Vigente hasta revisión — vencimiento manual.", null, 31, null, null },
                    { new Guid("40000000-0000-0000-0000-000000000004"), "Empresa", false, null, null, false, false, true, "Política Preventiva", "Vigente hasta revisión — vencimiento manual.", null, 32, null, null },
                    { new Guid("40000000-0000-0000-0000-000000000005"), "Empresa", false, null, null, false, false, true, "Organigrama Preventivo", "Vigente hasta cambios — vencimiento manual.", null, 33, null, null },
                    { new Guid("40000000-0000-0000-0000-000000000006"), "Empresa", false, null, null, false, false, true, "Modalidad Preventiva", "Vigente hasta cambios — vencimiento manual.", null, 34, null, null },
                    { new Guid("40000000-0000-0000-0000-000000000007"), "Empresa", false, null, null, false, false, true, "Escritura de Constitución", "Documento permanente — algunos clientes lo piden, no todos.", null, 35, null, null },
                    { new Guid("40000000-0000-0000-0000-000000000008"), "Empresa", false, null, null, false, false, true, "Poder del Representante Legal", "Vigente hasta modificación — vencimiento manual.", null, 36, null, null },
                    { new Guid("40000000-0000-0000-0000-000000000009"), "Empresa", false, null, null, false, false, true, "ISO 45001", "Certificación opcional — vigencia según auditoría del organismo certificador.", null, 37, null, null },
                    { new Guid("4000000a-0000-0000-0000-00000000000a"), "Empresa", false, null, null, false, false, true, "ISO 9001", "Certificación opcional — vigencia según auditoría del organismo certificador.", null, 38, null, null },
                    { new Guid("4000000b-0000-0000-0000-00000000000b"), "Empresa", false, null, null, false, false, true, "ISO 14001", "Certificación opcional — vigencia según auditoría del organismo certificador.", null, 39, null, null },
                    { new Guid("4000000c-0000-0000-0000-00000000000c"), "Empresa", false, null, null, false, false, true, "Declaración Responsable CAE", "Vigencia según lo que exija cada cliente — vencimiento manual.", null, 40, null, null },
                    { new Guid("4000000d-0000-0000-0000-00000000000d"), "Empresa", false, null, null, false, false, true, "Relación de Maquinaria", "Listado actualizable de la maquinaria de la empresa.", null, 41, null, null },
                    { new Guid("4000000e-0000-0000-0000-00000000000e"), "Empresa", false, null, null, false, false, true, "VAT europeo", "Solo aplica a empresas extranjeras de la UE.", null, 42, null, null },
                    { new Guid("4000000f-0000-0000-0000-00000000000f"), "Empresa", false, null, null, false, false, true, "Documento acreditativo de empresa extranjera", "Solo aplica a empresas extranjeras.", null, 43, null, null },
                    { new Guid("40000010-0000-0000-0000-000000000010"), "Empresa", false, null, null, false, false, true, "Traducción jurada", "Solo si el cliente la solicita explícitamente para documentación de una empresa extranjera.", null, 44, null, null },
                    { new Guid("40000011-0000-0000-0000-000000000011"), "Empresa", false, null, null, false, false, true, "Comunicación de desplazamiento", "Solo aplica cuando hay un desplazamiento temporal de trabajadores desde otro país de la UE.", null, 45, null, null },
                    { new Guid("50000000-0000-0000-0000-000000000001"), "Trabajador", false, null, null, false, false, true, "Contrato de Trabajo", "Vigente mientras dure la relación laboral — sin fecha de caducidad propia.", null, 16, null, null },
                    { new Guid("50000000-0000-0000-0000-000000000002"), "Trabajador", false, null, null, false, false, true, "Alta en Seguridad Social", "Vigente mientras continúe contratado — sin fecha de caducidad propia.", null, 17, null, null },
                    { new Guid("50000000-0000-0000-0000-000000000003"), "Trabajador", false, null, null, false, false, true, "Formación Riesgos Específicos", "Vigente hasta cambio de puesto o de riesgos — vencimiento manual.", null, 18, null, null },
                    { new Guid("50000000-0000-0000-0000-000000000004"), "Trabajador", false, null, null, false, false, true, "Formación EPIs", "Distinto de \"EPIS (firma)\" (la entrega/firma de recepción) — esta es la formación de uso.", null, 19, null, null },
                    { new Guid("50000000-0000-0000-0000-000000000005"), "Trabajador", false, null, null, false, false, true, "Permiso de conducir", "Vigencia según DGT, muy variable — vencimiento manual.", null, 20, null, null },
                    { new Guid("50000000-0000-0000-0000-000000000006"), "Trabajador", true, null, null, false, false, true, "Riesgo Eléctrico", "Renovación cada 3 años, criterio habitual del sector.", null, 21, null, 36 },
                    { new Guid("50000000-0000-0000-0000-000000000007"), "Trabajador", false, null, null, false, false, true, "Manipulación Manual de Cargas", "Vigencia según política de cada empresa — vencimiento manual.", null, 22, null, null },
                    { new Guid("50000000-0000-0000-0000-000000000008"), "Trabajador", false, null, null, false, false, true, "Manipulación de Productos Químicos", "Vigencia según la actividad — vencimiento manual.", null, 23, null, null },
                    { new Guid("50000000-0000-0000-0000-000000000009"), "Trabajador", true, null, null, false, false, true, "ADR", "Renovación cada 5 años (transporte de mercancías peligrosas).", null, 24, null, 60 },
                    { new Guid("5000000a-0000-0000-0000-00000000000a"), "Trabajador", false, null, null, false, false, true, "Soldadura", "Vigencia según política de cada empresa — vencimiento manual.", null, 25, null, null },
                    { new Guid("5000000b-0000-0000-0000-00000000000b"), "Trabajador", false, null, null, false, false, true, "Operador de Puente Grúa", "Vigencia según política de cada empresa — vencimiento manual.", null, 26, null, null },
                    { new Guid("5000000c-0000-0000-0000-00000000000c"), "Trabajador", false, null, null, false, false, true, "Operador de Grúa Torre", "Vigencia según normativa aplicable — vencimiento manual.", null, 27, null, null },
                    { new Guid("5000000d-0000-0000-0000-00000000000d"), "Trabajador", false, null, null, false, false, true, "Operador de Grúa Móvil", "Vigencia según normativa aplicable — vencimiento manual.", null, 28, null, null },
                    { new Guid("5000000e-0000-0000-0000-00000000000e"), "Trabajador", false, null, null, false, false, true, "Operador de Dumper", "Vigencia según política de cada empresa — vencimiento manual.", null, 29, null, null },
                    { new Guid("5000000f-0000-0000-0000-00000000000f"), "Trabajador", false, null, null, false, false, true, "Operador de Retroexcavadora", "Vigencia según política de cada empresa — vencimiento manual.", null, 30, null, null },
                    { new Guid("50000010-0000-0000-0000-000000000010"), "Trabajador", false, null, null, false, false, true, "Operador de Minicargadora", "Vigencia según política de cada empresa — vencimiento manual.", null, 31, null, null },
                    { new Guid("50000011-0000-0000-0000-000000000011"), "Trabajador", false, null, null, false, false, true, "Operador de Manipulador Telescópico", "Vigencia según política de cada empresa — vencimiento manual.", null, 32, null, null },
                    { new Guid("50000012-0000-0000-0000-000000000012"), "Trabajador", false, null, null, false, false, true, "Permiso de residencia", "Solo aplica a trabajadores extranjeros de fuera de la UE — vencimiento manual.", null, 33, null, null },
                    { new Guid("50000013-0000-0000-0000-000000000013"), "Trabajador", false, null, null, false, false, true, "Permiso de trabajo", "Solo aplica a trabajadores extranjeros de fuera de la UE — vencimiento manual.", null, 34, null, null },
                    { new Guid("50000014-0000-0000-0000-000000000014"), "Trabajador", false, null, null, false, false, true, "Certificado de Registro de Ciudadano de la UE", "Solo aplica a trabajadores extranjeros de la UE — vencimiento manual.", null, 35, null, null },
                    { new Guid("50000015-0000-0000-0000-000000000015"), "Trabajador", false, null, null, false, false, true, "Certificado A1 de Seguridad Social", "Trabajadores desplazados temporalmente desde otro país de la UE — vigencia ligada a la duración del desplazamiento.", null, 36, null, null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000009"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000a-0000-0000-0000-00000000000a"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000b-0000-0000-0000-00000000000b"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000c-0000-0000-0000-00000000000c"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000d-0000-0000-0000-00000000000d"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000e-0000-0000-0000-00000000000e"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000f-0000-0000-0000-00000000000f"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000010-0000-0000-0000-000000000010"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000011-0000-0000-0000-000000000011"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000009"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000a-0000-0000-0000-00000000000a"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000b-0000-0000-0000-00000000000b"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000c-0000-0000-0000-00000000000c"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000d-0000-0000-0000-00000000000d"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000e-0000-0000-0000-00000000000e"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000f-0000-0000-0000-00000000000f"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000010-0000-0000-0000-000000000010"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000011-0000-0000-0000-000000000011"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000012-0000-0000-0000-000000000012"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000013-0000-0000-0000-000000000013"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000014-0000-0000-0000-000000000014"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000015-0000-0000-0000-000000000015"));
        }
    }
}
