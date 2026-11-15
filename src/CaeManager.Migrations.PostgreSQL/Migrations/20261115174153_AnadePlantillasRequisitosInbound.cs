using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadePlantillasRequisitosInbound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PendienteRevisionLegal",
                table: "CatalogoRequisitosBase",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PlantillasRequisitos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ActividadId = table.Column<Guid>(type: "uuid", nullable: true),
                    RiesgoId = table.Column<Guid>(type: "uuid", nullable: true),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClienteEmpresarialId = table.Column<Guid>(type: "uuid", nullable: true),
                    Activa = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlantillasRequisitos", x => x.Id);
                    table.UniqueConstraint("AK_PlantillasRequisitos_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_PlantillasRequisitos_Tipo", "\"Tipo\" IN ('PorActividad', 'PorRiesgo', 'DeCentro', 'ExigenciasClienteEmpresarial')");
                    table.CheckConstraint("CK_PlantillasRequisitos_UnaReferenciaSegunTipo", "(\"Tipo\" = 'PorActividad') = (\"ActividadId\" IS NOT NULL) AND (\"Tipo\" = 'PorRiesgo') = (\"RiesgoId\" IS NOT NULL) AND (\"Tipo\" = 'DeCentro') = (\"CentroId\" IS NOT NULL) AND (\"Tipo\" = 'ExigenciasClienteEmpresarial') = (\"ClienteEmpresarialId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PlantillasRequisitos_CatalogoActividades_ActividadId",
                        column: x => x.ActividadId,
                        principalTable: "CatalogoActividades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlantillasRequisitos_CatalogoRiesgos_RiesgoId",
                        column: x => x.RiesgoId,
                        principalTable: "CatalogoRiesgos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlantillasRequisitos_Centros_TenantId_CentroId",
                        columns: x => new { x.TenantId, x.CentroId },
                        principalTable: "Centros",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlantillasRequisitos_Empresas_TenantId_ClienteEmpresarialId",
                        columns: x => new { x.TenantId, x.ClienteEmpresarialId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RequisitosPlantilla",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlantillaId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequisitoBaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    Entidad = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Origen = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ModoVigencia = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    VigenciaMeses = table.Column<int>(type: "integer", nullable: true),
                    ToleranciaDias = table.Column<int>(type: "integer", nullable: true),
                    BloqueaAcceso = table.Column<bool>(type: "boolean", nullable: false),
                    Criterio = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    SePideA = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TiposContratacion = table.Column<int>(type: "integer", nullable: false),
                    AmbitoDocumento = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Activo = table.Column<bool>(type: "boolean", nullable: false),
                    Equivalencias = table.Column<List<string>>(type: "text[]", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequisitosPlantilla", x => x.Id);
                    table.CheckConstraint("CK_RequisitosPlantilla_AmbitoDocumento", "\"AmbitoDocumento\" IN ('GeneralTec', 'EspecificoTce', 'EspecificoCentro')");
                    table.CheckConstraint("CK_RequisitosPlantilla_Entidad", "\"Entidad\" IN ('Empresa', 'Trabajador', 'VehiculoOMaquina')");
                    table.CheckConstraint("CK_RequisitosPlantilla_Equivalencias", "cardinality(\"Equivalencias\") <= 10");
                    table.CheckConstraint("CK_RequisitosPlantilla_ModoVigencia", "\"ModoVigencia\" IN ('Fija', 'LaPoneElDocumento', 'SinVencimiento')");
                    table.CheckConstraint("CK_RequisitosPlantilla_Origen", "\"Origen\" IN ('LegalN1', 'Convenio', 'Actividad', 'ExigenciaN3', 'PropioTenant')");
                    table.CheckConstraint("CK_RequisitosPlantilla_PropioSinBase", "\"Origen\" <> 'PropioTenant' OR \"RequisitoBaseId\" IS NULL");
                    table.CheckConstraint("CK_RequisitosPlantilla_SePideA", "\"SePideA\" IN ('Todos', 'Nacionales', 'Extranjeros')");
                    table.CheckConstraint("CK_RequisitosPlantilla_TiposContratacion", "\"TiposContratacion\" > 0 AND (\"TiposContratacion\" & ~7) = 0");
                    table.CheckConstraint("CK_RequisitosPlantilla_Tolerancia", "\"ToleranciaDias\" IS NULL OR (\"ToleranciaDias\" BETWEEN 1 AND 365 AND \"ModoVigencia\" <> 'SinVencimiento')");
                    table.CheckConstraint("CK_RequisitosPlantilla_VigenciaSiFija", "(\"ModoVigencia\" = 'Fija') = (\"VigenciaMeses\" IS NOT NULL) AND (\"VigenciaMeses\" IS NULL OR \"VigenciaMeses\" BETWEEN 1 AND 120)");
                    table.ForeignKey(
                        name: "FK_RequisitosPlantilla_CatalogoRequisitosBase_RequisitoBaseId",
                        column: x => x.RequisitoBaseId,
                        principalTable: "CatalogoRequisitosBase",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequisitosPlantilla_PlantillasRequisitos_TenantId_Plantilla~",
                        columns: x => new { x.TenantId, x.PlantillaId },
                        principalTable: "PlantillasRequisitos",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RequisitosPlantilla_TiposDocumento_TenantId_TipoDocumentoId",
                        columns: x => new { x.TenantId, x.TipoDocumentoId },
                        principalTable: "TiposDocumento",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasRequisitos_ActividadId",
                table: "PlantillasRequisitos",
                column: "ActividadId");

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasRequisitos_RiesgoId",
                table: "PlantillasRequisitos",
                column: "RiesgoId");

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasRequisitos_TenantId_ActividadId",
                table: "PlantillasRequisitos",
                columns: new[] { "TenantId", "ActividadId" },
                unique: true,
                filter: "\"ActividadId\" IS NOT NULL AND \"Activa\"");

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasRequisitos_TenantId_CentroId",
                table: "PlantillasRequisitos",
                columns: new[] { "TenantId", "CentroId" },
                unique: true,
                filter: "\"CentroId\" IS NOT NULL AND \"Activa\"");

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasRequisitos_TenantId_ClienteEmpresarialId",
                table: "PlantillasRequisitos",
                columns: new[] { "TenantId", "ClienteEmpresarialId" },
                unique: true,
                filter: "\"ClienteEmpresarialId\" IS NOT NULL AND \"Activa\"");

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasRequisitos_TenantId_RiesgoId",
                table: "PlantillasRequisitos",
                columns: new[] { "TenantId", "RiesgoId" },
                unique: true,
                filter: "\"RiesgoId\" IS NOT NULL AND \"Activa\"");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitosPlantilla_RequisitoBaseId",
                table: "RequisitosPlantilla",
                column: "RequisitoBaseId");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitosPlantilla_TenantId_PlantillaId_TipoDocumentoId",
                table: "RequisitosPlantilla",
                columns: new[] { "TenantId", "PlantillaId", "TipoDocumentoId" },
                unique: true,
                filter: "\"Activo\"");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitosPlantilla_TenantId_TipoDocumentoId",
                table: "RequisitosPlantilla",
                columns: new[] { "TenantId", "TipoDocumentoId" });

            // Las plantillas y sus requisitos son del Tenant TCE propietario: RLS por Tenant, como ContratosAcceso. El runtime
            // las crea y las cambia, pero no borra: plantillas y requisitos se retiran. Soporte TALVEG las lee dentro de su
            // Tenant; aprovisionamiento no las toca.
            migrationBuilder.Sql("""
                ALTER TABLE public."PlantillasRequisitos" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."PlantillasRequisitos" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."PlantillasRequisitos"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."PlantillasRequisitos" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT, UPDATE ON TABLE public."PlantillasRequisitos" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."PlantillasRequisitos" TO cae_app_soporte;

                ALTER TABLE public."RequisitosPlantilla" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."RequisitosPlantilla" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."RequisitosPlantilla"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."RequisitosPlantilla" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT, UPDATE ON TABLE public."RequisitosPlantilla" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."RequisitosPlantilla" TO cae_app_soporte;
                """);

            // R-14 (DECISION_LOG 2026-11-14, tanda 8): el certificado A1 del trabajador desplazado y el contrato de puesta a
            // disposición de una ETT son requisitos de plantilla según la modalidad (D-27), sin modelo de datos nuevo. Se crean
            // inactivos y pendientes de revisión legal: ninguna plantilla los adopta hasta que la revisión los active. La
            // lectura del catálogo (ObtenerCatalogoBaseQuery) solo devuelve los activos, así que sigue vacía para el runtime.
            migrationBuilder.Sql("""
                INSERT INTO public."CatalogoRequisitosBase" ("Id", "Codigo", "Nombre", "Fundamento", "Nivel", "Activo", "PendienteRevisionLegal")
                VALUES
                    ('b1400000-0000-4000-8000-0000000000a1', 'a1-trabajador-desplazado', 'Certificado A1 del trabajador desplazado',
                     'Reglamento (CE) 883/2004, art. 12, y Reglamento (CE) 987/2009, art. 19. Pendiente de revisión legal (R-14).',
                     'N1', false, true),
                    ('b1400000-0000-4000-8000-0000000000e1', 'puesta-a-disposicion-ett', 'Contrato de puesta a disposición de la ETT',
                     'Ley 14/1994, de empresas de trabajo temporal, arts. 6 y 28, y RD 216/1999. Pendiente de revisión legal (R-14).',
                     'N1', false, true)
                ON CONFLICT ("Codigo") DO UPDATE SET "Activo" = false, "PendienteRevisionLegal" = true;
                """);
            // Si el código ya existía (alta manual en otra ranura), no se respeta su estado: R-14 lo deja inactivo y
            // pendiente hasta la revisión legal, igual que a la fila sembrada.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Primero las tablas: RequisitosPlantilla tiene una FK RESTRICT al catálogo base, y un A1 o ETT ya adoptado
            // impediría borrar la semilla.
            migrationBuilder.DropTable(
                name: "RequisitosPlantilla");

            migrationBuilder.DropTable(
                name: "PlantillasRequisitos");

            migrationBuilder.Sql("""
                DELETE FROM public."CatalogoRequisitosBase" WHERE "Id" IN ('b1400000-0000-4000-8000-0000000000a1', 'b1400000-0000-4000-8000-0000000000e1');
                """);

            migrationBuilder.DropColumn(
                name: "PendienteRevisionLegal",
                table: "CatalogoRequisitosBase");
        }
    }
}
