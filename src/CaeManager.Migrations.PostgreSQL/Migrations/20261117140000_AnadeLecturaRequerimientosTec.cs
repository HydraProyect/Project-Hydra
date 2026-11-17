using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Demo Inbound, P0a (lectura inversa X-2): <c>app_inbound_tec_requerimientos()</c>. El Tenant TEC del contexto firmado
    /// lee los requisitos que le piden los Tenants TCE en sus contratos de acceso. Es el sentido contrario a
    /// <c>app_inbound_documentos_autorizados</c> (X-1): allí el Tenant TCE lee documentos del Tenant TEC; aquí el Tenant TEC
    /// lee requisitos que viven en el Tenant TCE, donde la RLS <c>aislamiento_tenant</c> no le deja ver nada.
    /// <para>
    /// Qué filas (decisiones D9 y D10 de Chris, 2026-11-17): solo contratos de acceso en estado Activo (el estado efectivo:
    /// <c>"Estado" = 'Activo'</c> sin cierre real, que <c>ContratoAcceso.EstadoEfectivo</c> trata como Finalizado), no
    /// eliminados, vinculados al Tenant TEC del contexto (<c>"TenantContratistaId" = app_ctx_tenant_id()</c>) y cuyo Tenant
    /// TCE tiene la línea Inbound vigente con el mismo criterio que el gate DN-7 (<c>GateInboundConRelojDeEvaluacion</c>):
    /// <c>Vigente</c>, no eliminada y con el inicio alcanzado según <c>clock_timestamp()</c>, por eso es <c>VOLATILE</c>.
    /// De cada contrato salen solo dos capas (D9): los requisitos activos de la plantilla activa del Centro del contrato
    /// (<c>DeCentro</c>) y los de la plantilla activa de exigencias de su Cliente empresarial (<c>ExigenciasClienteEmpresarial</c>,
    /// N3). No hay resolutor por capas (B-15): un mismo tipo de documento puede salir una vez por capa, y la columna
    /// <c>"CapaPlantilla"</c> dice de cuál viene.
    /// </para>
    /// <para>
    /// Qué columnas (P-4 y Q-18): ninguna identifica a un Gestor CAE ni a ninguna otra persona: ni usuarios, ni nombres de
    /// persona, ni correos. Del Tenant TCE solo su nombre visible; del Cliente empresarial, su razón social, y nunca su
    /// <c>"EjecutivoUsuarioId"</c>. El tipo de documento sale con su nombre exacto (D1), porque el Tenant TEC lo empareja con
    /// su catálogo.
    /// </para>
    /// <para>
    /// Mismo contrato que las funciones públicas <c>app_inbound_*</c> (<c>AnadeLecturaInboundEntreTenants</c> y
    /// <c>FalloCerradoLecturaInbound</c>): plpgsql <c>SECURITY DEFINER</c> con el propietario de las migraciones,
    /// <c>search_path</c> fijo y nombres cualificados; sin contexto firmado con Tenant y usuario lanza 42501, para que «sin
    /// contexto» no se confunda con «sin requisitos»; si su propietario no evita la RLS, falla cerrada en vez de devolver
    /// vacío. El Tenant TEC sale de <c>app_ctx_tenant_id()</c>, nunca de un parámetro ni de la GUC suelta. Sin traza: no lee
    /// datos personales ni Documentos. <c>EXECUTE</c> solo para <c>cae_app_runtime</c>; Soporte TALVEG y aprovisionamiento no
    /// la ejecutan. No toca ninguna tabla ni ninguna política y es compatible con la ranura anterior, que no la llama.
    /// </para>
    /// </summary>
    public partial class AnadeLecturaRequerimientosTec : Migration
    {
        public const string CreaFuncionRequerimientosTec =
            """
            CREATE FUNCTION public.app_inbound_tec_requerimientos()
                RETURNS TABLE("ContratoAccesoId" uuid, "TenantTceNombre" text, "CentroId" uuid, "CentroNombre" text,
                              "ClienteEmpresarialNombre" text, "RequisitoId" uuid, "TipoDocumentoId" uuid, "TipoDocumentoNombre" text,
                              "Entidad" text, "Criterio" text, "ModoVigencia" text, "VigenciaMeses" integer, "ToleranciaDias" integer,
                              "BloqueaAcceso" boolean, "AmbitoDocumento" text, "CapaPlantilla" text)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            #variable_conflict use_column
            DECLARE
                v_tec uuid;
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: la lectura Inbound no puede comprobar los datos del Tenant propietario', current_user;
                END IF;
                IF NOT COALESCE(public.app_ctx_valido(), false) OR public.app_ctx_tenant_id() IS NULL OR public.app_ctx_usuario_id() IS NULL THEN
                    RAISE EXCEPTION 'Sin contexto firmado con Tenant y usuario: no se lee nada de otro Tenant.' USING ERRCODE = '42501';
                END IF;
                v_tec := public.app_ctx_tenant_id();
                RETURN QUERY
                    SELECT c."Id", tn."Nombre"::text, ce."Id", ce."Nombre"::text, ec."RazonSocial"::text,
                           r."Id", r."TipoDocumentoId", td."Nombre"::text, r."Entidad"::text, r."Criterio"::text,
                           r."ModoVigencia"::text, r."VigenciaMeses", r."ToleranciaDias", r."BloqueaAcceso",
                           r."AmbitoDocumento"::text, p."Tipo"::text
                      FROM public."ContratosAcceso" c
                      JOIN public."Tenants" tn ON tn."Id" = c."TenantId"
                      JOIN public."Centros" ce ON ce."TenantId" = c."TenantId" AND ce."Id" = c."CentroId"
                      JOIN public."Empresas" ec ON ec."TenantId" = c."TenantId" AND ec."Id" = c."ClienteEmpresarialId"
                      JOIN public."PlantillasRequisitos" p
                        ON p."TenantId" = c."TenantId"
                       AND p."Activa" AND NOT p."EstaEliminado"
                       AND ((p."Tipo" = 'DeCentro' AND p."CentroId" = c."CentroId")
                            OR (p."Tipo" = 'ExigenciasClienteEmpresarial' AND p."ClienteEmpresarialId" = c."ClienteEmpresarialId"))
                      JOIN public."RequisitosPlantilla" r
                        ON r."TenantId" = p."TenantId" AND r."PlantillaId" = p."Id" AND r."Activo"
                      JOIN public."TiposDocumento" td ON td."TenantId" = r."TenantId" AND td."Id" = r."TipoDocumentoId"
                     WHERE c."TenantContratistaId" = v_tec
                       AND NOT c."EstaEliminado"
                       AND c."Estado" = 'Activo'
                       AND c."FechaCierreReal" IS NULL
                       AND EXISTS (SELECT 1 FROM public."ProductosContratados" pc
                                    WHERE pc."TenantId" = c."TenantId" AND pc."Linea" = 'Inbound'
                                      AND pc."Estado" = 'Vigente' AND NOT pc."EstaEliminado"
                                      AND pc."VigenteDesdeUtc" <= pg_catalog.clock_timestamp())
                     ORDER BY c."Id", p."Tipo", td."Nombre", r."Id";
            END;
            $$;
            REVOKE ALL ON FUNCTION public.app_inbound_tec_requerimientos() FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_inbound_tec_requerimientos() TO cae_app_runtime;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreaFuncionRequerimientosTec);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.app_inbound_tec_requerimientos();");
        }
    }
}
