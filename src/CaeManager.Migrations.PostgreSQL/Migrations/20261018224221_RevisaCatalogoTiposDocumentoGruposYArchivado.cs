using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RevisaCatalogoTiposDocumentoGruposYArchivado : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Primero, antes de tocar nada: sin un migrador que vea todos los Tenants, el UpdateData del Tenant #1 y el
            // SQL de las copias no verían filas bajo RLS.
            migrationBuilder.Sql(ExigirMigradorQueVeTodosLosTenants);

            migrationBuilder.AddColumn<bool>(
                name: "Archivado",
                table: "TiposDocumento",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DatosAdicionales",
                table: "TiposDocumento",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Grupo",
                table: "TiposDocumento",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SustitutoId",
                table: "TiposDocumento",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EsRenuncia",
                table: "Documentos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Horas",
                table: "Documentos",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Nivel",
                table: "Documentos",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 8, "VigilanciaDeLaSalud", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "InformacionYEpi", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000003"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Nombre", "SustitutoId" },
                values: new object[] { false, 1, "FormacionPrl", "Reciclaje PRL", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000004"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Nombre", "SustitutoId" },
                values: new object[] { false, 1, "FormacionPrl", "Formación PRL del puesto", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000005"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Nombre", "Notas", "SustitutoId" },
                values: new object[] { false, 3, "FormacionPrl", "Formación PRL de convenio", "Formación base de convenio (nivel básico de prevención, 60 h en construcción), no consta caducidad. Las horas y el nivel son datos del documento desde 2026-10-17.", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000006"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Nombre", "Notas", "SustitutoId" },
                values: new object[] { false, 1, "FormacionPrl", "Formación PRL de convenio por oficio", "Formación por puesto de trabajo u oficio del convenio (20 h en construcción, art. 141 del VII CGSC). Documento distinto de la formación de nivel básico: no la sustituye. No consta caducidad.", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000007"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Nombre", "Notas", "SustitutoId" },
                values: new object[] { false, 1, "FormacionPrl", "Formación PRL de convenio por oficio, módulo específico", "Módulo o curso específico de oficio del convenio (6 h en construcción, art. 143 del VII CGSC). Documento distinto de la formación de nivel básico y de la de oficio completa. No consta caducidad.", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000008"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Nombre", "SustitutoId" },
                values: new object[] { false, 0, "InformacionYEpi", "Información de riesgos del puesto", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000009"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 1, "HabilitacionesYOficios", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000a"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "HabilitacionesYOficios", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000b"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Nombre", "SustitutoId" },
                values: new object[] { false, 1, "HabilitacionesYOficios", "Bloqueo y etiquetado (LOTO)", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000c"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "FormacionPrl", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000d"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 1, "FormacionPrl", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000e"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 1, "HabilitacionesYOficios", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000f"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Nombre", "SustitutoId" },
                values: new object[] { false, 1, "HabilitacionesYOficios", "Trabajos en altura", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "SeguridadSocialYHacienda", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000002"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "SeguridadSocialYHacienda", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000003"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Nombre", "SustitutoId" },
                values: new object[] { false, 0, "SeguridadSocialYHacienda", "Informe de trabajadores en alta", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000004"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Nombre", "Notas", "SustitutoId" },
                values: new object[] { false, 4, "SeguridadSocialYHacienda", "RLC (TC1)", "Admite el recibo de pago como adjunto opcional del mismo documento (2026-10-17). Mensual — el documento de un periodo (p. ej. 01/05) vence 3 meses después (01/08), porque tarda en emitirse con la fecha del periodo ya pasada.", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000005"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Notas", "Requerido", "SustitutoId" },
                values: new object[] { true, 0, "SeguridadSocialYHacienda", "Archivado el 2026-10-17: el recibo es adjunto opcional de «RLC (TC1)», que lo sustituye.", "No", new Guid("20000000-0000-0000-0000-000000000004") });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000006"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Notas", "Requerido", "SustitutoId" },
                values: new object[] { true, 0, "SeguridadSocialYHacienda", "Archivado el 2026-10-17: la variante combinada es «RLC (TC1)» con el recibo como adjunto opcional, que lo sustituye.", "No", new Guid("20000000-0000-0000-0000-000000000004") });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000007"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "SeguridadSocialYHacienda", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000008"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Nombre", "SustitutoId" },
                values: new object[] { false, 0, "SegurosYMutua", "Mutua de Accidentes", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000009"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Nombre", "Notas", "SustitutoId" },
                values: new object[] { false, 4, "SegurosYMutua", "Seguro de Responsabilidad Civil", "Admite el recibo de pago como adjunto opcional del mismo documento (2026-10-17). Vigencia sin especificar — fecha de vencimiento manual.", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000a-0000-0000-0000-00000000000a"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "OrganizacionPreventiva", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000b-0000-0000-0000-00000000000b"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "OrganizacionPreventiva", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000c-0000-0000-0000-00000000000c"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "OrganizacionPreventiva", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000d-0000-0000-0000-00000000000d"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "SocietariaYAdministrativa", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000001"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, null, null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000002"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, null, null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000003"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, null, null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000004"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, null, null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000001"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "OrganizacionPreventiva", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000002"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "OrganizacionPreventiva", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000003"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Notas", "Requerido", "SustitutoId" },
                values: new object[] { false, 0, "CoordinacionCae", "Vigente hasta revisión — vencimiento manual. Deja de ser obligatorio el 2026-10-17 (decisión del propietario).", "No", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000004"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "OrganizacionPreventiva", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000005"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "OrganizacionPreventiva", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000006"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "OrganizacionPreventiva", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000007"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "SocietariaYAdministrativa", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000008"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "SocietariaYAdministrativa", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000009"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "Certificaciones", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000a-0000-0000-0000-00000000000a"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "Certificaciones", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000b-0000-0000-0000-00000000000b"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "Certificaciones", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000c-0000-0000-0000-00000000000c"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "CoordinacionCae", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000d-0000-0000-0000-00000000000d"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "SocietariaYAdministrativa", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000e-0000-0000-0000-00000000000e"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "EmpresaExtranjera", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000f-0000-0000-0000-00000000000f"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "EmpresaExtranjera", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000010-0000-0000-0000-000000000010"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "EmpresaExtranjera", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000011-0000-0000-0000-000000000011"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "EmpresaExtranjera", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000001"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "RelacionLaboral", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000002"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "RelacionLaboral", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000003"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 1, "FormacionPrl", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000004"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Notas", "SustitutoId" },
                values: new object[] { true, 0, "FormacionPrl", "Archivado el 2026-10-17 (retirado del catálogo base): lo sustituye «Entrega de EPI».", new Guid("10000000-0000-0000-0000-000000000002") });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000005"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "HabilitacionesYOficios", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000006"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "HabilitacionesYOficios", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000007"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "HabilitacionesYOficios", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000008"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "HabilitacionesYOficios", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000009"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "HabilitacionesYOficios", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000a-0000-0000-0000-00000000000a"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "HabilitacionesYOficios", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000b-0000-0000-0000-00000000000b"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "HabilitacionesYOficios", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000c-0000-0000-0000-00000000000c"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "HabilitacionesYOficios", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000d-0000-0000-0000-00000000000d"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "HabilitacionesYOficios", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000e-0000-0000-0000-00000000000e"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "HabilitacionesYOficios", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000f-0000-0000-0000-00000000000f"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "HabilitacionesYOficios", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000010-0000-0000-0000-000000000010"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "HabilitacionesYOficios", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000011-0000-0000-0000-000000000011"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "HabilitacionesYOficios", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000012-0000-0000-0000-000000000012"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "IdentidadYExtranjeria", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000013-0000-0000-0000-000000000013"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "IdentidadYExtranjeria", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000014-0000-0000-0000-000000000014"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "IdentidadYExtranjeria", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000015-0000-0000-0000-000000000015"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Nombre", "SustitutoId" },
                values: new object[] { false, 0, "IdentidadYExtranjeria", "Certificado A1", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000016-0000-0000-0000-000000000016"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "IdentidadYExtranjeria", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000001"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Nombre", "SustitutoId" },
                values: new object[] { false, 0, "HabilitacionesYOficios", "Autorización de uso de equipo de trabajo/maquinaria", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000002"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "OrganizacionPreventiva", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000003"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "CoordinacionCae", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000004"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "CoordinacionCae", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000005"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Notas", "SustitutoId" },
                values: new object[] { true, 0, "Igualdad", "F-93 del catálogo de formatos PRL — art. 48 LO 3/2007. Obligatorio para todas las empresas, sin umbral de plantilla. Vigente hasta revisión — vencimiento manual. Archivado el 2026-10-17 (retirado del catálogo base, sin sustituto).", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000006"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "Igualdad", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000001"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "CoordinacionCae", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000002"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "CoordinacionCae", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000003"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "CoordinacionCae", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000004"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "InformacionYEpi", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000001"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, "Construccion", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000002"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "SustitutoId" },
                values: new object[] { false, 0, null, null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000003"),
                columns: new[] { "Archivado", "DatosAdicionales", "Grupo", "Nombre", "SustitutoId" },
                values: new object[] { false, 0, "SeguridadSocialYHacienda", "Recibo de cuota de autónomos", null });

            migrationBuilder.InsertData(
                table: "TiposDocumento",
                columns: new[] { "Id", "AmbitoAplicacion", "AplicaVencimientoAutomatico", "Archivado", "CriteriosValidacion", "DatosAdicionales", "Descripcion", "DeteccionTrabajadoresActiva", "Grupo", "LecturaIaActiva", "Naturaleza", "Nombre", "Notas", "Observaciones", "Orden", "PerfilDocumentoOficial", "Requerido", "SeSolicitaA", "Sensibilidad", "SustitutoId", "TenantId", "TiposContratacionDeSerie", "VerificacionIaActiva", "VigenciaMeses" },
                values: new object[,]
                {
                    { new Guid("80000000-0000-0000-0000-000000000004"), "Empresa", false, false, null, 0, null, false, "SeguridadSocialYHacienda", true, "RequisitoCliente", "Alta en el RETA", "Resolución de alta del autónomo en el Régimen Especial de Trabajadores Autónomos: prueba la afiliación, mientras que el recibo de cuota prueba el pago. El autónomo es su propia Empresa. Sin caducidad propia — vencimiento manual.", null, 56, "Ninguno", "No", null, "DatosPersonales", null, new Guid("00000000-0000-0000-0000-000000000001"), 7, false, null },
                    { new Guid("80000000-0000-0000-0000-000000000005"), "Empresa", false, false, null, 0, null, false, "OrganizacionPreventiva", true, "RequisitoCliente", "Plan de emergencia y evacuación", "Plan de emergencia y evacuación de la Empresa. Opcional y nunca obligatorio por defecto: el art. 10.2 RD 171/2004 no lo exige al contratista. Vigente hasta revisión — vencimiento manual.", null, 57, "Ninguno", "No", null, "DatosPersonales", null, new Guid("00000000-0000-0000-0000-000000000001"), 7, false, null },
                    { new Guid("80000000-0000-0000-0000-000000000006"), "Empresa", false, false, null, 0, null, false, "CoordinacionCae", true, "RequisitoCliente", "Documento requerido por el Cliente empresarial", "Tipo genérico para los formularios propios que pide un Cliente empresarial concreto. Vigencia según cada Cliente empresarial — vencimiento manual.", null, 58, "Ninguno", "No", null, "CategoriaEspecialSalud", null, new Guid("00000000-0000-0000-0000-000000000001"), 7, false, null }
                });

            migrationBuilder.Sql(InsertarAliasNuevosTenantBase);

            migrationBuilder.CreateIndex(
                name: "IX_TiposDocumento_TenantId_SustitutoId",
                table: "TiposDocumento",
                columns: new[] { "TenantId", "SustitutoId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Documentos_HorasEnRango",
                table: "Documentos",
                sql: "\"Horas\" IS NULL OR (\"Horas\" >= 1 AND \"Horas\" <= 999)");

            migrationBuilder.AddForeignKey(
                name: "FK_TiposDocumento_TiposDocumento_TenantId_SustitutoId",
                table: "TiposDocumento",
                columns: new[] { "TenantId", "SustitutoId" },
                principalTable: "TiposDocumento",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(ActualizarCopias);
            migrationBuilder.Sql(ArchivarCopias);
            migrationBuilder.Sql(InsertarTiposNuevosEnCopias);
            migrationBuilder.Sql(InsertarAliasEnCopias);
            migrationBuilder.Sql(PasarConfiguracionAlSustituto);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ExigirMigradorQueVeTodosLosTenants);
            // PasarConfiguracionAlSustituto no se deshace, a propósito (mismo criterio que
            // ConvierteCarterasPorClienteATenantEntero): las filas que pasó al sustituto apuntan a tipos que ya existían
            // antes de esta migración, así que el estado que deja es válido sin ella; restaurarlo exactamente exigiría
            // una tabla de respaldo, y una tabla nueva sin RLS sería legible entre Tenants por los privilegios por
            // defecto de cae_app_runtime y cae_app_soporte. Consecuencias que se aceptan con ello: la fila de Centro del
            // archivado se queda como exclusión explícita (Incluido=false) aunque el tipo vuelva a pedirse de serie; sus
            // requisitos de Proyecto y de plantilla de evento siguen en el sustituto; el Requerido de «Formación EPIs» y
            // del protocolo de acoso en las copias no se restaura (no se guardó el anterior), y «Procedimiento de
            // Coordinación…» vuelve a Condicional en toda copia que esté en No. Los Documentos con EsRenuncia pierden la
            // marca al borrar la columna.
            migrationBuilder.Sql(RevertirCopias);

            migrationBuilder.DropForeignKey(
                name: "FK_TiposDocumento_TiposDocumento_TenantId_SustitutoId",
                table: "TiposDocumento");

            migrationBuilder.DropIndex(
                name: "IX_TiposDocumento_TenantId_SustitutoId",
                table: "TiposDocumento");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Documentos_HorasEnRango",
                table: "Documentos");

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000009"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000010"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000011"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000012"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000013"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000014"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000015"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000016"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000017"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000018"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000019"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000020"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000021"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000022"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000023"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000024"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000025"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000026"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000027"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000028"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000029"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000030"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000031"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000032"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000033"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000034"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000035"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000036"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000037"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000038"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000039"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000040"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000041"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000042"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000043"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000044"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000045"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000046"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000047"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000048"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000049"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000050"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000051"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000052"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000053"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000054"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000055"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000056"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000057"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000058"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000059"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000060"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000061"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000062"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000063"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000064"));

            migrationBuilder.DeleteData(
                table: "TiposDocumentoAlias",
                keyColumn: "Id",
                keyValue: new Guid("90000001-0000-0000-0000-000000000065"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000005"));

            migrationBuilder.DropColumn(
                name: "Archivado",
                table: "TiposDocumento");

            migrationBuilder.DropColumn(
                name: "DatosAdicionales",
                table: "TiposDocumento");

            migrationBuilder.DropColumn(
                name: "Grupo",
                table: "TiposDocumento");

            migrationBuilder.DropColumn(
                name: "SustitutoId",
                table: "TiposDocumento");

            migrationBuilder.DropColumn(
                name: "EsRenuncia",
                table: "Documentos");

            migrationBuilder.DropColumn(
                name: "Horas",
                table: "Documentos");

            migrationBuilder.DropColumn(
                name: "Nivel",
                table: "Documentos");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000003"),
                column: "Nombre",
                value: "Reciclaje 4h");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000004"),
                column: "Nombre",
                value: "Formación Art. 19");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000005"),
                columns: new[] { "Nombre", "Notas" },
                values: new object[] { "Formación 60h (base convenio)", "Formación base, no consta caducidad." });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000006"),
                columns: new[] { "Nombre", "Notas" },
                values: new object[] { "Formación 20h", "Mismo curso de convenio que 60h/6h, no consta caducidad." });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000007"),
                columns: new[] { "Nombre", "Notas" },
                values: new object[] { "Formación 6h", "Mismo curso de convenio que 60h/20h, no consta caducidad." });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000008"),
                column: "Nombre",
                value: "Información Art. 18");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000b"),
                column: "Nombre",
                value: "LOTO (4h)");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000f"),
                column: "Nombre",
                value: "Trabajos en altura (8h)");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000003"),
                column: "Nombre",
                value: "ITA");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000004"),
                columns: new[] { "Nombre", "Notas" },
                values: new object[] { "RLC", "Mensual — el documento de un periodo (p. ej. 01/05) vence 3 meses después (01/08), porque tarda en emitirse con la fecha del periodo ya pasada." });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000005"),
                columns: new[] { "Notas", "Requerido" },
                values: new object[] { "Mismo criterio de vigencia que el RLC.", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000006"),
                columns: new[] { "Notas", "Requerido" },
                values: new object[] { "Variante combinada — mismo criterio de vigencia que el RLC.", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000008"),
                column: "Nombre",
                value: "Mutua");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000009"),
                columns: new[] { "Nombre", "Notas" },
                values: new object[] { "Seguro de Responsabilidad Civil + recibo de pago", "Vigencia sin especificar — fecha de vencimiento manual." });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000003"),
                columns: new[] { "Notas", "Requerido" },
                values: new object[] { "Vigente hasta revisión — vencimiento manual.", "Condicional" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000004"),
                column: "Notas",
                value: "Distinto de \"Entrega de EPI\" (antes \"EPIS (firma)\"; la entrega/firma de recepción) — esta es la formación de uso.");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000015-0000-0000-0000-000000000015"),
                column: "Nombre",
                value: "Certificado A1 de Seguridad Social");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000001"),
                column: "Nombre",
                value: "Autorización de uso de equipo de trabajo");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000005"),
                column: "Notas",
                value: "F-93 del catálogo de formatos PRL — art. 48 LO 3/2007. Obligatorio para todas las empresas, sin umbral de plantilla. Vigente hasta revisión — vencimiento manual.");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000003"),
                column: "Nombre",
                value: "Recibo de cuota de autónomos (RETA)");
        }

        // ---------------------------------------------------------------------------------------------------------
        // SQL escrito a mano (revisión del catálogo del 2026-10-17). El HasData solo cubre el catálogo base del
        // Tenant #1; las copias del resto de Tenants (CrearCopiasParaTenant) tienen Id propios, así que se localizan
        // por (Tenant, ámbito, nombre base anterior). Un Tenant que renombró su copia no se toca. Los Id de lo que
        // se inserta en las copias son deterministas (md5 del Tenant, o de la copia, y del dato base): así el Down
        // borra exactamente lo que insertó el Up y nunca un tipo o alias que el Tenant ya tuviera con el mismo texto.
        // ---------------------------------------------------------------------------------------------------------
        /// <summary>
        /// Las copias son filas de otros Tenants bajo FORCE ROW LEVEL SECURITY: sin superusuario ni BYPASSRLS, los UPDATE
        /// no verían ninguna fila y la migración «pasaría» sin hacer nada. Se exige la identidad que ya exige la línea
        /// base (migrador superusuario o con BYPASSRLS) y, si no la hay, se aborta en vez de relajar RLS.
        /// </summary>
        private const string ExigirMigradorQueVeTodosLosTenants = """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'RevisaCatalogoTiposDocumentoGruposYArchivado necesita un migrador superusuario o con BYPASSRLS para actualizar las copias del catálogo de cada Tenant';
                END IF;
            END
            $$;
            """;
        /// <summary>Alias nuevos del Tenant #1 (bloque 90000001-…): no chocan con un alias igual añadido a mano.</summary>
        private const string InsertarAliasNuevosTenantBase = """
            INSERT INTO "TiposDocumentoAlias" ("Id", "TenantId", "Texto", "TipoDocumentoId")
            SELECT a.id::uuid, '00000000-0000-0000-0000-000000000001'::uuid, a.texto, a.tipo::uuid
            FROM (VALUES
                ('90000001-0000-0000-0000-000000000001', 'Reconocimiento médico', '10000000-0000-0000-0000-000000000001'),
                ('90000001-0000-0000-0000-000000000002', 'Apto', '10000000-0000-0000-0000-000000000001'),
                ('90000001-0000-0000-0000-000000000003', 'EPI', '10000000-0000-0000-0000-000000000002'),
                ('90000001-0000-0000-0000-000000000004', 'Reciclaje 4h', '10000000-0000-0000-0000-000000000003'),
                ('90000001-0000-0000-0000-000000000005', 'Formación Art. 19', '10000000-0000-0000-0000-000000000004'),
                ('90000001-0000-0000-0000-000000000006', 'Formación en prevención', '10000000-0000-0000-0000-000000000004'),
                ('90000001-0000-0000-0000-000000000007', 'Art. 19', '10000000-0000-0000-0000-000000000004'),
                ('90000001-0000-0000-0000-000000000008', 'Formación 60h (base convenio)', '10000000-0000-0000-0000-000000000005'),
                ('90000001-0000-0000-0000-000000000009', 'Formación 60h', '10000000-0000-0000-0000-000000000005'),
                ('90000001-0000-0000-0000-000000000010', 'Recurso preventivo 60h', '10000000-0000-0000-0000-000000000005'),
                ('90000001-0000-0000-0000-000000000011', 'Nivel básico PRL 60h', '10000000-0000-0000-0000-000000000005'),
                ('90000001-0000-0000-0000-000000000012', 'Formación 20h', '10000000-0000-0000-0000-000000000006'),
                ('90000001-0000-0000-0000-000000000013', 'Formación 6h', '10000000-0000-0000-0000-000000000007'),
                ('90000001-0000-0000-0000-000000000014', 'Información Art. 18', '10000000-0000-0000-0000-000000000008'),
                ('90000001-0000-0000-0000-000000000015', 'Registro de información PRL', '10000000-0000-0000-0000-000000000008'),
                ('90000001-0000-0000-0000-000000000016', 'Art. 18', '10000000-0000-0000-0000-000000000008'),
                ('90000001-0000-0000-0000-000000000017', 'Plataforma elevadora', '10000000-0000-0000-0000-00000000000a'),
                ('90000001-0000-0000-0000-000000000018', 'Plataforma elevadora móvil', '10000000-0000-0000-0000-00000000000a'),
                ('90000001-0000-0000-0000-000000000019', 'LOTO (4h)', '10000000-0000-0000-0000-00000000000b'),
                ('90000001-0000-0000-0000-000000000020', 'Trabajos en altura (8h)', '10000000-0000-0000-0000-00000000000f'),
                ('90000001-0000-0000-0000-000000000021', 'Altura', '10000000-0000-0000-0000-00000000000f'),
                ('90000001-0000-0000-0000-000000000022', 'Formación en altura', '10000000-0000-0000-0000-00000000000f'),
                ('90000001-0000-0000-0000-000000000023', 'TA.2', '50000000-0000-0000-0000-000000000002'),
                ('90000001-0000-0000-0000-000000000024', 'TA2', '50000000-0000-0000-0000-000000000002'),
                ('90000001-0000-0000-0000-000000000025', 'IDC', '50000000-0000-0000-0000-000000000002'),
                ('90000001-0000-0000-0000-000000000026', 'Alta TA2', '50000000-0000-0000-0000-000000000002'),
                ('90000001-0000-0000-0000-000000000027', 'Informe de datos de cotización', '50000000-0000-0000-0000-000000000002'),
                ('90000001-0000-0000-0000-000000000028', 'Carne de conducir', '50000000-0000-0000-0000-000000000005'),
                ('90000001-0000-0000-0000-000000000029', 'Certificado A1 de Seguridad Social', '50000015-0000-0000-0000-000000000015'),
                ('90000001-0000-0000-0000-000000000030', 'A1', '50000015-0000-0000-0000-000000000015'),
                ('90000001-0000-0000-0000-000000000031', 'Formulario A1', '50000015-0000-0000-0000-000000000015'),
                ('90000001-0000-0000-0000-000000000032', 'DNI/NIE', '50000016-0000-0000-0000-000000000016'),
                ('90000001-0000-0000-0000-000000000033', 'DNI', '50000016-0000-0000-0000-000000000016'),
                ('90000001-0000-0000-0000-000000000034', 'NIE', '50000016-0000-0000-0000-000000000016'),
                ('90000001-0000-0000-0000-000000000035', 'Autorización de uso de equipo de trabajo', '60000000-0000-0000-0000-000000000001'),
                ('90000001-0000-0000-0000-000000000036', 'Certificado SS', '20000000-0000-0000-0000-000000000001'),
                ('90000001-0000-0000-0000-000000000037', 'Certificado TGSS', '20000000-0000-0000-0000-000000000001'),
                ('90000001-0000-0000-0000-000000000038', 'Corriente SS', '20000000-0000-0000-0000-000000000001'),
                ('90000001-0000-0000-0000-000000000039', 'Certificado de no deuda SS', '20000000-0000-0000-0000-000000000001'),
                ('90000001-0000-0000-0000-000000000040', 'Certificado AEAT', '20000000-0000-0000-0000-000000000002'),
                ('90000001-0000-0000-0000-000000000041', 'AEAT', '20000000-0000-0000-0000-000000000002'),
                ('90000001-0000-0000-0000-000000000042', 'Corriente Hacienda', '20000000-0000-0000-0000-000000000002'),
                ('90000001-0000-0000-0000-000000000043', 'Certificado de contratistas', '20000000-0000-0000-0000-000000000002'),
                ('90000001-0000-0000-0000-000000000044', 'ITA', '20000000-0000-0000-0000-000000000003'),
                ('90000001-0000-0000-0000-000000000045', 'Trabajadores en alta (CCC)', '20000000-0000-0000-0000-000000000003'),
                ('90000001-0000-0000-0000-000000000046', 'RLC', '20000000-0000-0000-0000-000000000004'),
                ('90000001-0000-0000-0000-000000000047', 'Recibo de pago RLC/TC1', '20000000-0000-0000-0000-000000000004'),
                ('90000001-0000-0000-0000-000000000048', 'RLC/TC1 + Recibo de pago', '20000000-0000-0000-0000-000000000004'),
                ('90000001-0000-0000-0000-000000000049', 'Mutua', '20000000-0000-0000-0000-000000000008'),
                ('90000001-0000-0000-0000-000000000050', 'Certificado de mutua', '20000000-0000-0000-0000-000000000008'),
                ('90000001-0000-0000-0000-000000000051', 'Mutua AT/EP', '20000000-0000-0000-0000-000000000008'),
                ('90000001-0000-0000-0000-000000000052', 'Seguro de Responsabilidad Civil + recibo de pago', '20000000-0000-0000-0000-000000000009'),
                ('90000001-0000-0000-0000-000000000053', 'Seguro RC + recibo', '20000000-0000-0000-0000-000000000009'),
                ('90000001-0000-0000-0000-000000000054', 'Seguro RC', '20000000-0000-0000-0000-000000000009'),
                ('90000001-0000-0000-0000-000000000055', 'Certificado SPA', '40000000-0000-0000-0000-000000000006'),
                ('90000001-0000-0000-0000-000000000056', 'REA', '80000000-0000-0000-0000-000000000001'),
                ('90000001-0000-0000-0000-000000000057', 'Recibo de cuota de autónomos (RETA)', '80000000-0000-0000-0000-000000000003'),
                ('90000001-0000-0000-0000-000000000058', 'RETA', '80000000-0000-0000-0000-000000000003'),
                ('90000001-0000-0000-0000-000000000059', 'Alta de autónomo', '80000000-0000-0000-0000-000000000004'),
                ('90000001-0000-0000-0000-000000000060', 'Alta RETA', '80000000-0000-0000-0000-000000000004'),
                ('90000001-0000-0000-0000-000000000061', 'Plan de emergencia', '80000000-0000-0000-0000-000000000005'),
                ('90000001-0000-0000-0000-000000000062', 'Plan de evacuación', '80000000-0000-0000-0000-000000000005'),
                ('90000001-0000-0000-0000-000000000063', 'ITV', '30000000-0000-0000-0000-000000000001'),
                ('90000001-0000-0000-0000-000000000064', 'Seguro del vehículo', '30000000-0000-0000-0000-000000000003'),
                ('90000001-0000-0000-0000-000000000065', 'Póliza del vehículo', '30000000-0000-0000-0000-000000000003')
            ) AS a(id, texto, tipo)
            WHERE EXISTS (SELECT 1 FROM "TiposDocumento" t WHERE t."Id" = a.tipo::uuid)
            ON CONFLICT DO NOTHING;
            """;

        /// <summary>
        /// Copias: renombrado, grupo y datos adicionales. Si el nombre nuevo ya existe en el Tenant (índice único
        /// por Tenant y nombre), la copia conserva su nombre y recibe igualmente grupo y datos adicionales.
        /// </summary>
        private const string ActualizarCopias = """
            UPDATE "TiposDocumento" t
            SET "Nombre" = CASE
                    WHEN m.anterior <> m.nuevo AND NOT EXISTS (
                        SELECT 1 FROM "TiposDocumento" o WHERE o."TenantId" = t."TenantId" AND o."Nombre" = m.nuevo)
                    THEN m.nuevo ELSE t."Nombre" END,
                "Grupo" = m.grupo,
                "DatosAdicionales" = m.datos
            FROM (VALUES
                ('Trabajador', 'Certificado de aptitud médica', 'Certificado de aptitud médica', 'VigilanciaDeLaSalud', 8),
                ('Trabajador', 'Entrega de EPI', 'Entrega de EPI', 'InformacionYEpi', 0),
                ('Trabajador', 'Reciclaje 4h', 'Reciclaje PRL', 'FormacionPrl', 1),
                ('Trabajador', 'Formación Art. 19', 'Formación PRL del puesto', 'FormacionPrl', 1),
                ('Trabajador', 'Formación 60h (base convenio)', 'Formación PRL de convenio', 'FormacionPrl', 3),
                ('Trabajador', 'Formación 20h', 'Formación PRL de convenio por oficio', 'FormacionPrl', 1),
                ('Trabajador', 'Formación 6h', 'Formación PRL de convenio por oficio, módulo específico', 'FormacionPrl', 1),
                ('Trabajador', 'Información Art. 18', 'Información de riesgos del puesto', 'InformacionYEpi', 0),
                ('Trabajador', 'Carretillas elevadoras', 'Carretillas elevadoras', 'HabilitacionesYOficios', 1),
                ('Trabajador', 'PEMP (plataformas elevadoras)', 'PEMP (plataformas elevadoras)', 'HabilitacionesYOficios', 0),
                ('Trabajador', 'LOTO (4h)', 'Bloqueo y etiquetado (LOTO)', 'HabilitacionesYOficios', 1),
                ('Trabajador', 'Seguridad alimentaria', 'Seguridad alimentaria', 'FormacionPrl', 0),
                ('Trabajador', 'Primeros auxilios', 'Primeros auxilios', 'FormacionPrl', 1),
                ('Trabajador', 'Espacios confinados', 'Espacios confinados', 'HabilitacionesYOficios', 1),
                ('Trabajador', 'Trabajos en altura (8h)', 'Trabajos en altura', 'HabilitacionesYOficios', 1),
                ('Empresa', 'Certificado de estar al corriente con la Seguridad Social', 'Certificado de estar al corriente con la Seguridad Social', 'SeguridadSocialYHacienda', 0),
                ('Empresa', 'Certificado de estar al corriente con Hacienda', 'Certificado de estar al corriente con Hacienda', 'SeguridadSocialYHacienda', 0),
                ('Empresa', 'ITA', 'Informe de trabajadores en alta', 'SeguridadSocialYHacienda', 0),
                ('Empresa', 'RLC', 'RLC (TC1)', 'SeguridadSocialYHacienda', 4),
                ('Empresa', 'Recibo de pago RLC/TC1', 'Recibo de pago RLC/TC1', 'SeguridadSocialYHacienda', 0),
                ('Empresa', 'RLC/TC1 + Recibo de pago', 'RLC/TC1 + Recibo de pago', 'SeguridadSocialYHacienda', 0),
                ('Empresa', 'RNT', 'RNT', 'SeguridadSocialYHacienda', 0),
                ('Empresa', 'Mutua', 'Mutua de Accidentes', 'SegurosYMutua', 0),
                ('Empresa', 'Seguro de Responsabilidad Civil + recibo de pago', 'Seguro de Responsabilidad Civil', 'SegurosYMutua', 4),
                ('Empresa', 'Servicio de Prevención Ajeno', 'Servicio de Prevención Ajeno', 'OrganizacionPreventiva', 0),
                ('Empresa', 'Evaluación de Riesgos Laborales', 'Evaluación de Riesgos Laborales', 'OrganizacionPreventiva', 0),
                ('Empresa', 'Planificación de la Actividad Preventiva', 'Planificación de la Actividad Preventiva', 'OrganizacionPreventiva', 0),
                ('Empresa', 'Tarjeta de identificación fiscal', 'Tarjeta de identificación fiscal', 'SocietariaYAdministrativa', 0),
                ('Empresa', 'Plan de Prevención', 'Plan de Prevención', 'OrganizacionPreventiva', 0),
                ('Empresa', 'Designación de Recursos Preventivos', 'Designación de Recursos Preventivos', 'OrganizacionPreventiva', 0),
                ('Empresa', 'Procedimiento de Coordinación de Actividades Empresariales', 'Procedimiento de Coordinación de Actividades Empresariales', 'CoordinacionCae', 0),
                ('Empresa', 'Política Preventiva', 'Política Preventiva', 'OrganizacionPreventiva', 0),
                ('Empresa', 'Organigrama Preventivo', 'Organigrama Preventivo', 'OrganizacionPreventiva', 0),
                ('Empresa', 'Modalidad Preventiva', 'Modalidad Preventiva', 'OrganizacionPreventiva', 0),
                ('Empresa', 'Escritura de Constitución', 'Escritura de Constitución', 'SocietariaYAdministrativa', 0),
                ('Empresa', 'Poder del Representante Legal', 'Poder del Representante Legal', 'SocietariaYAdministrativa', 0),
                ('Empresa', 'ISO 45001', 'ISO 45001', 'Certificaciones', 0),
                ('Empresa', 'ISO 9001', 'ISO 9001', 'Certificaciones', 0),
                ('Empresa', 'ISO 14001', 'ISO 14001', 'Certificaciones', 0),
                ('Empresa', 'Declaración Responsable CAE', 'Declaración Responsable CAE', 'CoordinacionCae', 0),
                ('Empresa', 'Relación de Maquinaria', 'Relación de Maquinaria', 'SocietariaYAdministrativa', 0),
                ('Empresa', 'VAT europeo', 'VAT europeo', 'EmpresaExtranjera', 0),
                ('Empresa', 'Documento acreditativo de empresa extranjera', 'Documento acreditativo de empresa extranjera', 'EmpresaExtranjera', 0),
                ('Empresa', 'Traducción jurada', 'Traducción jurada', 'EmpresaExtranjera', 0),
                ('Empresa', 'Comunicación de desplazamiento', 'Comunicación de desplazamiento', 'EmpresaExtranjera', 0),
                ('Trabajador', 'Contrato de Trabajo', 'Contrato de Trabajo', 'RelacionLaboral', 0),
                ('Trabajador', 'Alta en Seguridad Social', 'Alta en Seguridad Social', 'RelacionLaboral', 0),
                ('Trabajador', 'Formación Riesgos Específicos', 'Formación Riesgos Específicos', 'FormacionPrl', 1),
                ('Trabajador', 'Formación EPIs', 'Formación EPIs', 'FormacionPrl', 0),
                ('Trabajador', 'Permiso de conducir', 'Permiso de conducir', 'HabilitacionesYOficios', 0),
                ('Trabajador', 'Riesgo Eléctrico', 'Riesgo Eléctrico', 'HabilitacionesYOficios', 0),
                ('Trabajador', 'Manipulación Manual de Cargas', 'Manipulación Manual de Cargas', 'HabilitacionesYOficios', 0),
                ('Trabajador', 'Manipulación de Productos Químicos', 'Manipulación de Productos Químicos', 'HabilitacionesYOficios', 0),
                ('Trabajador', 'ADR', 'ADR', 'HabilitacionesYOficios', 0),
                ('Trabajador', 'Soldadura', 'Soldadura', 'HabilitacionesYOficios', 0),
                ('Trabajador', 'Operador de Puente Grúa', 'Operador de Puente Grúa', 'HabilitacionesYOficios', 0),
                ('Trabajador', 'Operador de Grúa Torre', 'Operador de Grúa Torre', 'HabilitacionesYOficios', 0),
                ('Trabajador', 'Operador de Grúa Móvil', 'Operador de Grúa Móvil', 'HabilitacionesYOficios', 0),
                ('Trabajador', 'Operador de Dumper', 'Operador de Dumper', 'HabilitacionesYOficios', 0),
                ('Trabajador', 'Operador de Retroexcavadora', 'Operador de Retroexcavadora', 'HabilitacionesYOficios', 0),
                ('Trabajador', 'Operador de Minicargadora', 'Operador de Minicargadora', 'HabilitacionesYOficios', 0),
                ('Trabajador', 'Operador de Manipulador Telescópico', 'Operador de Manipulador Telescópico', 'HabilitacionesYOficios', 0),
                ('Trabajador', 'Permiso de residencia', 'Permiso de residencia', 'IdentidadYExtranjeria', 0),
                ('Trabajador', 'Permiso de trabajo', 'Permiso de trabajo', 'IdentidadYExtranjeria', 0),
                ('Trabajador', 'Certificado de Registro de Ciudadano de la UE', 'Certificado de Registro de Ciudadano de la UE', 'IdentidadYExtranjeria', 0),
                ('Trabajador', 'Certificado A1 de Seguridad Social', 'Certificado A1', 'IdentidadYExtranjeria', 0),
                ('Trabajador', 'Documento de identidad', 'Documento de identidad', 'IdentidadYExtranjeria', 0),
                ('Trabajador', 'Autorización de uso de equipo de trabajo', 'Autorización de uso de equipo de trabajo/maquinaria', 'HabilitacionesYOficios', 0),
                ('Empresa', 'Acta de presencia del recurso preventivo', 'Acta de presencia del recurso preventivo', 'OrganizacionPreventiva', 0),
                ('Empresa', 'Acta de reunión de coordinación', 'Acta de reunión de coordinación', 'CoordinacionCae', 0),
                ('Empresa', 'Informe de investigación de accidente o incidente', 'Informe de investigación de accidente o incidente', 'CoordinacionCae', 0),
                ('Empresa', 'Protocolo frente al acoso sexual y por razón de sexo', 'Protocolo frente al acoso sexual y por razón de sexo', 'Igualdad', 0),
                ('Empresa', 'Registro retributivo', 'Registro retributivo', 'Igualdad', 0),
                ('Empresa', 'Información de riesgos propios aportados al centro', 'Información de riesgos propios aportados al centro', 'CoordinacionCae', 0),
                ('Empresa', 'Información y coordinación con trabajadores autónomos', 'Información y coordinación con trabajadores autónomos', 'CoordinacionCae', 0),
                ('Empresa', 'Registro del deber de vigilancia sobre subcontratas', 'Registro del deber de vigilancia sobre subcontratas', 'CoordinacionCae', 0),
                ('Trabajador', 'Recibí de normas, procedimientos y plan de emergencia', 'Recibí de normas, procedimientos y plan de emergencia', 'InformacionYEpi', 0),
                ('Empresa', 'Certificado de inscripción en el REA', 'Certificado de inscripción en el REA', 'Construccion', 0),
                ('Empresa', 'Recibo de cuota de autónomos (RETA)', 'Recibo de cuota de autónomos', 'SeguridadSocialYHacienda', 0)
            ) AS m(ambito, anterior, nuevo, grupo, datos)
            WHERE t."TenantId" <> '00000000-0000-0000-0000-000000000001'
              AND t."AmbitoAplicacion" = m.ambito
              AND t."Nombre" = m.anterior;
            """;

        /// <summary>
        /// Copias: archivado con el sustituto del MISMO Tenant (la FK compuesta lo exige) y sin requerirse de serie,
        /// igual que TipoDocumento.Archivar. «Procedimiento de Coordinación de Actividades Empresariales» deja de
        /// ser obligatorio. Si la copia de «RLC» no se pudo renombrar porque el Tenant ya tenía un tipo propio llamado
        /// «RLC (TC1)», el sustituto es la copia que sigue llamándose «RLC» (la que desciende del tipo base que sustituye
        /// en el Tenant #1), no el tipo propio homónimo.
        /// </summary>
        private const string ArchivarCopias = """
            UPDATE "TiposDocumento" t
            SET "Archivado" = true,
                "Requerido" = 'No',
                "SustitutoId" = (
                    SELECT s."Id" FROM "TiposDocumento" s
                    WHERE s."TenantId" = t."TenantId" AND s."AmbitoAplicacion" = a.ambito_sustituto
                      AND s."Nombre" IN (a.sustituto, a.sustituto_sin_renombrar) AND s."Archivado" = false
                    ORDER BY (s."Nombre" = a.sustituto_sin_renombrar) DESC
                    LIMIT 1)
            FROM (VALUES
                ('Empresa', 'Recibo de pago RLC/TC1', 'Empresa', 'RLC (TC1)', 'RLC'),
                ('Empresa', 'RLC/TC1 + Recibo de pago', 'Empresa', 'RLC (TC1)', 'RLC'),
                ('Trabajador', 'Formación EPIs', 'Trabajador', 'Entrega de EPI', 'Entrega de EPI'),
                ('Empresa', 'Protocolo frente al acoso sexual y por razón de sexo', NULL, NULL, NULL)
            ) AS a(ambito, nombre, ambito_sustituto, sustituto, sustituto_sin_renombrar)
            WHERE t."TenantId" <> '00000000-0000-0000-0000-000000000001'
              AND t."AmbitoAplicacion" = a.ambito
              AND t."Nombre" = a.nombre;

            UPDATE "TiposDocumento"
            SET "Requerido" = 'No'
            WHERE "TenantId" <> '00000000-0000-0000-0000-000000000001'
              AND "AmbitoAplicacion" = 'Empresa'
              AND "Nombre" = 'Procedimiento de Coordinación de Actividades Empresariales'
              AND "Requerido" <> 'No';
            """;

        /// <summary>Los tres tipos nuevos, en cada Tenant con catálogo propio que no tenga ya uno con ese nombre.</summary>
        private const string InsertarTiposNuevosEnCopias = """
            INSERT INTO "TiposDocumento" (
                "Id", "TenantId", "Nombre", "VigenciaMeses", "AplicaVencimientoAutomatico", "Orden", "Notas",
                "AmbitoAplicacion", "Requerido", "Naturaleza", "Sensibilidad", "LecturaIaActiva",
                "DeteccionTrabajadoresActiva", "VerificacionIaActiva", "PerfilDocumentoOficial",
                "TiposContratacionDeSerie", "Grupo", "DatosAdicionales", "Archivado", "SustitutoId",
                "CriteriosValidacion", "Descripcion", "Observaciones", "SeSolicitaA")
            SELECT md5('catalogo-2026-10-17:' || tn."TenantId"::text || ':' || n.base)::uuid, tn."TenantId", n.nombre,
                NULL, false, n.orden, n.notas, 'Empresa', 'No', 'RequisitoCliente', n.sensibilidad, true,
                false, false, 'Ninguno', 7, n.grupo, 0, false, NULL, NULL, NULL, NULL, NULL
            FROM (SELECT DISTINCT "TenantId" FROM "TiposDocumento"
                  WHERE "TenantId" <> '00000000-0000-0000-0000-000000000001') AS tn
            CROSS JOIN (VALUES
                ('80000000-0000-0000-0000-000000000004', 'Alta en el RETA', 56,
                 'Resolución de alta del autónomo en el Régimen Especial de Trabajadores Autónomos: prueba la afiliación, mientras que el recibo de cuota prueba el pago. El autónomo es su propia Empresa. Sin caducidad propia — vencimiento manual.',
                 'DatosPersonales', 'SeguridadSocialYHacienda'),
                ('80000000-0000-0000-0000-000000000005', 'Plan de emergencia y evacuación', 57,
                 'Plan de emergencia y evacuación de la Empresa. Opcional y nunca obligatorio por defecto: el art. 10.2 RD 171/2004 no lo exige al contratista. Vigente hasta revisión — vencimiento manual.',
                 'DatosPersonales', 'OrganizacionPreventiva'),
                ('80000000-0000-0000-0000-000000000006', 'Documento requerido por el Cliente empresarial', 58,
                 'Tipo genérico para los formularios propios que pide un Cliente empresarial concreto. Vigencia según cada Cliente empresarial — vencimiento manual.',
                 'CategoriaEspecialSalud', 'CoordinacionCae')
            ) AS n(base, nombre, orden, notas, sensibilidad, grupo)
            WHERE NOT EXISTS (
                SELECT 1 FROM "TiposDocumento" o WHERE o."TenantId" = tn."TenantId" AND o."Nombre" = n.nombre);
            """;

        /// <summary>Alias de las copias (los originales de T3 y los nuevos), sin chocar con los que ya tengan.</summary>
        private const string InsertarAliasEnCopias = """
            INSERT INTO "TiposDocumentoAlias" ("Id", "TenantId", "Texto", "TipoDocumentoId")
            SELECT md5('alias-2026-10-17:' || c."Id"::text || ':' || a.texto)::uuid, c."TenantId", a.texto, c."Id"
            FROM (VALUES
                ('Trabajador', 'Certificado de aptitud médica', 'Apto médico laboral'),
                ('Trabajador', 'Entrega de EPI', 'EPIS (firma)'),
                ('Trabajador', 'Documento de identidad', 'DNI o NIE en vigor'),
                ('Trabajador', 'Documento de identidad', 'DNI/NIE/TIE'),
                ('Empresa', 'RLC (TC1)', 'RLC/TC1'),
                ('Empresa', 'RLC (TC1)', 'TC1'),
                ('Empresa', 'RNT', 'RNT/TC2'),
                ('Empresa', 'RNT', 'TC2'),
                ('Empresa', 'Servicio de Prevención Ajeno', 'SPA (Servicio de Prevención Ajeno)'),
                ('Empresa', 'Servicio de Prevención Ajeno', 'SPA'),
                ('Empresa', 'Evaluación de Riesgos Laborales', 'EVR (Evaluación de Riesgos Laborales)'),
                ('Empresa', 'Evaluación de Riesgos Laborales', 'EVR'),
                ('Empresa', 'Planificación de la Actividad Preventiva', 'PAP (Planificación de la Actividad Preventiva)'),
                ('Empresa', 'Planificación de la Actividad Preventiva', 'PAP'),
                ('Empresa', 'Tarjeta de identificación fiscal', 'Tarjeta CIF'),
                ('Empresa', 'Tarjeta de identificación fiscal', 'CIF'),
                ('Trabajador', 'Certificado de aptitud médica', 'Reconocimiento médico'),
                ('Trabajador', 'Certificado de aptitud médica', 'Apto'),
                ('Trabajador', 'Entrega de EPI', 'EPI'),
                ('Trabajador', 'Reciclaje PRL', 'Reciclaje 4h'),
                ('Trabajador', 'Formación PRL del puesto', 'Formación Art. 19'),
                ('Trabajador', 'Formación PRL del puesto', 'Formación en prevención'),
                ('Trabajador', 'Formación PRL del puesto', 'Art. 19'),
                ('Trabajador', 'Formación PRL de convenio', 'Formación 60h (base convenio)'),
                ('Trabajador', 'Formación PRL de convenio', 'Formación 60h'),
                ('Trabajador', 'Formación PRL de convenio', 'Recurso preventivo 60h'),
                ('Trabajador', 'Formación PRL de convenio', 'Nivel básico PRL 60h'),
                ('Trabajador', 'Formación PRL de convenio por oficio', 'Formación 20h'),
                ('Trabajador', 'Formación PRL de convenio por oficio, módulo específico', 'Formación 6h'),
                ('Trabajador', 'Información de riesgos del puesto', 'Información Art. 18'),
                ('Trabajador', 'Información de riesgos del puesto', 'Registro de información PRL'),
                ('Trabajador', 'Información de riesgos del puesto', 'Art. 18'),
                ('Trabajador', 'PEMP (plataformas elevadoras)', 'Plataforma elevadora'),
                ('Trabajador', 'PEMP (plataformas elevadoras)', 'Plataforma elevadora móvil'),
                ('Trabajador', 'Bloqueo y etiquetado (LOTO)', 'LOTO (4h)'),
                ('Trabajador', 'Trabajos en altura', 'Trabajos en altura (8h)'),
                ('Trabajador', 'Trabajos en altura', 'Altura'),
                ('Trabajador', 'Trabajos en altura', 'Formación en altura'),
                ('Trabajador', 'Alta en Seguridad Social', 'TA.2'),
                ('Trabajador', 'Alta en Seguridad Social', 'TA2'),
                ('Trabajador', 'Alta en Seguridad Social', 'IDC'),
                ('Trabajador', 'Alta en Seguridad Social', 'Alta TA2'),
                ('Trabajador', 'Alta en Seguridad Social', 'Informe de datos de cotización'),
                ('Trabajador', 'Permiso de conducir', 'Carne de conducir'),
                ('Trabajador', 'Certificado A1', 'Certificado A1 de Seguridad Social'),
                ('Trabajador', 'Certificado A1', 'A1'),
                ('Trabajador', 'Certificado A1', 'Formulario A1'),
                ('Trabajador', 'Documento de identidad', 'DNI/NIE'),
                ('Trabajador', 'Documento de identidad', 'DNI'),
                ('Trabajador', 'Documento de identidad', 'NIE'),
                ('Trabajador', 'Autorización de uso de equipo de trabajo/maquinaria', 'Autorización de uso de equipo de trabajo'),
                ('Empresa', 'Certificado de estar al corriente con la Seguridad Social', 'Certificado SS'),
                ('Empresa', 'Certificado de estar al corriente con la Seguridad Social', 'Certificado TGSS'),
                ('Empresa', 'Certificado de estar al corriente con la Seguridad Social', 'Corriente SS'),
                ('Empresa', 'Certificado de estar al corriente con la Seguridad Social', 'Certificado de no deuda SS'),
                ('Empresa', 'Certificado de estar al corriente con Hacienda', 'Certificado AEAT'),
                ('Empresa', 'Certificado de estar al corriente con Hacienda', 'AEAT'),
                ('Empresa', 'Certificado de estar al corriente con Hacienda', 'Corriente Hacienda'),
                ('Empresa', 'Certificado de estar al corriente con Hacienda', 'Certificado de contratistas'),
                ('Empresa', 'Informe de trabajadores en alta', 'ITA'),
                ('Empresa', 'Informe de trabajadores en alta', 'Trabajadores en alta (CCC)'),
                ('Empresa', 'RLC (TC1)', 'RLC'),
                ('Empresa', 'RLC (TC1)', 'Recibo de pago RLC/TC1'),
                ('Empresa', 'RLC (TC1)', 'RLC/TC1 + Recibo de pago'),
                ('Empresa', 'Mutua de Accidentes', 'Mutua'),
                ('Empresa', 'Mutua de Accidentes', 'Certificado de mutua'),
                ('Empresa', 'Mutua de Accidentes', 'Mutua AT/EP'),
                ('Empresa', 'Seguro de Responsabilidad Civil', 'Seguro de Responsabilidad Civil + recibo de pago'),
                ('Empresa', 'Seguro de Responsabilidad Civil', 'Seguro RC + recibo'),
                ('Empresa', 'Seguro de Responsabilidad Civil', 'Seguro RC'),
                ('Empresa', 'Modalidad Preventiva', 'Certificado SPA'),
                ('Empresa', 'Certificado de inscripción en el REA', 'REA'),
                ('Empresa', 'Recibo de cuota de autónomos', 'Recibo de cuota de autónomos (RETA)'),
                ('Empresa', 'Recibo de cuota de autónomos', 'RETA'),
                ('Empresa', 'Alta en el RETA', 'Alta de autónomo'),
                ('Empresa', 'Alta en el RETA', 'Alta RETA'),
                ('Empresa', 'Plan de emergencia y evacuación', 'Plan de emergencia'),
                ('Empresa', 'Plan de emergencia y evacuación', 'Plan de evacuación'),
                ('Vehiculo', 'ITC', 'ITV'),
                ('Vehiculo', 'Seguro', 'Seguro del vehículo'),
                ('Vehiculo', 'Seguro', 'Póliza del vehículo')
            ) AS a(ambito, nombre, texto)
            JOIN "TiposDocumento" c
              ON c."AmbitoAplicacion" = a.ambito AND c."Nombre" = a.nombre
             AND c."TenantId" <> '00000000-0000-0000-0000-000000000001'
            ON CONFLICT DO NOTHING;
            """;

        /// <summary>
        /// La configuración que pedía un tipo archivado pasa a pedir su sustituto, en todos los Tenants (también el #1):
        /// un tipo archivado no admite altas nuevas, así que una fila que lo siguiera pidiendo sería un requisito imposible.
        /// El sustituto sale siempre de <c>t."SustitutoId"</c>, que la FK compuesta (TenantId, SustitutoId) ya garantiza del
        /// mismo Tenant, y cada JOIN exige el mismo TenantId: nunca se resuelve por nombre ni cruza de Tenant (la migración
        /// corre con BYPASSRLS). Varios archivados con el mismo sustituto (las dos variantes de RLC) se agrupan antes de
        /// escribir, para no violar los índices únicos. Ids deterministas (md5) para lo que se inserta.
        /// <list type="bullet">
        /// <item>TiposDocumentoCentros: las filas Incluido de los archivados con sustituto se agrupan por (Tenant, Centro,
        /// sustituto). Si el Centro no tenía fila del sustituto se crea con los valores de una de ellas (la que bloquea el
        /// acceso, y si no la de menor Id); si la tenía y la incluía, conserva su periodicidad y su tolerancia, bloquea el
        /// acceso si alguna lo bloqueaba y recibe la plantilla en blanco si no tenía; si la excluía, la exclusión se respeta.
        /// Después, toda fila de un tipo archivado (con o sin sustituto) deja de incluirlo y de bloquear.</item>
        /// <item>ProyectosRequisitos y PlantillasRequisitosEventoTipos: el requisito pasa al sustituto (sin duplicar) y se
        /// borra el del archivado. Un archivado sin sustituto se queda; RequisitosDeEventos ya no lo cuenta.</item>
        /// <item>ContactosAgendaTiposDocumento: el contacto también atiende el sustituto; el enlace al archivado se queda,
        /// porque sigue habiendo documentos de ese tipo que reclamar.</item>
        /// <item>ToleranciasDocumentoClienteEmpresarial: el Cliente empresarial sin tolerancia propia para el sustituto
        /// recibe la menor de las de sus archivados; la del archivado se queda para sus documentos.</item>
        /// </list>
        /// </summary>
        private const string PasarConfiguracionAlSustituto = """
            WITH origen AS (
                SELECT DISTINCT ON (tc."TenantId", tc."CentroId", t."SustitutoId")
                       tc."TenantId", tc."CentroId", t."SustitutoId" AS sustituto,
                       tc."PeriodicidadEspecialMeses", tc."ToleranciaDias", tc."ArchivoUrl", tc."NombreArchivoOriginal"
                FROM "TiposDocumentoCentros" tc
                JOIN "TiposDocumento" t ON t."Id" = tc."TipoDocumentoId" AND t."TenantId" = tc."TenantId"
                WHERE t."Archivado" AND t."SustitutoId" IS NOT NULL AND tc."Incluido"
                ORDER BY tc."TenantId", tc."CentroId", t."SustitutoId", tc."BloqueaAcceso" DESC, tc."Id"
            ),
            bloqueo AS (
                SELECT tc."TenantId", tc."CentroId", t."SustitutoId" AS sustituto, bool_or(tc."BloqueaAcceso") AS bloquea,
                       (array_agg(tc."ArchivoUrl" ORDER BY tc."Id") FILTER (WHERE tc."ArchivoUrl" IS NOT NULL))[1] AS archivo,
                       (array_agg(tc."NombreArchivoOriginal" ORDER BY tc."Id") FILTER (WHERE tc."ArchivoUrl" IS NOT NULL))[1] AS nombre_archivo
                FROM "TiposDocumentoCentros" tc
                JOIN "TiposDocumento" t ON t."Id" = tc."TipoDocumentoId" AND t."TenantId" = tc."TenantId"
                WHERE t."Archivado" AND t."SustitutoId" IS NOT NULL AND tc."Incluido"
                GROUP BY tc."TenantId", tc."CentroId", t."SustitutoId"
            )
            INSERT INTO "TiposDocumentoCentros" (
                "Id", "TenantId", "TipoDocumentoId", "CentroId", "Incluido", "PeriodicidadEspecialMeses", "BloqueaAcceso",
                "ToleranciaDias", "ArchivoUrl", "NombreArchivoOriginal")
            SELECT md5('centro-sustituto-2026-10-17:' || o."TenantId"::text || ':' || o."CentroId"::text || ':' || o.sustituto::text)::uuid,
                   o."TenantId", o.sustituto, o."CentroId", true, o."PeriodicidadEspecialMeses", b.bloquea,
                   o."ToleranciaDias", b.archivo, b.nombre_archivo
            FROM origen o
            JOIN bloqueo b ON b."TenantId" = o."TenantId" AND b."CentroId" = o."CentroId" AND b.sustituto = o.sustituto
            ON CONFLICT ("TenantId", "TipoDocumentoId", "CentroId") DO UPDATE
            SET "BloqueaAcceso" = "TiposDocumentoCentros"."BloqueaAcceso" OR EXCLUDED."BloqueaAcceso",
                "ArchivoUrl" = CASE WHEN "TiposDocumentoCentros"."ArchivoUrl" IS NULL
                                    THEN EXCLUDED."ArchivoUrl" ELSE "TiposDocumentoCentros"."ArchivoUrl" END,
                "NombreArchivoOriginal" = CASE WHEN "TiposDocumentoCentros"."ArchivoUrl" IS NULL
                                    THEN EXCLUDED."NombreArchivoOriginal" ELSE "TiposDocumentoCentros"."NombreArchivoOriginal" END
            WHERE "TiposDocumentoCentros"."Incluido";

            UPDATE "TiposDocumentoCentros" tc
            SET "Incluido" = false, "BloqueaAcceso" = false
            FROM "TiposDocumento" t
            WHERE t."Id" = tc."TipoDocumentoId" AND t."TenantId" = tc."TenantId"
              AND t."Archivado" AND (tc."Incluido" OR tc."BloqueaAcceso");

            INSERT INTO "ProyectosRequisitos" ("Id", "TenantId", "ProyectoId", "TipoDocumentoId")
            SELECT DISTINCT md5('requisito-sustituto-2026-10-17:' || r."TenantId"::text || ':' || r."ProyectoId"::text || ':' || t."SustitutoId"::text)::uuid,
                   r."TenantId", r."ProyectoId", t."SustitutoId"
            FROM "ProyectosRequisitos" r
            JOIN "TiposDocumento" t ON t."Id" = r."TipoDocumentoId" AND t."TenantId" = r."TenantId"
            WHERE t."Archivado" AND t."SustitutoId" IS NOT NULL
            ON CONFLICT DO NOTHING;

            DELETE FROM "ProyectosRequisitos" r
            USING "TiposDocumento" t
            WHERE t."Id" = r."TipoDocumentoId" AND t."TenantId" = r."TenantId"
              AND t."Archivado" AND t."SustitutoId" IS NOT NULL;

            INSERT INTO "PlantillasRequisitosEventoTipos" ("Id", "TenantId", "PlantillaId", "TipoDocumentoId")
            SELECT DISTINCT md5('plantilla-sustituto-2026-10-17:' || p."TenantId"::text || ':' || p."PlantillaId"::text || ':' || t."SustitutoId"::text)::uuid,
                   p."TenantId", p."PlantillaId", t."SustitutoId"
            FROM "PlantillasRequisitosEventoTipos" p
            JOIN "TiposDocumento" t ON t."Id" = p."TipoDocumentoId" AND t."TenantId" = p."TenantId"
            WHERE t."Archivado" AND t."SustitutoId" IS NOT NULL
            ON CONFLICT DO NOTHING;

            DELETE FROM "PlantillasRequisitosEventoTipos" p
            USING "TiposDocumento" t
            WHERE t."Id" = p."TipoDocumentoId" AND t."TenantId" = p."TenantId"
              AND t."Archivado" AND t."SustitutoId" IS NOT NULL;

            INSERT INTO "ContactosAgendaTiposDocumento" ("Id", "TenantId", "ContactoAgendaId", "TipoDocumentoId")
            SELECT DISTINCT md5('contacto-sustituto-2026-10-17:' || c."TenantId"::text || ':' || c."ContactoAgendaId"::text || ':' || t."SustitutoId"::text)::uuid,
                   c."TenantId", c."ContactoAgendaId", t."SustitutoId"
            FROM "ContactosAgendaTiposDocumento" c
            JOIN "TiposDocumento" t ON t."Id" = c."TipoDocumentoId" AND t."TenantId" = c."TenantId"
            WHERE t."Archivado" AND t."SustitutoId" IS NOT NULL
            ON CONFLICT DO NOTHING;

            INSERT INTO "ToleranciasDocumentoClienteEmpresarial" ("Id", "TenantId", "ClienteEmpresarialId", "TipoDocumentoId", "ToleranciaDias")
            SELECT md5('tolerancia-sustituto-2026-10-17:' || x."TenantId"::text || ':' || x."ClienteEmpresarialId"::text || ':' || t."SustitutoId"::text)::uuid,
                   x."TenantId", x."ClienteEmpresarialId", t."SustitutoId", min(x."ToleranciaDias")
            FROM "ToleranciasDocumentoClienteEmpresarial" x
            JOIN "TiposDocumento" t ON t."Id" = x."TipoDocumentoId" AND t."TenantId" = x."TenantId"
            WHERE t."Archivado" AND t."SustitutoId" IS NOT NULL
            GROUP BY x."TenantId", x."ClienteEmpresarialId", t."SustitutoId"
            ON CONFLICT DO NOTHING;
            """;

        /// <summary>Down de las copias: borra solo lo insertado por el Up (Id deterministas) y deshace renombrados y requisitos.</summary>
        private const string RevertirCopias = """
            DELETE FROM "TiposDocumentoAlias" al
            WHERE al."TenantId" <> '00000000-0000-0000-0000-000000000001'
              AND al."Id" = md5('alias-2026-10-17:' || al."TipoDocumentoId"::text || ':' || al."Texto")::uuid;

            DELETE FROM "TiposDocumento" t
            USING (VALUES
                ('80000000-0000-0000-0000-000000000004'),
                ('80000000-0000-0000-0000-000000000005'),
                ('80000000-0000-0000-0000-000000000006')
            ) AS n(base)
            WHERE t."TenantId" <> '00000000-0000-0000-0000-000000000001'
              AND t."Id" = md5('catalogo-2026-10-17:' || t."TenantId"::text || ':' || n.base)::uuid;

            UPDATE "TiposDocumento"
            SET "Requerido" = 'Si'
            WHERE "TenantId" <> '00000000-0000-0000-0000-000000000001'
              AND "AmbitoAplicacion" = 'Empresa'
              AND "Nombre" IN ('Recibo de pago RLC/TC1', 'RLC/TC1 + Recibo de pago')
              AND "Archivado" = true;

            UPDATE "TiposDocumento"
            SET "Requerido" = 'Condicional'
            WHERE "TenantId" <> '00000000-0000-0000-0000-000000000001'
              AND "AmbitoAplicacion" = 'Empresa'
              AND "Nombre" = 'Procedimiento de Coordinación de Actividades Empresariales'
              AND "Requerido" = 'No';

            UPDATE "TiposDocumento" t
            SET "Nombre" = m.anterior
            FROM (VALUES
                ('Trabajador', 'Reciclaje 4h', 'Reciclaje PRL'),
                ('Trabajador', 'Formación Art. 19', 'Formación PRL del puesto'),
                ('Trabajador', 'Formación 60h (base convenio)', 'Formación PRL de convenio'),
                ('Trabajador', 'Formación 20h', 'Formación PRL de convenio por oficio'),
                ('Trabajador', 'Formación 6h', 'Formación PRL de convenio por oficio, módulo específico'),
                ('Trabajador', 'Información Art. 18', 'Información de riesgos del puesto'),
                ('Trabajador', 'LOTO (4h)', 'Bloqueo y etiquetado (LOTO)'),
                ('Trabajador', 'Trabajos en altura (8h)', 'Trabajos en altura'),
                ('Empresa', 'ITA', 'Informe de trabajadores en alta'),
                ('Empresa', 'RLC', 'RLC (TC1)'),
                ('Empresa', 'Mutua', 'Mutua de Accidentes'),
                ('Empresa', 'Seguro de Responsabilidad Civil + recibo de pago', 'Seguro de Responsabilidad Civil'),
                ('Trabajador', 'Certificado A1 de Seguridad Social', 'Certificado A1'),
                ('Trabajador', 'Autorización de uso de equipo de trabajo', 'Autorización de uso de equipo de trabajo/maquinaria'),
                ('Empresa', 'Recibo de cuota de autónomos (RETA)', 'Recibo de cuota de autónomos')
            ) AS m(ambito, anterior, nuevo)
            WHERE t."TenantId" <> '00000000-0000-0000-0000-000000000001'
              AND t."AmbitoAplicacion" = m.ambito
              AND t."Nombre" = m.nuevo
              AND NOT EXISTS (
                  SELECT 1 FROM "TiposDocumento" o WHERE o."TenantId" = t."TenantId" AND o."Nombre" = m.anterior);
            """;
    }
}
