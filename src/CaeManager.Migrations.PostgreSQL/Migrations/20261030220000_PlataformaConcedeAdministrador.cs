using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Decisión del 2026-10-30: un Actor de Plataforma TALVEG concede Administrador en un Tenant
    /// ya existente, con Sesión Privilegiada y auditoría, para que un Tenant no se quede sin
    /// Administrador ni forma de reponerlo. Tercera capacidad de acción acotada de ADR-011 § 8.7,
    /// punto 3 (<c>ConcesionAdministrador</c>), con el mismo régimen que
    /// <c>app_revocar_operador_cae_externo_por_soporte</c>.
    ///
    /// <para>
    /// La conexión de la sesión adopta <c>cae_app_soporte</c> (solo <c>SELECT</c>) y no lo pierde:
    /// esta función es su única puerta. Ninguna política se ensancha más allá de la lista de
    /// capacidades y ningún <c>GRANT</c> de tabla cambia. Se acota sola:
    /// <list type="bullet">
    /// <item>Contexto: el RLS firmado. Actor = <c>app_ctx_usuario_id()</c>, Tenant =
    /// <c>app_ctx_tenant_id()</c>.</item>
    /// <item>Motivo: no vacío y de 500 caracteres como mucho.</item>
    /// <item>Sesión: la que se nombra, abierta, dentro de su ventana, sin simulación, sobre el
    /// Tenant del contexto, del actor del contexto, y amparada por una concesión vigente de esta
    /// capacidad, por Tenant, que alcanza ese Tenant y que concedió otra persona.</item>
    /// <item>Actor: cuenta de un Tenant de plataforma. Y el Tenant objetivo no lo es: esta vía no
    /// crea Administradores de plataforma.</item>
    /// <item>Cuenta: del Tenant del contexto y no desactivada. Sirve la que sigue sin activar:
    /// reponer el Administrador no exige que la cuenta haya entrado alguna vez.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Efectos, los de <c>EditarUsuarioCommand</c> con <c>CambiarRolAsync</c>: la cuenta se queda
    /// con Administrador como único rol, sin Coordinador CAE ni Cliente vinculado, y con sello
    /// nuevo, de modo que su sesión abierta no conserva el rol anterior. Fila y luego candado de
    /// Administradores del Tenant, el orden de <c>app_serializar_alta_de_administrador</c>. Es una
    /// copia en SQL: si cambia ese camino en C#, cambia aquí.
    /// </para>
    ///
    /// <para>
    /// Auditoría en la misma transacción y en el Tenant afectado, que la ve en su registro de
    /// actividad: Actor real = el Actor de Plataforma TALVEG, <c>ViaAcceso = SesionPrivilegiada</c>,
    /// la sesión en <c>ViaAccesoId</c>, el rol anterior y el motivo.
    /// </para>
    /// </summary>
    public partial class PlataformaConcedeAdministrador : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER POLICY privilegio_concesion_a_tercero ON public."ConcesionesPrivilegio"
                    WITH CHECK ((("ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))
                        AND ("UsuarioPlataformaId" <> (SELECT public.app_ctx_usuario_id()))
                        AND (("Estado")::text = 'Vigente'::text)
                        AND ("EsAlcanceGlobal" = false)
                        AND (("Origen")::text = 'Ordinaria'::text)
                        AND ("VigenciaHasta" IS NOT NULL)
                        AND (("Capacidad")::text IN ('Aprovisionamiento', 'RestablecimientoSegundoFactor', 'RevocacionOperadorCaeExterno', 'ConcesionAdministrador'))
                        AND public.app_es_admin_plataforma((SELECT public.app_ctx_usuario_id()))));

                ALTER POLICY privilegio_del_usuario ON public."ConcesionesPrivilegio"
                    WITH CHECK ((("UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))
                        OR (("ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))
                            AND ("EsAlcanceGlobal" = false)
                            AND ("Capacidad" IN ('Aprovisionamiento', 'RestablecimientoSegundoFactor', 'RevocacionOperadorCaeExterno', 'ConcesionAdministrador'))
                            AND public.app_es_admin_plataforma((SELECT public.app_ctx_usuario_id())))));

                ALTER POLICY sesion_privilegiada_apertura ON public."SesionesPrivilegiadas"
                    WITH CHECK ((("CerradaEnUtc" IS NULL)
                        AND ("UsuarioSimuladoId" IS NULL)
                        AND ("InicioEnUtc" >= (now() - '00:05:00'::interval))
                        AND ("InicioEnUtc" <= (now() + '00:05:00'::interval))
                        AND ("ExpiraEnUtc" > "InicioEnUtc")
                        AND ("ExpiraEnUtc" <= ("InicioEnUtc" + '04:00:00'::interval))
                        AND (EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
                            WHERE ((c."Id" = "SesionesPrivilegiadas"."ConcesionPrivilegioId")
                                AND (c."UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))
                                AND ((c."Estado")::text = 'Vigente'::text)
                                AND (c."VigenciaDesde" <= now())
                                AND ((c."VigenciaHasta" IS NULL) OR (now() < c."VigenciaHasta"))
                                AND ((c."Capacidad")::text IN ('SoporteLectura', 'Aprovisionamiento', 'RestablecimientoSegundoFactor', 'RevocacionOperadorCaeExterno', 'ConcesionAdministrador'))
                                AND (c."EsAlcanceGlobal"
                                    OR (EXISTS (SELECT 1 FROM public."TenantsAlcanzadosPorConcesion" t
                                        WHERE ((t."ConcesionPrivilegioId" = c."Id")
                                            AND (t."TenantId" = "SesionesPrivilegiadas"."TenantObjetivoId"))))))))));

                CREATE FUNCTION public.app_conceder_administrador_por_plataforma(p_sesion uuid, p_usuario uuid, p_motivo text) RETURNS text
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                  v_actor uuid;
                  v_tenant uuid;
                  v_motivo text;
                  v_rol_administrador uuid;
                  v_roles_antes text;
                  v_ahora timestamp with time zone := now();
                BEGIN
                  IF NOT public.app_ctx_valido() THEN
                    RETURN 'contexto_no_valido';
                  END IF;
                  v_actor := public.app_ctx_usuario_id();
                  v_tenant := public.app_ctx_tenant_id();
                  IF v_actor IS NULL OR v_tenant IS NULL OR p_sesion IS NULL OR p_usuario IS NULL THEN
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
                        AND c."Capacidad" = 'ConcesionAdministrador'
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

                  -- Quien la ejerce es un Actor de Plataforma TALVEG, y el Tenant objetivo no es
                  -- de plataforma. EsPlataforma aquí solo restringe, no concede nada.
                  IF NOT EXISTS (
                      SELECT 1 FROM public."AspNetUsers" a
                      JOIN public."Tenants" tp ON tp."Id" = a."TenantId"
                      WHERE a."Id" = v_actor AND tp."EsPlataforma")
                    OR EXISTS (
                      SELECT 1 FROM public."Tenants" t WHERE t."Id" = v_tenant AND t."EsPlataforma")
                  THEN
                    RETURN 'sesion_no_autorizada';
                  END IF;

                  -- La cuenta: del Tenant del contexto y no desactivada (mismo umbral que
                  -- ApplicationUser.UmbralDeCuentaDesactivada). Inexistente, de otro Tenant o
                  -- desactivada, el mismo código.
                  PERFORM 1
                  FROM public."AspNetUsers" u
                  WHERE u."Id" = p_usuario
                    AND u."TenantId" = v_tenant
                    AND (u."LockoutEnd" IS NULL OR u."LockoutEnd" <= v_ahora + interval '365 days')
                  FOR UPDATE;
                  IF NOT FOUND THEN
                    RETURN 'cuenta_no_encontrada';
                  END IF;

                  -- Fila y luego candado, como app_serializar_alta_de_administrador, y los roles
                  -- se leen ya bajo él.
                  PERFORM public.app_bloquear_administradores_de_tenant(v_tenant);

                  SELECT r."Id" INTO v_rol_administrador
                  FROM public."AspNetRoles" r WHERE r."NormalizedName" = 'ADMINISTRADOR';
                  IF NOT FOUND THEN
                    RETURN 'rol_no_encontrado';
                  END IF;

                  IF EXISTS (
                      SELECT 1 FROM public."AspNetUserRoles" ur
                      WHERE ur."UserId" = p_usuario AND ur."RoleId" = v_rol_administrador)
                  THEN
                    RETURN 'ya_es_administrador';
                  END IF;

                  SELECT string_agg(r."Name", ',' ORDER BY r."Name") INTO v_roles_antes
                  FROM public."AspNetUserRoles" ur
                  JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
                  WHERE ur."UserId" = p_usuario;

                  -- GestionCuentasUsuarioIdentity.CambiarRolAsync y los datos de EditarUsuarioCommand:
                  -- un único rol, sin vínculos de otro rol, y sello nuevo en la misma transacción.
                  DELETE FROM public."AspNetUserRoles" WHERE "UserId" = p_usuario;
                  INSERT INTO public."AspNetUserRoles" ("UserId", "RoleId") VALUES (p_usuario, v_rol_administrador);

                  UPDATE public."AspNetUsers"
                  SET "CoordinadorUsuarioId" = NULL,
                      "ClienteId" = NULL,
                      "SecurityStamp" = upper(replace(gen_random_uuid()::text, '-', '')),
                      "ConcurrencyStamp" = gen_random_uuid()::text
                  WHERE "Id" = p_usuario;

                  INSERT INTO public."RegistrosAuditoria"
                    ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues",
                     "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId", "TipoActor", "FechaUtc")
                  VALUES
                    (uuidv7(), v_tenant, 'Usuario', p_usuario, 'Modificado',
                     json_build_object('Roles', v_roles_antes, 'SecurityStamp', '***')::text,
                     json_build_object('Roles', 'Administrador', 'SecurityStamp', '***',
                                       'MotivoConcesionPorPlataforma', v_motivo)::text,
                     v_actor, v_actor, 'SesionPrivilegiada', p_sesion, 'Persona', v_ahora);

                  RETURN 'concedido';
                END;
                $$;

                REVOKE ALL ON FUNCTION public.app_conceder_administrador_por_plataforma(uuid, uuid, text) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.app_conceder_administrador_por_plataforma(uuid, uuid, text) TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP FUNCTION public.app_conceder_administrador_por_plataforma(uuid, uuid, text);

                ALTER POLICY privilegio_concesion_a_tercero ON public."ConcesionesPrivilegio"
                    WITH CHECK ((("ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))
                        AND ("UsuarioPlataformaId" <> (SELECT public.app_ctx_usuario_id()))
                        AND (("Estado")::text = 'Vigente'::text)
                        AND ("EsAlcanceGlobal" = false)
                        AND (("Origen")::text = 'Ordinaria'::text)
                        AND ("VigenciaHasta" IS NOT NULL)
                        AND (("Capacidad")::text IN ('Aprovisionamiento', 'RestablecimientoSegundoFactor', 'RevocacionOperadorCaeExterno'))
                        AND public.app_es_admin_plataforma((SELECT public.app_ctx_usuario_id()))));

                ALTER POLICY privilegio_del_usuario ON public."ConcesionesPrivilegio"
                    WITH CHECK ((("UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))
                        OR (("ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))
                            AND ("EsAlcanceGlobal" = false)
                            AND ("Capacidad" IN ('Aprovisionamiento', 'RestablecimientoSegundoFactor', 'RevocacionOperadorCaeExterno'))
                            AND public.app_es_admin_plataforma((SELECT public.app_ctx_usuario_id())))));

                ALTER POLICY sesion_privilegiada_apertura ON public."SesionesPrivilegiadas"
                    WITH CHECK ((("CerradaEnUtc" IS NULL)
                        AND ("UsuarioSimuladoId" IS NULL)
                        AND ("InicioEnUtc" >= (now() - '00:05:00'::interval))
                        AND ("InicioEnUtc" <= (now() + '00:05:00'::interval))
                        AND ("ExpiraEnUtc" > "InicioEnUtc")
                        AND ("ExpiraEnUtc" <= ("InicioEnUtc" + '04:00:00'::interval))
                        AND (EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
                            WHERE ((c."Id" = "SesionesPrivilegiadas"."ConcesionPrivilegioId")
                                AND (c."UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))
                                AND ((c."Estado")::text = 'Vigente'::text)
                                AND (c."VigenciaDesde" <= now())
                                AND ((c."VigenciaHasta" IS NULL) OR (now() < c."VigenciaHasta"))
                                AND ((c."Capacidad")::text IN ('SoporteLectura', 'Aprovisionamiento', 'RestablecimientoSegundoFactor', 'RevocacionOperadorCaeExterno'))
                                AND (c."EsAlcanceGlobal"
                                    OR (EXISTS (SELECT 1 FROM public."TenantsAlcanzadosPorConcesion" t
                                        WHERE ((t."ConcesionPrivilegioId" = c."Id")
                                            AND (t."TenantId" = "SesionesPrivilegiadas"."TenantObjetivoId"))))))))));
                """);
        }
    }
}
