using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RegistraAccesoDocumentoSensiblePorSoporte : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DEC-36 y REC-208: todo acceso al contenido de un Documento sensible queda registrado,
            // también el de Soporte TALVEG desde una Sesión Privilegiada (revisión extensa del
            // 2026-10-23, Z2-2). Esa conexión adopta cae_app_soporte, que solo tiene SELECT sobre
            // RegistrosAccesoDocumentoSensible y no lo pierde: esta función es la única puerta de
            // escritura de la sesión, igual que app_restablecer_segundo_factor_por_soporte.
            //
            // Se acota sola, con lo que la conexión no puede elegir:
            //   - Contexto: el contexto RLS firmado (app_ctx_valido). Actor = app_ctx_usuario_id(),
            //     Tenant = app_ctx_tenant_id(); sin contexto válido no escribe nada.
            //   - Sesión: la que se nombra, abierta (CerradaEnUtc nulo), dentro de su ventana, sobre
            //     el Tenant del contexto y con una concesión vigente del actor del contexto que
            //     todavía alcanza ese Tenant (global, o con el Tenant en TenantsAlcanzadosPorConcesion).
            //     Las mismas condiciones que SesionPrivilegiadaActual comprueba en la aplicación. No
            //     se filtra por capacidad: registrar un acceso no concede nada, y cualquier sesión
            //     que lea un documento sensible tiene que dejar rastro.
            //   - Fila: TenantId, UsuarioId, ActorRealUsuarioId, ViaAcceso, ViaAccesoId, TipoActor y
            //     OcurridoEnUtc los pone la función; del llamador solo vienen el Id del registro, el
            //     recurso accedido, la sensibilidad (nunca SinDatosPersonales: «nunca registrar de
            //     más») y el tipo de acceso. UsuarioId es el usuario simulado si la sesión simula a
            //     alguien, y si no el actor; ActorRealUsuarioId es siempre el actor.
            // El propietario de la función es el rol de las migraciones, que no está sujeto a RLS:
            // por eso el TenantId de la fila sale del contexto firmado y no de un parámetro.
            // Devuelve un código en vez de lanzar, para que el repositorio pueda fallar en cerrado
            // con el motivo en el registro. EXECUTE solo para cae_app_soporte.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION public.app_registrar_acceso_documento_sensible_por_soporte(
                    p_sesion uuid, p_registro uuid, p_documento uuid, p_sensibilidad text, p_tipo_acceso text) RETURNS text
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                  v_actor uuid;
                  v_tenant uuid;
                  v_simulado uuid;
                BEGIN
                  IF NOT public.app_ctx_valido() THEN
                    RETURN 'contexto_no_valido';
                  END IF;
                  v_actor := public.app_ctx_usuario_id();
                  v_tenant := public.app_ctx_tenant_id();
                  IF v_actor IS NULL OR v_tenant IS NULL OR p_sesion IS NULL THEN
                    RETURN 'contexto_no_valido';
                  END IF;

                  IF p_registro IS NULL OR p_documento IS NULL
                     OR p_sensibilidad IS NULL OR p_sensibilidad NOT IN ('DatosPersonales', 'CategoriaEspecialSalud')
                     OR p_tipo_acceso IS NULL OR p_tipo_acceso NOT IN ('Apertura', 'VersionAnterior') THEN
                    RETURN 'valor_no_valido';
                  END IF;

                  SELECT s."UsuarioSimuladoId" INTO v_simulado
                  FROM public."SesionesPrivilegiadas" s
                  JOIN public."ConcesionesPrivilegio" c ON c."Id" = s."ConcesionPrivilegioId"
                  WHERE s."Id" = p_sesion
                    AND s."TenantObjetivoId" = v_tenant
                    AND s."CerradaEnUtc" IS NULL
                    AND s."InicioEnUtc" <= now()
                    AND s."ExpiraEnUtc" > now()
                    AND c."UsuarioPlataformaId" = v_actor
                    AND c."Estado" = 'Vigente'
                    AND c."VigenciaDesde" <= now()
                    AND (c."VigenciaHasta" IS NULL OR c."VigenciaHasta" > now())
                    AND (c."EsAlcanceGlobal" OR EXISTS (
                        SELECT 1 FROM public."TenantsAlcanzadosPorConcesion" t
                        WHERE t."ConcesionPrivilegioId" = c."Id" AND t."TenantId" = v_tenant));
                  IF NOT FOUND THEN
                    RETURN 'sesion_no_autorizada';
                  END IF;

                  INSERT INTO public."RegistrosAccesoDocumentoSensible" (
                      "Id", "DocumentoId", "Sensibilidad", "TipoAcceso", "UsuarioId", "ActorRealUsuarioId",
                      "ViaAcceso", "ViaAccesoId", "OcurridoEnUtc", "TenantId", "TipoActor")
                  VALUES (
                      p_registro, p_documento, p_sensibilidad, p_tipo_acceso, COALESCE(v_simulado, v_actor), v_actor,
                      'SesionPrivilegiada', p_sesion, now(), v_tenant, 'Persona');

                  RETURN 'registrado';
                END;
                $$;

                REVOKE ALL ON FUNCTION public.app_registrar_acceso_documento_sensible_por_soporte(uuid, uuid, uuid, text, text) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.app_registrar_acceso_documento_sensible_por_soporte(uuid, uuid, uuid, text, text) TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP FUNCTION public.app_registrar_acceso_documento_sensible_por_soporte(uuid, uuid, uuid, text, text);
                """);
        }
    }
}
