using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// B-05 del plan backend Inbound: el estado del contrato de acceso (Pendiente de confirmar, Activo, Suspendido,
    /// Finalizado, Cancelado y Rechazado; duda 3 del DECISION_LOG), sus condiciones (tipo, propia actividad, nivel máximo de
    /// subcontrata y cupos con su motivo) y el historial de cambios del fin contratado (Renovar y Editar).
    /// <para>
    /// Crea en <c>ContratosAcceso</c> siete columnas: <c>Estado</c> (NOT NULL DEFAULT 'Activo') y seis que admiten nulo, con
    /// sus CHECK. Rellena <c>Estado = 'Finalizado'</c> en los contratos que ya tienen cierre real. Crea la tabla
    /// <c>HistorialVigenciaContratos</c>, hija del contrato por la FK compuesta (TenantId, ContratoAccesoId), con RLS
    /// <c>FORCE</c>, la política <c>aislamiento_tenant</c> y solo SELECT e INSERT para <c>cae_app_runtime</c> y SELECT para
    /// <c>cae_app_soporte</c>. Y reescribe <c>app_inbound_tenant_contratista_del_contrato</c> con el filtro
    /// <c>"Estado" IN ('Activo', 'Finalizado')</c>, con la misma firma, lenguaje, volatilidad y <c>search_path</c>, sin
    /// <c>SECURITY DEFINER</c>: conserva propietario y privilegios.
    /// </para>
    /// <para>
    /// Compatible con la ranura anterior durante el relevo azul/verde: solo añade (columnas, CHECK, una tabla, un filtro) y
    /// no borra ni renombra nada que la ranura anterior lea. Sus altas no mandan <c>Estado</c> y reciben el DEFAULT 'Activo',
    /// que es lo que eran; sus lecturas no piden las columnas nuevas. Su <c>Cerrar</c> deja un contrato Activo con cierre
    /// real, que da acceso solo hasta el cierre, igual que antes: no se finaliza hasta que lo cierre de nuevo la ranura nueva o
    /// lo corrija una migración, hueco acotado y declarado. Su <c>Reabrir</c> sobre un contrato que la ranura nueva ya finalizó
    /// falla por <c>CK_ContratosAcceso_FinalizadoConCierre</c>: D-21 retira Reabrir, y la base no deja reabrirlo por la puerta
    /// de atrás. La ranura anterior no conoce la tabla nueva ni el estado, y un contrato Cancelado o Pendiente que ella vea
    /// abierto sigue sin dar lectura entre Tenants, porque el filtro está en la base.
    /// </para>
    /// <para>
    /// El Down devuelve la auxiliar al cuerpo exacto de <c>AnadeLineaInboundProductoContratado</c> antes de borrar la columna
    /// que lee, y después borra la tabla y las columnas: los estados y el historial se pierden, que es lo que significa
    /// revertir B-05.
    /// </para>
    /// </summary>
    public partial class AnadeEstadoContratoAcceso : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CupoSubcontratas",
                table: "ContratosAcceso",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CupoTrabajadores",
                table: "ContratosAcceso",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Estado",
                table: "ContratosAcceso",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Activo");

            migrationBuilder.AddColumn<string>(
                name: "MotivoCupo",
                table: "ContratosAcceso",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NivelMaxSubcontrata",
                table: "ContratosAcceso",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PropiaActividad",
                table: "ContratosAcceso",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tipo",
                table: "ContratosAcceso",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            // Los contratos ya cerrados pasan a Finalizado. Son filas de todos los Tenants bajo FORCE ROW LEVEL SECURITY:
            // sin la guarda, un migrador que no evite la RLS no vería ninguna y la migración pasaría sin relleno.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'AnadeEstadoContratoAcceso necesita un migrador superusuario o con BYPASSRLS';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql("""
                UPDATE public."ContratosAcceso" SET "Estado" = 'Finalizado' WHERE "FechaCierreReal" IS NOT NULL;
                """);

            migrationBuilder.CreateTable(
                name: "HistorialVigenciaContratos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContratoAccesoId = table.Column<Guid>(type: "uuid", nullable: false),
                    FechaFinAnterior = table.Column<DateOnly>(type: "date", nullable: true),
                    FechaFinNueva = table.Column<DateOnly>(type: "date", nullable: true),
                    Origen = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RegistradoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistradoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistorialVigenciaContratos", x => x.Id);
                    table.CheckConstraint("CK_HistorialVigenciaContratos_CambiaElFin", "\"FechaFinAnterior\" IS DISTINCT FROM \"FechaFinNueva\"");
                    table.CheckConstraint("CK_HistorialVigenciaContratos_Origen", "\"Origen\" IN ('Renovacion', 'Edicion')");
                    table.ForeignKey(
                        name: "FK_HistorialVigenciaContratos_ContratosAcceso_TenantId_Contrat~",
                        columns: x => new { x.TenantId, x.ContratoAccesoId },
                        principalTable: "ContratosAcceso",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ContratosAcceso_CuposNoNegativos",
                table: "ContratosAcceso",
                sql: "(\"CupoTrabajadores\" IS NULL OR \"CupoTrabajadores\" >= 0) AND (\"CupoSubcontratas\" IS NULL OR \"CupoSubcontratas\" >= 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ContratosAcceso_Estado",
                table: "ContratosAcceso",
                sql: "\"Estado\" IN ('PendienteDeConfirmar', 'Activo', 'Suspendido', 'Finalizado', 'Cancelado', 'Rechazado')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ContratosAcceso_FinalizadoConCierre",
                table: "ContratosAcceso",
                sql: "\"Estado\" <> 'Finalizado' OR \"FechaCierreReal\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ContratosAcceso_MotivoSiHayCupo",
                table: "ContratosAcceso",
                sql: "(\"CupoTrabajadores\" IS NULL AND \"CupoSubcontratas\" IS NULL) = (\"MotivoCupo\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ContratosAcceso_NivelMaxSubcontrata",
                table: "ContratosAcceso",
                sql: "\"NivelMaxSubcontrata\" IS NULL OR \"NivelMaxSubcontrata\" BETWEEN 0 AND 3");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ContratosAcceso_PropiaActividad",
                table: "ContratosAcceso",
                sql: "\"PropiaActividad\" IN ('Si', 'No', 'NoLoSe')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ContratosAcceso_Tipo",
                table: "ContratosAcceso",
                sql: "\"Tipo\" IN ('Fijo', 'Recurrente', 'Temporal', 'Puntual', 'Obra', 'Emergencia')");

            migrationBuilder.CreateIndex(
                name: "IX_HistorialVigenciaContratos_TenantId_ContratoAccesoId_Regist~",
                table: "HistorialVigenciaContratos",
                columns: new[] { "TenantId", "ContratoAccesoId", "RegistradoEnUtc" });

            // El historial es del Tenant propietario del contrato, y solo se añade: sin UPDATE ni DELETE para ningún rol de la
            // aplicación. Soporte lo lee dentro de su Tenant, como el contrato.
            migrationBuilder.Sql("""
                ALTER TABLE public."HistorialVigenciaContratos" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."HistorialVigenciaContratos" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."HistorialVigenciaContratos"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."HistorialVigenciaContratos" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT ON TABLE public."HistorialVigenciaContratos" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."HistorialVigenciaContratos" TO cae_app_soporte;
                """);

            // La auxiliar de la lectura entre Tenants (B-01) con un filtro más: solo un contrato Activo o Finalizado da acceso,
            // como ContratoAcceso.VigenteEn. Pendiente de confirmar, Suspendido, Cancelado y Rechazado no.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION public.app_inbound_tenant_contratista_del_contrato(p_lector uuid, p_contrato uuid, p_hoy date) RETURNS uuid
                    LANGUAGE sql STABLE
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT c."TenantContratistaId"
                    FROM public."ContratosAcceso" c
                   WHERE c."TenantId" = p_lector
                     AND c."Id" = p_contrato
                     AND NOT c."EstaEliminado"
                     AND c."Estado" IN ('Activo', 'Finalizado')
                     AND c."FechaInicio" <= p_hoy
                     AND (c."FechaFin" IS NULL OR p_hoy <= c."FechaFin")
                     AND (c."FechaCierreReal" IS NULL OR p_hoy <= c."FechaCierreReal")
                     AND c."TenantContratistaId" IS NOT NULL
                     AND EXISTS (SELECT 1 FROM public."ProductosContratados" pc
                                  WHERE pc."TenantId" = p_lector AND pc."Linea" = 'Inbound'
                                    AND pc."Estado" = 'Vigente' AND NOT pc."EstaEliminado"
                                    AND pc."VigenteDesdeUtc" <= pg_catalog.now());
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Primero la auxiliar, que lee la columna Estado: vuelve al cuerpo exacto de B-01 (con el gate de la línea
            // Inbound), que no amplía nada respecto de lo que había antes de esta migración.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION public.app_inbound_tenant_contratista_del_contrato(p_lector uuid, p_contrato uuid, p_hoy date) RETURNS uuid
                    LANGUAGE sql STABLE
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT c."TenantContratistaId"
                    FROM public."ContratosAcceso" c
                   WHERE c."TenantId" = p_lector
                     AND c."Id" = p_contrato
                     AND NOT c."EstaEliminado"
                     AND c."FechaInicio" <= p_hoy
                     AND (c."FechaFin" IS NULL OR p_hoy <= c."FechaFin")
                     AND (c."FechaCierreReal" IS NULL OR p_hoy <= c."FechaCierreReal")
                     AND c."TenantContratistaId" IS NOT NULL
                     AND EXISTS (SELECT 1 FROM public."ProductosContratados" pc
                                  WHERE pc."TenantId" = p_lector AND pc."Linea" = 'Inbound'
                                    AND pc."Estado" = 'Vigente' AND NOT pc."EstaEliminado"
                                    AND pc."VigenteDesdeUtc" <= pg_catalog.now());
                $$;
                """);

            migrationBuilder.DropTable(
                name: "HistorialVigenciaContratos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ContratosAcceso_CuposNoNegativos",
                table: "ContratosAcceso");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ContratosAcceso_Estado",
                table: "ContratosAcceso");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ContratosAcceso_FinalizadoConCierre",
                table: "ContratosAcceso");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ContratosAcceso_MotivoSiHayCupo",
                table: "ContratosAcceso");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ContratosAcceso_NivelMaxSubcontrata",
                table: "ContratosAcceso");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ContratosAcceso_PropiaActividad",
                table: "ContratosAcceso");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ContratosAcceso_Tipo",
                table: "ContratosAcceso");

            migrationBuilder.DropColumn(
                name: "CupoSubcontratas",
                table: "ContratosAcceso");

            migrationBuilder.DropColumn(
                name: "CupoTrabajadores",
                table: "ContratosAcceso");

            migrationBuilder.DropColumn(
                name: "Estado",
                table: "ContratosAcceso");

            migrationBuilder.DropColumn(
                name: "MotivoCupo",
                table: "ContratosAcceso");

            migrationBuilder.DropColumn(
                name: "NivelMaxSubcontrata",
                table: "ContratosAcceso");

            migrationBuilder.DropColumn(
                name: "PropiaActividad",
                table: "ContratosAcceso");

            migrationBuilder.DropColumn(
                name: "Tipo",
                table: "ContratosAcceso");
        }
    }
}
