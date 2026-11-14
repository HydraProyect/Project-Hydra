using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Decisión de Chris del 2026-11-14 (hallazgo de Codex en la revisión de la PR #2215): revocar
    /// la Delegación de un Operador CAE externo cierra <b>toda</b> la autorización Outbound que
    /// deriva de ella sobre el Tenant propietario. <c>app_revocar_operador_cae_externo_por_soporte</c>
    /// (20261026120000_SoporteRevocaOperadorCaeExterno) cerraba solo la Asignación de Operación
    /// Outbound vigente del Tenant entero; una acotada a un Cliente empresarial, o una Programada que
    /// el job activa al llegar su fecha, seguía autorizando por Operación con sus carteras.
    ///
    /// <para>
    /// La función se reescribe con los mismos efectos que
    /// <c>AsignacionesOperativasWriter.CerrarOperacionDelegadaAsync</c>, del que es copia en SQL: todas
    /// las Asignaciones de Operación Outbound externas no cerradas del par (Tenant entero y acotadas;
    /// Vigente, Programada o Suspendida) se cierran como <c>Revocada</c>, y sus carteras no cerradas
    /// también. Las vigentes, por la cascada (apagan la marca de principal y la recuerdan en
    /// <c>EraPrincipalAlCerrarsePorCascada</c>, D-9); el resto, sin recordarla. Todo con el mismo
    /// instante. Solo Outbound: una operación Inbound del mismo Operador CAE externo queda intacta.
    /// </para>
    ///
    /// <para>
    /// Las precondiciones (contexto firmado, motivo, sesión, concesión, actor de un Tenant de
    /// plataforma, Delegación del Tenant del contexto, de Operador externo y activa), la auditoría y
    /// los permisos (<c>SECURITY DEFINER</c>, solo <c>EXECUTE</c> para <c>cae_app_soporte</c>) no
    /// cambian. La auditoría deja ahora una fila por cada operación y cada cartera que cierra, con su
    /// estado anterior. El <c>Down</c> restaura el cuerpo de 20261026120000.
    /// </para>
    /// </summary>
    public partial class SoporteRevocaOperacionesOutboundAcotadas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION public.app_revocar_operador_cae_externo_por_soporte(p_sesion uuid, p_delegacion uuid, p_motivo text) RETURNS text
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
                  v_operacion record;
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

                  -- AsignacionesOperativasWriter.CerrarOperacionDelegadaAsync: TODAS las operaciones
                  -- externas Outbound no cerradas del par, del Tenant entero y acotadas a un Cliente
                  -- empresarial, en cualquier estado no cerrado, y sus carteras no cerradas.
                  FOR v_operacion IN
                    SELECT o."Id", o."Estado"
                    FROM public."AsignacionesOperacion" o
                    WHERE NOT o."EsRaiz"
                      AND o."PropietarioTenantId" = v_tenant
                      AND o."OperadorTenantId" = v_operador
                      AND o."Servicio" = 'Outbound'
                      AND o."Estado" <> 'Cerrada'
                    ORDER BY o."Id"
                    FOR UPDATE
                  LOOP
                    -- Las vigentes, AsignacionCartera.CerrarPorCascadaDeLaOperacion: cierra, apaga la
                    -- marca de principal y recuerda si la llevaba. Las programadas o suspendidas,
                    -- AsignacionCartera.Cerrar: cierra y apaga la marca sin recordarla. Mismo
                    -- instante que la operación.
                    WITH previas AS (
                      SELECT c."Id", c."Estado" AS estado_antes, c."EsPrincipal" AS principal_antes
                      FROM public."AsignacionesCartera" c
                      WHERE c."AsignacionOperacionId" = v_operacion."Id"
                        AND c."Estado" <> 'Cerrada'),
                    cerradas AS (
                      UPDATE public."AsignacionesCartera" c
                      SET "EraPrincipalAlCerrarsePorCascada" = (p.estado_antes = 'Vigente' AND c."EsPrincipal"),
                          "EsPrincipal" = false,
                          "Estado" = 'Cerrada',
                          "MotivoCierre" = 'Revocada',
                          "VigenciaHasta" = CASE
                              WHEN c."VigenciaHasta" IS NULL OR c."VigenciaHasta" > v_ahora
                                THEN GREATEST(v_ahora, c."VigenciaDesde")
                              ELSE c."VigenciaHasta" END,
                          "Version" = uuidv7()
                      FROM previas p
                      WHERE c."Id" = p."Id"
                      RETURNING c."Id", p.estado_antes, p.principal_antes,
                                c."EraPrincipalAlCerrarsePorCascada" AS era, c."VigenciaHasta" AS hasta)
                    INSERT INTO public."RegistrosAuditoria"
                      ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues",
                       "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId", "TipoActor", "FechaUtc")
                    SELECT uuidv7(), v_tenant, 'AsignacionCartera', x."Id", 'Modificado',
                           json_build_object('Estado', x.estado_antes, 'EsPrincipal', x.principal_antes)::text,
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
                    WHERE o."Id" = v_operacion."Id";

                    INSERT INTO public."RegistrosAuditoria"
                      ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues",
                       "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId", "TipoActor", "FechaUtc")
                    SELECT uuidv7(), v_tenant, 'AsignacionOperacion', o."Id", 'Modificado',
                           json_build_object('Estado', v_operacion."Estado")::text,
                           json_build_object('Estado', 'Cerrada', 'MotivoCierre', 'Revocada', 'VigenciaHasta', o."VigenciaHasta")::text,
                           v_actor, v_actor, 'SesionPrivilegiada', p_sesion, 'Persona', v_ahora
                    FROM public."AsignacionesOperacion" o
                    WHERE o."Id" = v_operacion."Id";
                  END LOOP;

                  RETURN 'revocada';
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION public.app_revocar_operador_cae_externo_por_soporte(p_sesion uuid, p_delegacion uuid, p_motivo text) RETURNS text
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

                  IF NOT EXISTS (
                      SELECT 1 FROM public."AspNetUsers" a
                      JOIN public."Tenants" tp ON tp."Id" = a."TenantId"
                      WHERE a."Id" = v_actor AND tp."EsPlataforma")
                  THEN
                    RETURN 'sesion_no_autorizada';
                  END IF;

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
                """);
        }
    }
}
