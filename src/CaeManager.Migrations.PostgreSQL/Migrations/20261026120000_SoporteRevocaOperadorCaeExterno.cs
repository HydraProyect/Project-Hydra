using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Petición del propietario del 2026-10-26 (enmienda de ADR-004 § 12.2): Soporte TALVEG revoca
    /// la Delegación de un Operador CAE externo a petición del Tenant propietario, con Sesión
    /// Privilegiada y auditoría. Segunda capacidad de acción acotada de ADR-011 § 8.7, punto 3
    /// (<c>RevocacionOperadorCaeExterno</c>), con el mismo régimen que
    /// <c>app_restablecer_segundo_factor_por_soporte</c>.
    ///
    /// <para>
    /// La conexión de la sesión adopta <c>cae_app_soporte</c> (solo <c>SELECT</c>) y no lo pierde:
    /// esta función es su única puerta de escritura sobre <c>DelegacionesTenant</c>,
    /// <c>AsignacionesOperacion</c> y <c>AsignacionesCartera</c>. Ninguna política de RLS se
    /// ensancha y ningún <c>GRANT</c> de tabla cambia. Se acota sola, con lo que la conexión no
    /// puede elegir:
    /// <list type="bullet">
    /// <item>Contexto: el contexto RLS firmado (<c>app_ctx_valido</c>). Actor =
    /// <c>app_ctx_usuario_id()</c>, Tenant propietario = <c>app_ctx_tenant_id()</c>.</item>
    /// <item>Motivo: no vacío y de 500 caracteres como mucho.</item>
    /// <item>Sesión: la que se nombra, abierta, dentro de su ventana, sin simulación, sobre el
    /// Tenant del contexto, del actor del contexto, y amparada por una concesión vigente de esta
    /// capacidad, por Tenant, que alcanza ese Tenant y que concedió otra persona.</item>
    /// <item>Actor: cuenta de un Tenant de plataforma (<c>EsPlataforma</c> aquí solo restringe).</item>
    /// <item>Delegación: del Tenant del contexto como Tenant propietario, de propósito
    /// <c>OperadorExterno</c> y activa. Una de Soporte no se revoca por aquí.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Efectos, los mismos que <c>DesactivarDelegacionTenantCommand</c> con
    /// <c>AsignacionesOperativasWriter.CerrarOperacionDelegadaAsync</c>: la Delegación queda
    /// inactiva (<c>DelegacionTenant.Desactivar</c>), la Asignación de Operación externa vigente
    /// del par se cierra como <c>Revocada</c> y sus carteras vigentes se cierran en cascada con el
    /// mismo instante, apagando la marca de principal y recordándola en
    /// <c>EraPrincipalAlCerrarsePorCascada</c> (<c>AsignacionCartera.CerrarPorCascadaDeLaOperacion</c>),
    /// que es lo que la reactivación normal necesita para devolverla (D-9). Es una copia en SQL de
    /// esos efectos, como la de <c>app_restablecer_segundo_factor_por_soporte</c> con P0-8: si
    /// cambia el cierre en C#, cambia aquí. <c>SoporteRevocaOperadorCaeExternoBajoRuntimeTests</c>
    /// fija el estado que la reactivación normal lee (motivo, marca recordada, mismo instante).
    /// </para>
    ///
    /// <para>
    /// Auditoría en la misma transacción, en el Tenant propietario (lo ve en su registro de
    /// actividad, como el restablecimiento de 2FA por Soporte TALVEG): una fila por la Delegación,
    /// otra por la operación y una por cartera, con Actor real = técnico de Soporte TALVEG,
    /// <c>ViaAcceso = SesionPrivilegiada</c>, la sesión en <c>ViaAccesoId</c> y el motivo y el
    /// Operador CAE externo en la fila de la Delegación.
    /// </para>
    ///
    /// <para>
    /// La política <c>privilegio_del_usuario</c> de <c>ConcesionesPrivilegio</c> amplía su lista de
    /// capacidades que un AdminPlataforma concede a otro (mismo cambio que hizo
    /// <c>RestablecimientoSegundoFactor</c>), con las mismas condiciones.
    /// </para>
    /// </summary>
    public partial class SoporteRevocaOperadorCaeExterno : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER POLICY privilegio_del_usuario ON public."ConcesionesPrivilegio"
                    WITH CHECK ((("UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))
                        OR (("ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))
                            AND ("EsAlcanceGlobal" = false)
                            AND ("Capacidad" IN ('Aprovisionamiento', 'RestablecimientoSegundoFactor', 'RevocacionOperadorCaeExterno'))
                            AND public.app_es_admin_plataforma((SELECT public.app_ctx_usuario_id())))));

                CREATE FUNCTION public.app_revocar_operador_cae_externo_por_soporte(p_sesion uuid, p_delegacion uuid, p_motivo text) RETURNS text
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                  v_actor uuid;
                  v_tenant uuid;
                  v_motivo text;
                  v_operador uuid;
                  v_activa boolean;
                  v_expira timestamp with time zone;
                  v_motivo_activacion text;
                  v_operacion uuid;
                  v_ahora timestamp with time zone := now();
                BEGIN
                  IF NOT public.app_ctx_valido() THEN
                    RETURN 'contexto_no_valido';
                  END IF;
                  v_actor := public.app_ctx_usuario_id();
                  v_tenant := public.app_ctx_tenant_id();
                  IF v_actor IS NULL OR v_tenant IS NULL OR p_sesion IS NULL OR p_delegacion IS NULL THEN
                    RETURN 'contexto_no_valido';
                  END IF;

                  v_motivo := btrim(p_motivo);
                  IF v_motivo IS NULL OR v_motivo = '' OR char_length(v_motivo) > 500 THEN
                    RETURN 'motivo_no_valido';
                  END IF;

                  -- La sesión: abierta, sin simular a nadie, sobre el Tenant del contexto, del
                  -- actor del contexto, y amparada por una concesión vigente de esta capacidad,
                  -- por Tenant, que alcanza ese Tenant y que concedió otra persona.
                  IF NOT EXISTS (
                      SELECT 1
                      FROM public."SesionesPrivilegiadas" s
                      JOIN public."ConcesionesPrivilegio" c ON c."Id" = s."ConcesionPrivilegioId"
                      WHERE s."Id" = p_sesion
                        AND s."TenantObjetivoId" = v_tenant
                        AND s."UsuarioSimuladoId" IS NULL
                        AND s."CerradaEnUtc" IS NULL
                        AND s."InicioEnUtc" <= v_ahora
                        AND s."ExpiraEnUtc" > v_ahora
                        AND c."UsuarioPlataformaId" = v_actor
                        AND c."Capacidad" = 'RevocacionOperadorCaeExterno'
                        AND c."Estado" = 'Vigente'
                        AND c."EsAlcanceGlobal" = false
                        AND c."VigenciaDesde" <= v_ahora
                        AND (c."VigenciaHasta" IS NULL OR c."VigenciaHasta" > v_ahora)
                        AND c."ConcedidaPorUsuarioId" IS NOT NULL
                        AND c."ConcedidaPorUsuarioId" <> c."UsuarioPlataformaId"
                        AND EXISTS (
                            SELECT 1 FROM public."TenantsAlcanzadosPorConcesion" t
                            WHERE t."ConcesionPrivilegioId" = c."Id" AND t."TenantId" = v_tenant))
                  THEN
                    RETURN 'sesion_no_autorizada';
                  END IF;

                  -- Quien la ejerce es un Actor de Plataforma TALVEG: su cuenta es de un Tenant
                  -- de plataforma. EsPlataforma aquí solo restringe, no concede nada.
                  IF NOT EXISTS (
                      SELECT 1 FROM public."AspNetUsers" a
                      JOIN public."Tenants" tp ON tp."Id" = a."TenantId"
                      WHERE a."Id" = v_actor AND tp."EsPlataforma")
                  THEN
                    RETURN 'sesion_no_autorizada';
                  END IF;

                  -- La Delegación: del Tenant del contexto como Tenant propietario y de un
                  -- Operador CAE externo. Inexistente, de otro Tenant o de Soporte, el mismo código.
                  SELECT d."TenantConsultoraId", d."Activa", d."ExpiraEnUtc", d."MotivoActivacion"
                    INTO v_operador, v_activa, v_expira, v_motivo_activacion
                  FROM public."DelegacionesTenant" d
                  WHERE d."Id" = p_delegacion
                    AND d."TenantClienteId" = v_tenant
                    AND d."Proposito" = 'OperadorExterno'
                  FOR UPDATE;
                  IF NOT FOUND THEN
                    RETURN 'delegacion_no_encontrada';
                  END IF;
                  IF NOT v_activa THEN
                    RETURN 'ya_inactiva';
                  END IF;

                  -- DelegacionTenant.Desactivar.
                  UPDATE public."DelegacionesTenant"
                  SET "Activa" = false, "ExpiraEnUtc" = NULL, "MotivoActivacion" = NULL
                  WHERE "Id" = p_delegacion;

                  INSERT INTO public."RegistrosAuditoria"
                    ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues",
                     "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId", "TipoActor", "FechaUtc")
                  VALUES
                    (uuidv7(), v_tenant, 'DelegacionTenant', p_delegacion, 'Modificado',
                     json_build_object('Activa', true, 'ExpiraEnUtc', v_expira, 'MotivoActivacion', v_motivo_activacion)::text,
                     json_build_object('Activa', false, 'ExpiraEnUtc', NULL, 'MotivoActivacion', NULL,
                                       'OperadorTenantId', v_operador, 'MotivoRevocacionPorSoporte', v_motivo)::text,
                     v_actor, v_actor, 'SesionPrivilegiada', p_sesion, 'Persona', v_ahora);

                  -- AsignacionesOperativasWriter.CerrarOperacionDelegadaAsync: la operación externa
                  -- vigente del par, de ámbito universal, y sus carteras vigentes en cascada.
                  SELECT o."Id" INTO v_operacion
                  FROM public."AsignacionesOperacion" o
                  WHERE NOT o."EsRaiz"
                    AND o."PropietarioTenantId" = v_tenant
                    AND o."OperadorTenantId" = v_operador
                    AND o."Servicio" = 'Outbound'
                    AND o."AmbitoRelacionClienteId" IS NULL
                    AND o."AmbitoCentroId" IS NULL
                    AND o."AmbitoTrabajadorId" IS NULL
                    AND o."AmbitoProyectoId" IS NULL
                    AND o."Estado" = 'Vigente'
                  FOR UPDATE;

                  IF FOUND THEN
                    -- AsignacionCartera.CerrarPorCascadaDeLaOperacion: cierra, apaga la marca de
                    -- principal y recuerda si la llevaba. Mismo instante que la operación.
                    WITH cerradas AS (
                      UPDATE public."AsignacionesCartera" c
                      SET "EraPrincipalAlCerrarsePorCascada" = c."EsPrincipal",
                          "EsPrincipal" = false,
                          "Estado" = 'Cerrada',
                          "MotivoCierre" = 'Revocada',
                          "VigenciaHasta" = CASE
                              WHEN c."VigenciaHasta" IS NULL OR c."VigenciaHasta" > v_ahora
                                THEN GREATEST(v_ahora, c."VigenciaDesde")
                              ELSE c."VigenciaHasta" END,
                          "Version" = uuidv7()
                      WHERE c."AsignacionOperacionId" = v_operacion
                        AND c."Estado" = 'Vigente'
                      RETURNING c."Id", c."EraPrincipalAlCerrarsePorCascada" AS era, c."VigenciaHasta" AS hasta)
                    INSERT INTO public."RegistrosAuditoria"
                      ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues",
                       "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId", "TipoActor", "FechaUtc")
                    SELECT uuidv7(), v_tenant, 'AsignacionCartera', x."Id", 'Modificado',
                           json_build_object('Estado', 'Vigente', 'EsPrincipal', x.era)::text,
                           json_build_object('Estado', 'Cerrada', 'MotivoCierre', 'Revocada', 'EsPrincipal', false,
                                             'EraPrincipalAlCerrarsePorCascada', x.era, 'VigenciaHasta', x.hasta)::text,
                           v_actor, v_actor, 'SesionPrivilegiada', p_sesion, 'Persona', v_ahora
                    FROM cerradas x;

                    -- AsignacionResponsabilidad.Cerrar sobre la operación.
                    UPDATE public."AsignacionesOperacion" o
                    SET "Estado" = 'Cerrada',
                        "MotivoCierre" = 'Revocada',
                        "VigenciaHasta" = CASE
                            WHEN o."VigenciaHasta" IS NULL OR o."VigenciaHasta" > v_ahora
                              THEN GREATEST(v_ahora, o."VigenciaDesde")
                            ELSE o."VigenciaHasta" END,
                        "Version" = uuidv7()
                    WHERE o."Id" = v_operacion;

                    INSERT INTO public."RegistrosAuditoria"
                      ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues",
                       "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId", "TipoActor", "FechaUtc")
                    SELECT uuidv7(), v_tenant, 'AsignacionOperacion', o."Id", 'Modificado',
                           json_build_object('Estado', 'Vigente')::text,
                           json_build_object('Estado', 'Cerrada', 'MotivoCierre', 'Revocada', 'VigenciaHasta', o."VigenciaHasta")::text,
                           v_actor, v_actor, 'SesionPrivilegiada', p_sesion, 'Persona', v_ahora
                    FROM public."AsignacionesOperacion" o
                    WHERE o."Id" = v_operacion;
                  END IF;

                  RETURN 'revocada';
                END;
                $$;

                REVOKE ALL ON FUNCTION public.app_revocar_operador_cae_externo_por_soporte(uuid, uuid, text) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.app_revocar_operador_cae_externo_por_soporte(uuid, uuid, text) TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP FUNCTION public.app_revocar_operador_cae_externo_por_soporte(uuid, uuid, text);

                ALTER POLICY privilegio_del_usuario ON public."ConcesionesPrivilegio"
                    WITH CHECK ((("UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))
                        OR (("ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))
                            AND ("EsAlcanceGlobal" = false)
                            AND ("Capacidad" IN ('Aprovisionamiento', 'RestablecimientoSegundoFactor'))
                            AND public.app_es_admin_plataforma((SELECT public.app_ctx_usuario_id())))));
                """);
        }
    }
}
