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
    /// <c>SECURITY DEFINER</c>: conserva propietario y privilegios. Y crea el disparador
    /// <c>TR_ContratosAcceso_CierreIrreversible</c> (D-21): un cierre real ya escrito no cambia ni se quita para ningún rol.
    /// </para>
    /// <para>
    /// Compatible con la ranura anterior durante el relevo azul/verde: solo añade (columnas, CHECK, una tabla, un filtro, un
    /// disparador) y no borra ni renombra nada que la ranura anterior lea. Sus altas no mandan <c>Estado</c> y reciben el DEFAULT 'Activo',
    /// que es lo que eran; sus lecturas no piden las columnas nuevas. Su <c>Cerrar</c> deja un contrato con
    /// <c>Estado = 'Activo'</c> y cierre real, que da acceso solo hasta el cierre, igual que antes. La columna se queda en
    /// 'Activo' (la ranura nueva no la reescribe: su <c>Cerrar</c> responde «ya cerrado»), pero el dominio de la ranura nueva
    /// lo trata como Finalizado (<c>ContratoAcceso.EstadoEfectivo</c>): no admite ninguna transición y se muestra Finalizado.
    /// Normalizar la columna queda para una migración posterior al relevo, hueco acotado y declarado. Su <c>Cerrar</c> sobre
    /// un Pendiente de confirmar, un Cancelado o un Rechazado de la ranura nueva (que ella ve abiertos) les escribe el cierre:
    /// conservan su estado, siguen sin dar acceso y quedan sin transiciones. Su <c>Reabrir</c> falla siempre, por
    /// <c>TR_ContratosAcceso_CierreIrreversible</c>, cerrara quien cerrase el contrato (ella misma tras la migración, ella antes
    /// de la migración, o la ranura nueva): D-21 retira Reabrir, y la base no deja reabrir por la puerta de atrás. Es el único
    /// cambio de comportamiento para la ranura anterior, y falla cerrado: su Reabrir responde con un error de base de datos en
    /// vez de devolver el acceso. Su edición del fin no deja fila en el historial de vigencia: los cambios hechos en la ventana del relevo no
    /// constan en él, otro hueco acotado y declarado. La ranura anterior no conoce la tabla nueva ni el estado, y un contrato Cancelado o Pendiente que ella vea
    /// abierto sigue sin dar lectura entre Tenants, porque el filtro está en la base.
    /// </para>
    /// <para>
    /// El Down quita el disparador y, antes de quitar el filtro de estado, falla cerrado con los datos: a cada contrato que
    /// no está Activo ni Finalizado le pone el cierre real en su inicio (el más temprano que admite
    /// <c>CK_ContratosAcceso_CierreNoAnteriorAlInicio</c>) y, si empieza desde ayer en adelante, además lo marca como
    /// eliminado, porque el día de su inicio aún daría acceso. Así ningún Pendiente de confirmar, Suspendido, Cancelado o
    /// Rechazado vuelve a dar lectura entre Tenants por fechas. Después devuelve la auxiliar al cuerpo exacto de
    /// <c>AnadeLineaInboundProductoContratado</c> antes de borrar la columna que lee, y borra la tabla y las columnas: los
    /// estados y el historial se pierden, que es lo que significa revertir B-05, y esos contratos quedan cerrados (o
    /// eliminados) para la versión anterior. Necesita, como el Up, un migrador superusuario o con BYPASSRLS.
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
                sql: "(\"CupoTrabajadores\" IS NULL AND \"CupoSubcontratas\" IS NULL) = (\"MotivoCupo\" IS NULL) AND (\"MotivoCupo\" IS NULL OR \"MotivoCupo\" ~ '[^[:space:]]')");

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

            // D-21: el cierre real es irreversible. Escrito, no cambia ni se quita para ningún rol, también el de la ranura
            // anterior: su Reabrir (FechaCierreReal = NULL) falla siempre, cerrara quien cerrase el contrato. Escribirlo sobre
            // un contrato sin cierre (el Cerrar de las dos ranuras) sigue valiendo. Sin UPDATE OF: un BEFORE UPDATE por fila con
            // WHEN vigila el valor final de la fila, lo cambie quien lo cambie.
            migrationBuilder.Sql("""
                CREATE FUNCTION public.contrato_acceso_cierre_irreversible() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    RAISE EXCEPTION 'El contrato de acceso % ya tiene cierre real: el cierre no cambia ni se quita (D-21).', OLD."Id"
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'TR_ContratosAcceso_CierreIrreversible';
                END;
                $$;

                CREATE TRIGGER "TR_ContratosAcceso_CierreIrreversible" BEFORE UPDATE ON public."ContratosAcceso"
                    FOR EACH ROW
                    WHEN (OLD."FechaCierreReal" IS NOT NULL AND NEW."FechaCierreReal" IS DISTINCT FROM OLD."FechaCierreReal")
                    EXECUTE FUNCTION public.contrato_acceso_cierre_irreversible();
                """);

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
            // Antes que nada, el disparador del cierre irreversible: el relleno de abajo reescribe cierres que ya existen.
            migrationBuilder.Sql("""
                DROP TRIGGER "TR_ContratosAcceso_CierreIrreversible" ON public."ContratosAcceso";
                DROP FUNCTION public.contrato_acceso_cierre_irreversible();
                """);

            // Sin la columna Estado, la auxiliar de B-01 decide solo por fechas: un contrato Pendiente de confirmar,
            // Suspendido, Cancelado o Rechazado dentro de sus fechas volvería a dar lectura entre Tenants. Antes de quitar el
            // filtro, esos contratos reciben el cierre más temprano que admite CK_ContratosAcceso_CierreNoAnteriorAlInicio, su
            // propio inicio (también los que la ranura anterior cerró más tarde). Con eso dejan de dar acceso desde el día
            // siguiente al inicio; los que empiezan hoy o después darían acceso aún el día de su inicio, y esos además se
            // marcan como eliminados, que la auxiliar excluye. El margen de un día (desde ayer) cubre una petición que calculó
            // su día de negocio justo antes de medianoche. Activo y Finalizado no se tocan: dan el mismo acceso que antes.
            // Las mismas filas de todos los Tenants bajo FORCE ROW LEVEL SECURITY que el relleno del Up: la misma guarda.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'Revertir AnadeEstadoContratoAcceso necesita un migrador superusuario o con BYPASSRLS';
                    END IF;
                END
                $$;

                UPDATE public."ContratosAcceso"
                   SET "FechaCierreReal" = "FechaInicio",
                       "EstaEliminado" = true,
                       "EliminadoEnUtc" = COALESCE("EliminadoEnUtc", pg_catalog.now())
                 WHERE "Estado" NOT IN ('Activo', 'Finalizado')
                   AND "FechaInicio" >= (pg_catalog.timezone('Europe/Madrid', pg_catalog.now()))::date - 1;

                UPDATE public."ContratosAcceso"
                   SET "FechaCierreReal" = "FechaInicio"
                 WHERE "Estado" NOT IN ('Activo', 'Finalizado');
                """);

            // Después la auxiliar, que lee la columna Estado, antes de borrarla: vuelve al cuerpo exacto de B-01 (con el gate de
            // la línea Inbound). Con el relleno de arriba, no amplía nada respecto de lo que daba esta migración.
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
