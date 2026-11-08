using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Decisiones del 2026-11-08, «Segundo factor configurable también por TALVEG» y «Capacidad
    /// acotada para que TALVEG edite el segundo factor de un Tenant»: un Actor de Plataforma TALVEG
    /// cambia la exigencia de segundo factor para todos los perfiles de un Tenant, con Sesión
    /// Privilegiada y auditoría. Cuarta capacidad de acción acotada de ADR-011 § 8.7, punto 3
    /// (<c>AjusteSegundoFactorTenant</c>), con el mismo régimen que
    /// <c>app_conceder_administrador_por_plataforma</c> (ASVS 4.0.3 V4.1.3, V4.2.1 y V7.1.3).
    ///
    /// <para>
    /// La conexión de la sesión adopta <c>cae_app_soporte</c> (solo <c>SELECT</c>) y no lo pierde:
    /// esta función es su única puerta. Chris autoriza expresamente solo dos cosas: añadir la
    /// capacidad a las listas de <c>privilegio_concesion_a_tercero</c> y de
    /// <c>sesion_privilegiada_apertura</c>, sin cambiar nada más de esas políticas, y el
    /// <c>GRANT EXECUTE</c> de la función a <c>cae_app_soporte</c>. Ningún <c>GRANT</c> de tabla
    /// cambia. La función se acota sola:
    /// <list type="bullet">
    /// <item>Contexto: el RLS firmado. Actor = <c>app_ctx_usuario_id()</c>, Tenant =
    /// <c>app_ctx_tenant_id()</c>.</item>
    /// <item>Motivo: no vacío ni solo de espacios en blanco (la misma regla que
    /// <c>string.IsNullOrWhiteSpace</c>: tabuladores, saltos de línea y espacios Unicode cuentan como
    /// blanco) y, recortado como con <c>string.Trim()</c>, de 500 caracteres como mucho.</item>
    /// <item>Sesión: la que se nombra, abierta, dentro de su ventana, sin simulación, sobre el
    /// Tenant del contexto, del actor del contexto, y amparada por una concesión vigente de esta
    /// capacidad, por Tenant, que alcanza ese Tenant y que concedió otra persona.</item>
    /// <item>Actor: cuenta de un Tenant de plataforma. Y el Tenant objetivo no lo es: esta vía no
    /// rebaja la exigencia de los Actores de Plataforma TALVEG.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Efecto: solo <c>ExigeSegundoFactorTodosLosPerfiles</c> de la fila de <c>ParametrosSistema</c>
    /// del Tenant objetivo. <c>MinutosInactividadSesion</c> no se toca: lo sigue fijando el
    /// Administrador del Tenant. Si el valor ya es el pedido, devuelve <c>sin_cambios</c> y no
    /// escribe ni audita. Las sesiones del Tenant lo reciben en la siguiente revalidación de su sello,
    /// como con <c>ActualizarSeguridadAccesoCommand</c>.
    /// </para>
    ///
    /// <para>
    /// Concurrencia con el cierre de la sesión, la revocación de la concesión y la retirada del
    /// alcance: la comprobación que ampara el <c>UPDATE</c> se hace con la fila de
    /// <c>ParametrosSistema</c> tomada (<c>FOR UPDATE</c>, primero) y las de la sesión, la concesión y
    /// el alcance bloqueadas (<c>FOR SHARE</c>), en una sentencia posterior a los candados y con la
    /// hora leída en ella, como <c>app_registrar_actividad_soporte_por_sesion</c>, y solo en READ
    /// COMMITTED (<c>aislamiento_no_admitido</c> en otro caso). Un cierre o una revocación confirmados
    /// antes se ven; uno posterior espera a que el acto se confirme. Una comprobación previa sin
    /// candados evita que una sesión que no podría ampararlo llegue a bloquear filas.
    /// </para>
    ///
    /// <para>
    /// Auditoría en la misma transacción y en el Tenant afectado, que la ve en su registro de
    /// actividad: Actor real = el Actor de Plataforma TALVEG, <c>ViaAcceso = SesionPrivilegiada</c>,
    /// la sesión en <c>ViaAccesoId</c>, el valor anterior, el nuevo y el motivo.
    /// </para>
    /// </summary>
    public partial class PlataformaAjustaSegundoFactorTenant : Migration
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
                        AND ("VigenciaHasta" <= ("VigenciaDesde" + '2160:00:00'::interval))
                        AND ("VigenciaDesde" >= (now() - '00:05:00'::interval))
                        AND ("VigenciaDesde" <= (now() + '00:05:00'::interval))
                        AND (("Capacidad")::text IN ('Aprovisionamiento', 'RestablecimientoSegundoFactor', 'RevocacionOperadorCaeExterno', 'ConcesionAdministrador', 'AjusteSegundoFactorTenant'))
                        AND public.app_es_admin_plataforma((SELECT public.app_ctx_usuario_id()))));

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
                                AND ((c."Capacidad")::text IN ('SoporteLectura', 'Aprovisionamiento', 'RestablecimientoSegundoFactor', 'RevocacionOperadorCaeExterno', 'ConcesionAdministrador', 'AjusteSegundoFactorTenant'))
                                AND (c."EsAlcanceGlobal"
                                    OR (EXISTS (SELECT 1 FROM public."TenantsAlcanzadosPorConcesion" t
                                        WHERE ((t."ConcesionPrivilegioId" = c."Id")
                                            AND (t."TenantId" = "SesionesPrivilegiadas"."TenantObjetivoId"))))))))));

                CREATE FUNCTION public.app_cambiar_segundo_factor_tenant_por_plataforma(p_sesion uuid, p_exige boolean, p_motivo text) RETURNS text
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                  v_actor uuid;
                  v_tenant uuid;
                  v_motivo text;
                  v_parametros uuid;
                  v_antes boolean;
                  v_ahora timestamp with time zone := clock_timestamp();
                BEGIN
                  IF NOT public.app_ctx_valido() THEN
                    RETURN 'contexto_no_valido';
                  END IF;
                  v_actor := public.app_ctx_usuario_id();
                  v_tenant := public.app_ctx_tenant_id();
                  IF v_actor IS NULL OR v_tenant IS NULL OR p_sesion IS NULL OR p_exige IS NULL THEN
                    RETURN 'contexto_no_valido';
                  END IF;

                  -- Solo READ COMMITTED, donde cada sentencia toma una instantánea nueva y la
                  -- comprobación posterior a los candados ve el último estado confirmado. Como en
                  -- app_registrar_actividad_soporte_por_sesion.
                  IF current_setting('transaction_isolation') <> 'read committed' THEN
                    RETURN 'aislamiento_no_admitido';
                  END IF;

                  -- Recorta los mismos caracteres que string.Trim() de .NET (char.IsWhiteSpace) y
                  -- rechaza lo que string.IsNullOrWhiteSpace rechazaría: un motivo solo de
                  -- tabuladores, saltos de línea o espacios Unicode tampoco vale. La clase se escribe
                  -- con escapes y no con [[:space:]], que depende de la intercalación de la base.
                  v_motivo := regexp_replace(p_motivo,
                    '^[\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+|[\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+$',
                    '', 'g');
                  IF v_motivo IS NULL OR v_motivo = '' OR char_length(v_motivo) > 500 THEN
                    RETURN 'motivo_no_valido';
                  END IF;

                  -- Primera comprobación, sin candados: lo que no podría ampararse ni siquiera
                  -- llega a bloquear filas. La sesión: abierta, sin simular a nadie, sobre el
                  -- Tenant del contexto, del actor del contexto, y amparada por una concesión
                  -- vigente de esta capacidad, por Tenant, que alcanza ese Tenant y que concedió
                  -- otra persona.
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
                        AND c."Capacidad" = 'AjusteSegundoFactorTenant'
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

                  -- Candados, siempre en este orden: la fila de ParametrosSistema del Tenant
                  -- objetivo (FOR UPDATE), la sesión y la concesión (FOR SHARE OF s, c) y la fila
                  -- de alcance (FOR SHARE OF t). ParametrosSistema va primero para que la espera a
                  -- un cambio del Administrador del Tenant en curso no retenga la sesión ni la
                  -- concesión: mientras tanto, un cierre o una revocación se confirman sin esperar
                  -- y la comprobación definitiva los ve. Un cierre o una revocación en curso al
                  -- llegar a s y c se espera; uno posterior espera a que termine esta transacción,
                  -- que ya no espera a nadie. CerrarSesionPrivilegiadaCommand,
                  -- RevocarConcesionPrivilegioCommand y ActualizarSeguridadAccesoCommand actualizan
                  -- una sola fila por transacción, así que con ellos no hay ciclo. La única
                  -- transacción que escribe varias de estas filas es la de RetiradaTenantDemoService
                  -- (borra las de un Tenant de demo en un solo SaveChanges, en el orden que elige EF):
                  -- si coincidiera con un ajuste sobre ese Tenant, PostgreSQL detecta el
                  -- interbloqueo y aborta una de las dos, sin escritura parcial.
                  SELECT p."Id", p."ExigeSegundoFactorTodosLosPerfiles" INTO v_parametros, v_antes
                  FROM public."ParametrosSistema" p
                  WHERE p."TenantId" = v_tenant
                  FOR UPDATE;
                  IF NOT FOUND THEN
                    RETURN 'parametros_no_encontrados';
                  END IF;

                  PERFORM 1
                  FROM public."SesionesPrivilegiadas" s
                  JOIN public."ConcesionesPrivilegio" c ON c."Id" = s."ConcesionPrivilegioId"
                  WHERE s."Id" = p_sesion
                    AND s."TenantObjetivoId" = v_tenant
                    AND c."UsuarioPlataformaId" = v_actor
                  FOR SHARE OF s, c;
                  PERFORM 1
                  FROM public."TenantsAlcanzadosPorConcesion" t
                  JOIN public."SesionesPrivilegiadas" s ON s."ConcesionPrivilegioId" = t."ConcesionPrivilegioId"
                  WHERE s."Id" = p_sesion
                    AND t."TenantId" = v_tenant
                  FOR SHARE OF t;

                  -- Comprobación definitiva, con las filas ya bloqueadas y una instantánea nueva
                  -- (READ COMMITTED toma una por sentencia): ve el último estado confirmado de la
                  -- sesión, la concesión y el alcance, que ya no puede cambiar hasta que esta
                  -- transacción termine. La hora la lee esta misma sentencia, después de los
                  -- candados (subconsulta reloj, ver RegistraActividadSoportePorSesionPrivilegiada),
                  -- y es la que fecha la auditoría.
                  SELECT reloj.ahora INTO v_ahora
                  FROM (SELECT clock_timestamp() AS ahora) reloj
                  CROSS JOIN public."SesionesPrivilegiadas" s
                  JOIN public."ConcesionesPrivilegio" c ON c."Id" = s."ConcesionPrivilegioId"
                  WHERE s."Id" = p_sesion
                    AND s."TenantObjetivoId" = v_tenant
                    AND s."UsuarioSimuladoId" IS NULL
                    AND s."CerradaEnUtc" IS NULL
                    AND s."InicioEnUtc" <= reloj.ahora
                    AND s."ExpiraEnUtc" > reloj.ahora
                    AND c."UsuarioPlataformaId" = v_actor
                    AND c."Capacidad" = 'AjusteSegundoFactorTenant'
                    AND c."Estado" = 'Vigente'
                    AND c."EsAlcanceGlobal" = false
                    AND c."VigenciaDesde" <= reloj.ahora
                    AND (c."VigenciaHasta" IS NULL OR c."VigenciaHasta" > reloj.ahora)
                    AND c."ConcedidaPorUsuarioId" IS NOT NULL
                    AND c."ConcedidaPorUsuarioId" <> c."UsuarioPlataformaId"
                    AND EXISTS (
                        SELECT 1 FROM public."TenantsAlcanzadosPorConcesion" t
                        WHERE t."ConcesionPrivilegioId" = c."Id" AND t."TenantId" = v_tenant)
                  LIMIT 1;
                  IF NOT FOUND THEN
                    RETURN 'sesion_no_autorizada';
                  END IF;

                  IF v_antes = p_exige THEN
                    RETURN 'sin_cambios';
                  END IF;

                  -- Solo la exigencia de segundo factor: el plazo de inactividad no es de esta vía.
                  UPDATE public."ParametrosSistema"
                  SET "ExigeSegundoFactorTodosLosPerfiles" = p_exige
                  WHERE "Id" = v_parametros;

                  INSERT INTO public."RegistrosAuditoria"
                    ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues",
                     "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId", "TipoActor", "FechaUtc")
                  VALUES
                    (uuidv7(), v_tenant, 'ParametroSistema', v_parametros, 'Modificado',
                     json_build_object('ExigeSegundoFactorTodosLosPerfiles', v_antes)::text,
                     json_build_object('ExigeSegundoFactorTodosLosPerfiles', p_exige,
                                       'MotivoAjustePorPlataforma', v_motivo)::text,
                     v_actor, v_actor, 'SesionPrivilegiada', p_sesion, 'Persona', v_ahora);

                  RETURN 'cambiado';
                END;
                $$;

                REVOKE ALL ON FUNCTION public.app_cambiar_segundo_factor_tenant_por_plataforma(uuid, boolean, text) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.app_cambiar_segundo_factor_tenant_por_plataforma(uuid, boolean, text) TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Lo que haya quedado concedido o abierto con esta capacidad no admite las políticas
            // anteriores: ALTER POLICY no revalida filas existentes, pero ninguna sesión nueva la
            // podría abrir, y la función ya no existe.
            migrationBuilder.Sql(
                """
                DROP FUNCTION public.app_cambiar_segundo_factor_tenant_por_plataforma(uuid, boolean, text);

                ALTER POLICY privilegio_concesion_a_tercero ON public."ConcesionesPrivilegio"
                    WITH CHECK ((("ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))
                        AND ("UsuarioPlataformaId" <> (SELECT public.app_ctx_usuario_id()))
                        AND (("Estado")::text = 'Vigente'::text)
                        AND ("EsAlcanceGlobal" = false)
                        AND (("Origen")::text = 'Ordinaria'::text)
                        AND ("VigenciaHasta" IS NOT NULL)
                        AND ("VigenciaHasta" <= ("VigenciaDesde" + '2160:00:00'::interval))
                        AND ("VigenciaDesde" >= (now() - '00:05:00'::interval))
                        AND ("VigenciaDesde" <= (now() + '00:05:00'::interval))
                        AND (("Capacidad")::text IN ('Aprovisionamiento', 'RestablecimientoSegundoFactor', 'RevocacionOperadorCaeExterno', 'ConcesionAdministrador'))
                        AND public.app_es_admin_plataforma((SELECT public.app_ctx_usuario_id()))));

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
                """);
        }
    }
}
