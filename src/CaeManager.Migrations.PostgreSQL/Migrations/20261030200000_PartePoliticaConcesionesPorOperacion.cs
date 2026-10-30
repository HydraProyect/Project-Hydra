using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Defensa en profundidad del plano de privilegio de plataforma (OWASP ASVS 4.0.3 V4.1.1, el control
    /// de acceso se impone en una capa de confianza, y V4.1.3, mínimo privilegio). Hallazgo de la
    /// auditoría del 2026-10-29: <c>privilegio_del_usuario</c> de <c>ConcesionesPrivilegio</c> no tenía
    /// cláusula <c>FOR</c>, y su rama de fila propia del <c>WITH CHECK</c>
    /// (<c>UsuarioPlataformaId = app_ctx_usuario_id()</c>) no limitaba capacidad, alcance, estado ni
    /// origen. Con <c>INSERT</c>, <c>UPDATE</c> y <c>DELETE</c> concedidos a <c>cae_app_runtime</c>,
    /// cualquier sesión de usuario podía escribirse una <c>AdminPlataforma</c> global vigente propia, o
    /// devolver a <c>Vigente</c> una concesión revocada, y <c>app_es_admin_plataforma_global</c> y
    /// <c>app_destinatarios_aviso_seguridad</c> confían en esa fila.
    ///
    /// <para>
    /// <b><c>ConcesionesPrivilegio</c>, una política por operación</b>, cada una con la forma exacta de
    /// la escritura legítima que la usa (las tres fábricas que persisten hoy, ninguna más):
    /// <list type="bullet">
    /// <item><c>privilegio_del_usuario</c>, <c>FOR SELECT</c>: el mismo <c>USING</c> que antes (te
    /// nombra como beneficiario o como concedente). Las políticas de <c>SesionesPrivilegiadas</c> y
    /// <c>TenantsAlcanzadosPorConcesion</c> leen a través de ella y no cambian.</item>
    /// <item><c>privilegio_autoconcesion</c>, <c>FOR INSERT</c>, fila propia y global: o bien
    /// <c>SoporteLectura</c> ordinaria con fin, si quien escribe ya tiene <c>AdminPlataforma</c> vigente
    /// (<c>AutorizacionAutoConcesionPorMatriz</c>), o bien la <c>AdminPlataforma</c> de origen
    /// <c>BootstrapPlataforma</c>, si quien escribe es la identidad raíz y el bootstrap no está
    /// consumido (<c>AutoConcederPrivilegioCommand.ArrancarLaPlataformaAsync</c>).</item>
    /// <item><c>privilegio_concesion_a_tercero</c>, <c>FOR INSERT</c>: lo que admitía la segunda rama
    /// (concedente = usuario del contexto con <c>AdminPlataforma</c> vigente, no global, una de las tres
    /// capacidades acotadas), ahora además vigente, ordinaria, con fin y a nombre de otra persona
    /// (<c>ConcederPrivilegioCommand</c>). Esta política no prueba la cobertura del Tenant objetivo: esa
    /// la expresa la fila hija en <c>TenantsAlcanzadosPorConcesion</c>, cuya política no cambia aquí y
    /// sigue admitiendo escribirla al beneficiario (hallazgo abierto, incremento propio).</item>
    /// <item><c>privilegio_cierre_de_vigente</c>, <c>FOR UPDATE</c>: solo de <c>Vigente</c> a
    /// <c>Revocada</c> o <c>Expirada</c> (<c>ConcesionPrivilegio.Revocar</c> y <c>MarcarExpirada</c>). El
    /// <c>USING</c> mira la fila de partida y el <c>WITH CHECK</c> la de llegada, así que una concesión
    /// terminada no vuelve a ser vigente ni una vigente se reescribe sin cerrarse.</item>
    /// <item>Trigger <c>concesion_privilegio_solo_se_cierra</c>, <c>AFTER UPDATE</c>: la RLS no puede
    /// comparar la fila de partida con la de llegada, así que al cerrar podría reescribirse quién
    /// concedió, a quién, qué capacidad o con qué origen, y falsear el rastro. El trigger exige que
    /// solo cambien <c>Estado</c>, <c>VigenciaHasta</c> y el testigo de concurrencia <c>Version</c>, y que el fin nunca se alargue
    /// (<c>AdelantarFinDeVigencia</c>). Solo actúa cuando la RLS está activa para quien escribe
    /// (<c>row_security_active</c>): el propietario de la base, que no pasa por RLS, sigue preparando
    /// fixtures. Es <c>AFTER</c> para que la RLS hable primero y conserve su <c>42501</c>.</item>
    /// <item>Sin política de <c>DELETE</c>, a propósito: hoy ningún código borra concesiones, y una
    /// concesión es el rastro de quién tuvo autoridad y quién se la dio (las Sesiones Privilegiadas la
    /// referencian con <c>RESTRICT</c>). Retirar autoridad es cerrarla, no borrarla. Bajo
    /// <c>FORCE ROW LEVEL SECURITY</c> y sin política permisiva, ningún rol sujeto a RLS borra una
    /// fila.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Orden dentro del acto fundacional.</b> La concesión fundacional y el consumo del bootstrap van
    /// en el mismo <c>SaveChanges</c>. EF Core ordena las sentencias independientes por esquema y nombre
    /// de tabla (<c>ModificationCommandComparer</c>), así que el <c>INSERT</c> en
    /// <c>ConcesionesPrivilegio</c> se ejecuta antes que el <c>UPDATE</c> de
    /// <c>EstadoBootstrapPlataforma</c> y ve el bootstrap todavía sin consumir. Lo fija, bajo
    /// <c>cae_app_runtime</c>, <c>ConcesionesPrivilegioPorOperacionBajoRuntimeTests</c>: si el orden
    /// cambiara, el arranque de la plataforma se pondría rojo allí, no en un despliegue.
    /// </para>
    ///
    /// <para>
    /// <b><c>EstadoBootstrapPlataforma</c>, consumo monótono.</b> <c>estado_bootstrap_consumo_por_la_raiz</c>
    /// dejaba a la identidad raíz actualizar su fila sin impedir que <c>Consumido</c> volviera de
    /// <c>true</c> a <c>false</c>, lo que reabría el acto fundacional. Ahora su <c>USING</c> solo alcanza
    /// la fila sin consumir y su <c>WITH CHECK</c> solo admite la fila consumida, con fecha: la única
    /// transición posible es <c>false → true</c> (<c>EstadoBootstrapPlataforma.Consumir</c>). No hace
    /// falta trigger porque la RLS ya separa la fila de partida (<c>USING</c>) de la de llegada
    /// (<c>WITH CHECK</c>). La coordenada se escribe como <c>app_ctx_usuario_id()</c>, el contexto
    /// firmado; las otras dos políticas de la tabla no cambian.
    /// </para>
    ///
    /// <para>
    /// Ningún <c>GRANT</c> cambia, ni las funciones <c>app_es_admin_plataforma*</c>, ni
    /// <c>deploy/bootstrap/roles-de-cluster.sql</c>.
    /// </para>
    /// </summary>
    public partial class PartePoliticaConcesionesPorOperacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY privilegio_del_usuario ON public."ConcesionesPrivilegio";

                CREATE POLICY privilegio_del_usuario ON public."ConcesionesPrivilegio" FOR SELECT
                    USING ((("UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))
                        OR ("ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))));

                CREATE POLICY privilegio_autoconcesion ON public."ConcesionesPrivilegio" FOR INSERT
                    WITH CHECK ((("UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))
                        AND ("ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))
                        AND (("Estado")::text = 'Vigente'::text)
                        AND "EsAlcanceGlobal"
                        AND (((("Capacidad")::text = 'SoporteLectura'::text)
                                AND (("Origen")::text = 'Ordinaria'::text)
                                AND ("VigenciaHasta" IS NOT NULL)
                                AND public.app_es_admin_plataforma((SELECT public.app_ctx_usuario_id())))
                            OR ((("Capacidad")::text = 'AdminPlataforma'::text)
                                AND (("Origen")::text = 'BootstrapPlataforma'::text)
                                AND (EXISTS (SELECT 1 FROM public."EstadoBootstrapPlataforma" e
                                    WHERE e."UsuarioRaizId" = (SELECT public.app_ctx_usuario_id())
                                      AND NOT e."Consumido"))))));

                CREATE POLICY privilegio_concesion_a_tercero ON public."ConcesionesPrivilegio" FOR INSERT
                    WITH CHECK ((("ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))
                        AND ("UsuarioPlataformaId" <> (SELECT public.app_ctx_usuario_id()))
                        AND (("Estado")::text = 'Vigente'::text)
                        AND ("EsAlcanceGlobal" = false)
                        AND (("Origen")::text = 'Ordinaria'::text)
                        AND ("VigenciaHasta" IS NOT NULL)
                        AND (("Capacidad")::text IN ('Aprovisionamiento', 'RestablecimientoSegundoFactor', 'RevocacionOperadorCaeExterno'))
                        AND public.app_es_admin_plataforma((SELECT public.app_ctx_usuario_id()))));

                CREATE POLICY privilegio_cierre_de_vigente ON public."ConcesionesPrivilegio" FOR UPDATE
                    USING (((("UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))
                            OR ("ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id())))
                        AND (("Estado")::text = 'Vigente'::text)))
                    WITH CHECK (((("UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))
                            OR ("ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id())))
                        AND (("Estado")::text IN ('Revocada', 'Expirada'))));

                ALTER POLICY estado_bootstrap_consumo_por_la_raiz ON public."EstadoBootstrapPlataforma"
                    USING ((("UsuarioRaizId" = (SELECT public.app_ctx_usuario_id()))
                        AND (NOT "Consumido")))
                    WITH CHECK ((("UsuarioRaizId" = (SELECT public.app_ctx_usuario_id()))
                        AND "Consumido"
                        AND ("ConsumidoEnUtc" IS NOT NULL)));

                CREATE FUNCTION public.app_concesion_privilegio_solo_se_cierra() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF NOT pg_catalog.row_security_active(TG_RELID) THEN
                        RETURN NULL;
                    END IF;
                    IF (pg_catalog.to_jsonb(NEW) - 'Estado' - 'VigenciaHasta' - 'Version')
                       IS DISTINCT FROM (pg_catalog.to_jsonb(OLD) - 'Estado' - 'VigenciaHasta' - 'Version') THEN
                        RAISE EXCEPTION 'Cerrar una concesión de privilegio solo cambia su estado y su fin'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    IF OLD."VigenciaHasta" IS NOT NULL
                       AND (NEW."VigenciaHasta" IS NULL OR NEW."VigenciaHasta" > OLD."VigenciaHasta") THEN
                        RAISE EXCEPTION 'Cerrar una concesión de privilegio nunca alarga su vigencia'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_concesion_privilegio_solo_se_cierra() FROM PUBLIC;
                CREATE TRIGGER concesion_privilegio_solo_se_cierra
                    AFTER UPDATE ON public."ConcesionesPrivilegio"
                    FOR EACH ROW EXECUTE FUNCTION public.app_concesion_privilegio_solo_se_cierra();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER concesion_privilegio_solo_se_cierra ON public."ConcesionesPrivilegio";
                DROP FUNCTION public.app_concesion_privilegio_solo_se_cierra();

                ALTER POLICY estado_bootstrap_consumo_por_la_raiz ON public."EstadoBootstrapPlataforma"
                    USING (("UsuarioRaizId" = (SELECT public.app_ctx_usuario_id())))
                    WITH CHECK (("UsuarioRaizId" = (SELECT public.app_ctx_usuario_id())));

                DROP POLICY privilegio_cierre_de_vigente ON public."ConcesionesPrivilegio";
                DROP POLICY privilegio_concesion_a_tercero ON public."ConcesionesPrivilegio";
                DROP POLICY privilegio_autoconcesion ON public."ConcesionesPrivilegio";
                DROP POLICY privilegio_del_usuario ON public."ConcesionesPrivilegio";

                CREATE POLICY privilegio_del_usuario ON public."ConcesionesPrivilegio"
                    USING ((("UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))
                        OR ("ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))))
                    WITH CHECK ((("UsuarioPlataformaId" = (SELECT public.app_ctx_usuario_id()))
                        OR (("ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))
                            AND ("EsAlcanceGlobal" = false)
                            AND ("Capacidad" IN ('Aprovisionamiento', 'RestablecimientoSegundoFactor', 'RevocacionOperadorCaeExterno'))
                            AND public.app_es_admin_plataforma((SELECT public.app_ctx_usuario_id())))));
                """);
        }
    }
}
