using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Aislamiento entre Tenants en el plano de privilegio de plataforma (OWASP ASVS 4.0.3 V4.1.3,
    /// mínimo privilegio, y V4.2.1, acceso a datos de otro Tenant). Sigue a
    /// <c>PartePoliticaConcesionesPorOperacion</c>, que partió por operación la política de
    /// <c>ConcesionesPrivilegio</c> y dejó abierto este hallazgo: las dos tablas hijas conservaban
    /// <c>privilegio_del_usuario</c> sin cláusula <c>FOR</c>, y su <c>WITH CHECK</c> admitía al
    /// <b>titular</b> de la concesión (el Actor de Plataforma TALVEG que la recibe). Quien tenía una concesión de
    /// <c>RestablecimientoSegundoFactor</c> acotada a un Tenant podía insertar otra fila en
    /// <c>TenantsAlcanzadosPorConcesion</c> con cualquier Tenant, abrir sobre él una Sesión
    /// Privilegiada y restablecer allí el segundo factor (<c>app_restablecer_segundo_factor_por_soporte</c>
    /// confía en esas dos filas). También podía reescribir una Sesión Privilegiada abierta (otro Tenant
    /// objetivo, otra ventana) o borrarla, y con ella el rastro.
    ///
    /// <para>
    /// <b>Las escrituras legítimas, medidas en el código</b> (ninguna más):
    /// <list type="bullet">
    /// <item><c>TenantsAlcanzadosPorConcesion</c> solo se escribe con
    /// <c>ConcesionPrivilegio.SobreTenants</c>, que solo llama <c>ConcederPrivilegioCommand</c>: la fila
    /// hija entra en el mismo <c>SaveChanges</c> que la concesión, bajo la sesión de quien concede, un
    /// AdminPlataforma vigente sobre el Tenant objetivo, a nombre de otra persona. Ninguna fila hija
    /// cambia después, y solo la borra la retirada de un Tenant de demo, con la identidad de bootstrap,
    /// que no pasa por RLS.</item>
    /// <item><c>SesionesPrivilegiadas</c> se inserta con <c>AbrirSesionPrivilegiadaCommand</c>
    /// (<c>SesionPrivilegiada.Abrir</c>), bajo la sesión del titular de la concesión, y se
    /// actualiza solo con <c>CerrarSesionPrivilegiadaCommand</c> (<c>SesionPrivilegiada.Cerrar</c>, que
    /// fija <c>CerradaEnUtc</c>), también del titular, incluso con la concesión ya cerrada. Solo la
    /// borra la misma retirada de demo.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b><c>TenantsAlcanzadosPorConcesion</c></b>:
    /// <list type="bullet">
    /// <item><c>privilegio_del_usuario</c>, <c>FOR SELECT</c>: el mismo <c>USING</c> que antes.</item>
    /// <item><c>alcance_por_quien_concede</c>, <c>FOR INSERT</c>: la concesión madre la concedió el
    /// usuario del contexto a otra persona, no es global y está vigente, y quien inserta tiene
    /// AdminPlataforma vigente sobre ese Tenant (<c>app_es_admin_plataforma_sobre</c>, la misma prueba
    /// que <c>ConcederPrivilegioCommand</c> hace en C#). El titular ya no inserta.</item>
    /// <item>Sin <c>UPDATE</c> ni <c>DELETE</c>: no existe ninguna escritura legítima así.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b><c>SesionesPrivilegiadas</c></b>:
    /// <list type="bullet">
    /// <item><c>privilegio_del_usuario</c>, <c>FOR SELECT</c>: el mismo <c>USING</c> que antes.
    /// <c>administrador_del_tenant_objetivo</c> no cambia.</item>
    /// <item><c>sesion_privilegiada_apertura</c>, <c>FOR INSERT</c>: la forma exacta de
    /// <c>SesionPrivilegiada.Abrir</c> desde el comando. Abierta, sin simular a nadie, con inicio a menos de
    /// 5 minutos de <c>now()</c> (el margen de reloj entre aplicación y base; sin él se podrían dejar
    /// sesiones armadas para más adelante, sin la ceremonia de 2FA y motivo en el momento de usarlas), con una ventana
    /// positiva de 4 horas como mucho (<c>SesionPrivilegiada.VentanaMaxima</c>), y amparada por una
    /// concesión del propio usuario, vigente ahora, de una capacidad que abre sesión
    /// (<c>CapacidadesQuePuedenAbrirSesion</c>) y que alcanza el Tenant objetivo: global, o con ese
    /// Tenant en <c>TenantsAlcanzadosPorConcesion</c>.</item>
    /// <item><c>sesion_privilegiada_cierre</c>, <c>FOR UPDATE</c>: el <c>USING</c> solo alcanza las
    /// sesiones abiertas del propio usuario y el <c>WITH CHECK</c> solo admite la fila cerrada, no
    /// antes de su inicio y con el cierre a menos de 5 minutos de <c>now()</c>, para que el rastro que
    /// ve el Tenant no muestre una ventana más corta que la real. No exige la concesión vigente: cerrar
    /// tiene que poder hacerse siempre.</item>
    /// <item>Trigger <c>sesion_privilegiada_solo_se_cierra</c>, <c>AFTER UPDATE</c>: la RLS no compara
    /// la fila de partida con la de llegada, así que al cerrar podrían reescribirse el Tenant objetivo,
    /// la ventana o el motivo. Solo pueden cambiar <c>CerradaEnUtc</c> y el testigo de concurrencia
    /// <c>Version</c>. Actúa solo cuando la RLS está activa para quien escribe
    /// (<c>row_security_active</c>), como <c>concesion_privilegio_solo_se_cierra</c>, y es
    /// <c>AFTER</c> para que la RLS hable primero.</item>
    /// <item>Sin <c>DELETE</c>: una Sesión Privilegiada es el rastro de quién entró en qué Tenant.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Ningún <c>GRANT</c> cambia, ni las funciones <c>app_es_admin_plataforma*</c>, ni
    /// <c>deploy/bootstrap/roles-de-cluster.sql</c>, ni otra política.
    /// </para>
    /// </summary>
    public partial class PartePoliticasHijasDeConcesionPorOperacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY privilegio_del_usuario ON public."TenantsAlcanzadosPorConcesion";

                CREATE POLICY privilegio_del_usuario ON public."TenantsAlcanzadosPorConcesion" FOR SELECT
                    USING ((EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
                        WHERE ((c."Id" = "TenantsAlcanzadosPorConcesion"."ConcesionPrivilegioId")
                            AND (c."UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))))));

                CREATE POLICY alcance_por_quien_concede ON public."TenantsAlcanzadosPorConcesion" FOR INSERT
                    WITH CHECK (((EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
                            WHERE ((c."Id" = "TenantsAlcanzadosPorConcesion"."ConcesionPrivilegioId")
                                AND (c."ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))
                                AND (c."UsuarioPlataformaId" <> (SELECT public.app_ctx_usuario_id()))
                                AND (c."EsAlcanceGlobal" = false)
                                AND ((c."Estado")::text = 'Vigente'::text))))
                        AND public.app_es_admin_plataforma_sobre((SELECT public.app_ctx_usuario_id()), "TenantId")));

                DROP POLICY privilegio_del_usuario ON public."SesionesPrivilegiadas";

                CREATE POLICY privilegio_del_usuario ON public."SesionesPrivilegiadas" FOR SELECT
                    USING ((EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
                        WHERE ((c."Id" = "SesionesPrivilegiadas"."ConcesionPrivilegioId")
                            AND (c."UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))))));

                CREATE POLICY sesion_privilegiada_apertura ON public."SesionesPrivilegiadas" FOR INSERT
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

                CREATE POLICY sesion_privilegiada_cierre ON public."SesionesPrivilegiadas" FOR UPDATE
                    USING ((("CerradaEnUtc" IS NULL)
                        AND (EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
                            WHERE ((c."Id" = "SesionesPrivilegiadas"."ConcesionPrivilegioId")
                                AND (c."UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id())))))))
                    WITH CHECK ((("CerradaEnUtc" IS NOT NULL)
                        AND ("CerradaEnUtc" >= "InicioEnUtc")
                        AND ("CerradaEnUtc" >= (now() - '00:05:00'::interval))
                        AND ("CerradaEnUtc" <= (now() + '00:05:00'::interval))
                        AND (EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
                            WHERE ((c."Id" = "SesionesPrivilegiadas"."ConcesionPrivilegioId")
                                AND (c."UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id())))))));

                CREATE FUNCTION public.app_sesion_privilegiada_solo_se_cierra() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF NOT pg_catalog.row_security_active(TG_RELID) THEN
                        RETURN NULL;
                    END IF;
                    IF (pg_catalog.to_jsonb(NEW) - 'CerradaEnUtc' - 'Version')
                       IS DISTINCT FROM (pg_catalog.to_jsonb(OLD) - 'CerradaEnUtc' - 'Version') THEN
                        RAISE EXCEPTION 'Cerrar una sesión privilegiada solo fija su cierre'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_sesion_privilegiada_solo_se_cierra() FROM PUBLIC;
                CREATE TRIGGER sesion_privilegiada_solo_se_cierra
                    AFTER UPDATE ON public."SesionesPrivilegiadas"
                    FOR EACH ROW EXECUTE FUNCTION public.app_sesion_privilegiada_solo_se_cierra();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER sesion_privilegiada_solo_se_cierra ON public."SesionesPrivilegiadas";
                DROP FUNCTION public.app_sesion_privilegiada_solo_se_cierra();

                DROP POLICY sesion_privilegiada_cierre ON public."SesionesPrivilegiadas";
                DROP POLICY sesion_privilegiada_apertura ON public."SesionesPrivilegiadas";
                DROP POLICY privilegio_del_usuario ON public."SesionesPrivilegiadas";

                CREATE POLICY privilegio_del_usuario ON public."SesionesPrivilegiadas"
                    USING ((EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
                        WHERE ((c."Id" = "SesionesPrivilegiadas"."ConcesionPrivilegioId")
                            AND (c."UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))))))
                    WITH CHECK ((EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
                        WHERE ((c."Id" = "SesionesPrivilegiadas"."ConcesionPrivilegioId")
                            AND (c."UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))))));

                DROP POLICY alcance_por_quien_concede ON public."TenantsAlcanzadosPorConcesion";
                DROP POLICY privilegio_del_usuario ON public."TenantsAlcanzadosPorConcesion";

                CREATE POLICY privilegio_del_usuario ON public."TenantsAlcanzadosPorConcesion"
                    USING ((EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
                        WHERE ((c."Id" = "TenantsAlcanzadosPorConcesion"."ConcesionPrivilegioId")
                            AND (c."UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))))))
                    WITH CHECK ((EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
                        WHERE ((c."Id" = "TenantsAlcanzadosPorConcesion"."ConcesionPrivilegioId")
                            AND ((c."UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))
                                OR ((c."ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))
                                    AND public.app_es_admin_plataforma_sobre((SELECT public.app_ctx_usuario_id()), "TenantsAlcanzadosPorConcesion"."TenantId")))))));
                """);
        }
    }
}
