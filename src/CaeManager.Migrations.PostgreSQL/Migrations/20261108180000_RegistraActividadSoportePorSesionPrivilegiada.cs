using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RegistraActividadSoportePorSesionPrivilegiada : Migration
    {
        /// <summary>Firma de la función, la misma en el GRANT, el REVOKE y el DROP.</summary>
        internal const string Firma = "public.app_registrar_actividad_soporte_por_sesion(uuid, uuid, text, text)";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Issue #2001 (decisión de Chris del 2026-11-03, al cerrar la #1971): toda lectura que un
            // Actor de Plataforma TALVEG hace bajo una Sesión Privilegiada deja una fila en
            // RegistrosActividadSoporte. Esa conexión adopta cae_app_soporte, que solo tiene SELECT
            // sobre la tabla y no lo pierde: esta función es su única puerta de escritura, igual que
            // app_registrar_acceso_documento_sensible_por_soporte para los documentos sensibles.
            //
            // Se acota sola, con lo que la conexión no puede elegir:
            //   - Contexto: el contexto RLS firmado (app_ctx_valido). Actor real = app_ctx_usuario_id(),
            //     Tenant = app_ctx_tenant_id(); sin contexto válido no escribe nada.
            //   - Sesión: la que se nombra, abierta (CerradaEnUtc nulo), dentro de su ventana, con el
            //     Tenant objetivo igual al Tenant del contexto, y con una concesión vigente cuyo actor
            //     es el actor real del contexto y que todavía alcanza ese Tenant (global, o con el
            //     Tenant en TenantsAlcanzadosPorConcesion). Las mismas condiciones que
            //     SesionPrivilegiadaActual comprueba en la aplicación. No se filtra por capacidad:
            //     registrar una lectura no concede nada.
            //   - Fila: TenantId (el Tenant objetivo), UsuarioSoporteId (el actor real, nunca el
            //     usuario simulado), SesionPrivilegiadaId y OcurridaEnUtc los pone la función;
            //     DelegacionTenantId queda nulo (CK_RegistrosActividadSoporte_UnSoloAgrupador). Del
            //     llamador solo vienen el Id del registro (UUID v7 de la aplicación), el tipo (solo
            //     'Lectura') y el detalle, que solo puede ser un identificador de request (letra inicial,
            //     letras, dígitos, '_' o '.', hasta 200): nunca texto libre ni datos leídos.
            //   - Hora: clock_timestamp(), no now(). now() es la hora de inicio de la transacción, y una
            //     transacción abierta antes de que la sesión o la concesión caducaran seguiría viéndolas
            //     vigentes; la misma hora fecha la fila.
            // El propietario de la función es el rol de las migraciones: por eso el TenantId de la
            // fila sale del contexto firmado y no de un parámetro. Devuelve un código en vez de lanzar,
            // para que el repositorio falle en cerrado con el motivo en el registro. EXECUTE solo para
            // cae_app_soporte; los GRANT de la tabla no cambian.
            migrationBuilder.Sql(
                $"""
                CREATE FUNCTION public.app_registrar_actividad_soporte_por_sesion(
                    p_sesion uuid, p_registro uuid, p_tipo text, p_detalle text) RETURNS text
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                  v_actor uuid;
                  v_tenant uuid;
                  v_ahora timestamptz := clock_timestamp();
                BEGIN
                  IF NOT public.app_ctx_valido() THEN
                    RETURN 'contexto_no_valido';
                  END IF;
                  v_actor := public.app_ctx_usuario_id();
                  v_tenant := public.app_ctx_tenant_id();
                  IF v_actor IS NULL OR v_tenant IS NULL OR p_sesion IS NULL THEN
                    RETURN 'contexto_no_valido';
                  END IF;

                  IF p_registro IS NULL OR p_tipo IS NULL OR p_tipo NOT IN ('Lectura')
                     OR p_detalle IS NULL OR length(p_detalle) > 200
                     OR p_detalle !~ '^[A-Za-z][A-Za-z0-9_.]*$' THEN
                    RETURN 'valor_no_valido';
                  END IF;

                  PERFORM 1
                  FROM public."SesionesPrivilegiadas" s
                  JOIN public."ConcesionesPrivilegio" c ON c."Id" = s."ConcesionPrivilegioId"
                  WHERE s."Id" = p_sesion
                    AND s."TenantObjetivoId" = v_tenant
                    AND s."CerradaEnUtc" IS NULL
                    AND s."InicioEnUtc" <= v_ahora
                    AND s."ExpiraEnUtc" > v_ahora
                    AND c."UsuarioPlataformaId" = v_actor
                    AND c."Estado" = 'Vigente'
                    AND c."VigenciaDesde" <= v_ahora
                    AND (c."VigenciaHasta" IS NULL OR c."VigenciaHasta" > v_ahora)
                    AND (c."EsAlcanceGlobal" OR EXISTS (
                        SELECT 1 FROM public."TenantsAlcanzadosPorConcesion" t
                        WHERE t."ConcesionPrivilegioId" = c."Id" AND t."TenantId" = v_tenant));
                  IF NOT FOUND THEN
                    RETURN 'sesion_no_autorizada';
                  END IF;

                  INSERT INTO public."RegistrosActividadSoporte" (
                      "Id", "UsuarioSoporteId", "Tipo", "DelegacionTenantId", "Detalle", "OcurridaEnUtc",
                      "TenantId", "SesionPrivilegiadaId")
                  VALUES (
                      p_registro, v_actor, p_tipo, NULL, p_detalle, v_ahora,
                      v_tenant, p_sesion);

                  RETURN 'registrado';
                END;
                $$;

                REVOKE ALL ON FUNCTION {Firma} FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION {Firma} TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"""
                DROP FUNCTION {Firma};
                """);
        }
    }
}
