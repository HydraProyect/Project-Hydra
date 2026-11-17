using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Demo Inbound, P0b (lectura inversa X-2): <c>app_inbound_tec_veredictos()</c>. El Tenant TEC del contexto firmado lee
    /// el veredicto que el Tenant TCE dio sobre sus propios Documentos: el estado de cada <c>EvidenciasInbound</c> (Aceptada o
    /// Rechazada), con su causa, su motivo y su fecha, el contrato de acceso y el Documento de origen. La evidencia vive en el
    /// Tenant TCE, que es su Tenant propietario, y la RLS <c>aislamiento_tenant</c> no deja al Tenant TEC ver nada de ella.
    /// <para>
    /// Qué filas: solo evidencias cuyo origen es el Tenant TEC del contexto (<c>"TenantOrigenId" = app_ctx_tenant_id()</c>,
    /// sus propios Documentos), no eliminadas, y solo por un contrato de acceso que pasa el mismo gate D10 que
    /// <c>app_inbound_tec_requerimientos()</c> (P0a, <c>AnadeLecturaRequerimientosTec</c>), con el texto idéntico: estado
    /// Activo sin cierre real, no eliminado, vinculado al Tenant TEC del contexto (<c>"TenantContratistaId"</c>), dentro de sus
    /// fechas el día de negocio (Europe/Madrid, bordes incluidos) y con la línea Inbound vigente del Tenant TCE según
    /// <c>clock_timestamp()</c>, por eso es <c>VOLATILE</c>. Las dos condiciones sobre el Tenant TEC se exigen a la vez: una
    /// evidencia de otro origen colgada de un contrato vinculado no sale, ni una propia por un contrato que ya no lo está.
    /// </para>
    /// <para>
    /// Qué columnas (P-4 y Q-18): ninguna identifica a un Gestor CAE ni a ninguna otra persona. Ni
    /// <c>"ValidadaPorUsuarioId"</c> ni <c>"RechazadaPorUsuarioId"</c>, ni el sujeto, ni el archivo copiado, ni su huella, ni
    /// la conclusión de aptitud. La fecha es la del veredicto vigente: la del rechazo si está Rechazada y la de la última
    /// validación si está Aceptada. La causa y el motivo salen solo con Rechazada: tras volver a validar, el último rechazo se
    /// conserva en la evidencia como hecho pasado, pero ya no es el veredicto.
    /// </para>
    /// <para>
    /// Salud (art. 9 RGPD): una evidencia de salud, porque el tipo del Tenant TCE es de categoría especial de salud
    /// (<c>TipoDocumento.RevelaSalud</c>) o porque llegó como solo conclusión de aptitud (el mismo criterio que
    /// <c>ObtenerAptosHoyDelCentroQuery</c>), da solo el estado y la fecha: ni la causa ni el motivo, que es texto libre y
    /// puede llevar el motivo clínico. La conclusión no sale nunca, de ninguna evidencia. La causa y el motivo solo salen
    /// cuando la base sabe que la evidencia no es de salud, así que un dato que falte deja la fila callada en vez de abierta.
    /// </para>
    /// <para>
    /// Mismo contrato que <c>app_inbound_tec_requerimientos()</c> y las funciones públicas <c>app_inbound_*</c>: plpgsql
    /// <c>SECURITY DEFINER</c> con el propietario de las migraciones, <c>search_path</c> fijo y nombres cualificados; sin
    /// contexto firmado con Tenant y usuario lanza 42501, para que «sin contexto» no se confunda con «sin veredictos»; si su
    /// propietario no evita la RLS, falla cerrada en vez de devolver vacío. El Tenant TEC sale de <c>app_ctx_tenant_id()</c>,
    /// nunca de un parámetro ni de la GUC suelta. Sin traza: no lee Documentos ni datos personales del Tenant TCE, solo el
    /// veredicto sobre lo que ya es del Tenant TEC. <c>EXECUTE</c> solo para <c>cae_app_runtime</c>; Soporte TALVEG y
    /// aprovisionamiento no la ejecutan. No toca ninguna tabla ni ninguna política y es compatible con la ranura anterior,
    /// que no la llama.
    /// </para>
    /// </summary>
    public partial class AnadeLecturaVeredictosTec : Migration
    {
        public const string CreaFuncionVeredictosTec =
            """
            CREATE FUNCTION public.app_inbound_tec_veredictos()
                RETURNS TABLE("ContratoAccesoId" uuid, "DocumentoOrigenId" uuid, "Estado" text, "CausaRechazo" text,
                              "MotivoRechazo" text, "FechaVeredictoUtc" timestamp with time zone)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            #variable_conflict use_column
            DECLARE
                v_tec uuid;
                v_hoy date;
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: la lectura Inbound no puede comprobar los datos del Tenant propietario', current_user;
                END IF;
                IF NOT COALESCE(public.app_ctx_valido(), false) OR public.app_ctx_tenant_id() IS NULL OR public.app_ctx_usuario_id() IS NULL THEN
                    RAISE EXCEPTION 'Sin contexto firmado con Tenant y usuario: no se lee nada de otro Tenant.' USING ERRCODE = '42501';
                END IF;
                v_tec := public.app_ctx_tenant_id();
                v_hoy := (pg_catalog.now() AT TIME ZONE 'Europe/Madrid')::date;
                RETURN QUERY
                    SELECT e."ContratoAccesoId", e."DocumentoOrigenId", e."Estado"::text,
                           CASE WHEN e."Estado" = 'Rechazada' AND NOT e."SoloConclusionAptitud" AND td."Sensibilidad" <> 'CategoriaEspecialSalud'
                                THEN e."CausaRechazo"::text END,
                           CASE WHEN e."Estado" = 'Rechazada' AND NOT e."SoloConclusionAptitud" AND td."Sensibilidad" <> 'CategoriaEspecialSalud'
                                THEN e."MotivoRechazo"::text END,
                           CASE WHEN e."Estado" = 'Rechazada' THEN e."RechazadaEnUtc" ELSE e."ValidadaEnUtc" END
                      FROM public."EvidenciasInbound" e
                      JOIN public."ContratosAcceso" c ON c."TenantId" = e."TenantId" AND c."Id" = e."ContratoAccesoId"
                      JOIN public."TiposDocumento" td ON td."TenantId" = e."TenantId" AND td."Id" = e."TipoDocumentoId"
                     WHERE e."TenantOrigenId" = v_tec
                       AND NOT e."EstaEliminado"
                       AND e."Estado" IN ('Aceptada', 'Rechazada')
                       AND c."TenantContratistaId" = v_tec
                       AND NOT c."EstaEliminado"
                       AND c."Estado" = 'Activo'
                       AND c."FechaCierreReal" IS NULL
                       AND c."FechaInicio" <= v_hoy
                       AND (c."FechaFin" IS NULL OR v_hoy <= c."FechaFin")
                       AND EXISTS (SELECT 1 FROM public."ProductosContratados" pc
                                    WHERE pc."TenantId" = c."TenantId" AND pc."Linea" = 'Inbound'
                                      AND pc."Estado" = 'Vigente' AND NOT pc."EstaEliminado"
                                      AND pc."VigenteDesdeUtc" <= pg_catalog.clock_timestamp())
                     ORDER BY e."ContratoAccesoId", e."DocumentoOrigenId";
            END;
            $$;
            REVOKE ALL ON FUNCTION public.app_inbound_tec_veredictos() FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_inbound_tec_veredictos() TO cae_app_runtime;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreaFuncionVeredictosTec);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.app_inbound_tec_veredictos();");
        }
    }
}
