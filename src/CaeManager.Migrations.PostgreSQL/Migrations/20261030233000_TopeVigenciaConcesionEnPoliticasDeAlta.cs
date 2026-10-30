using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Defensa en profundidad del tope de vigencia de una concesión de privilegio (OWASP ASVS 4.0.3
    /// V4.1.1 y V11.1.3): <c>ConcesionPrivilegio.MaximoDiasDeVigencia</c> lo aplica el dominio al crear
    /// y estas dos políticas lo vuelven a imponer en la base, con la misma medida. Los dos
    /// <c>WITH CHECK</c> son copias íntegras de los vigentes (<c>privilegio_autoconcesion</c> de
    /// <c>PartePoliticaConcesionesPorOperacion</c>, <c>privilegio_concesion_a_tercero</c> de
    /// <c>PlataformaConcedeAdministrador</c>) con una condición más, y ninguna otra cambia.
    ///
    /// <para>
    /// <b>Política y no <c>CHECK</c> de tabla.</b> El tope limita lo que nace, nunca lo que ya existe:
    /// un <c>CHECK</c> rechazaría también el <c>UPDATE</c> que cierra una fila histórica por encima del
    /// tope, y esa fila tiene que seguir leyéndose y cerrándose. <c>privilegio_cierre_de_vigente</c> no
    /// cambia: la vigencia ya no puede crecer al cerrar (<c>concesion_privilegio_solo_se_cierra</c>).
    /// </para>
    ///
    /// <para>
    /// <b>La condición.</b> El fin queda como mucho 2160 horas después del inicio, y el inicio queda a
    /// 5 minutos o menos de <c>now()</c>; sin la segunda parte, el tope se mediría desde una fecha
    /// libre. En <c>privilegio_autoconcesion</c> va envuelta en <c>VigenciaHasta IS NULL OR …</c>: la
    /// fundacional sin fin (<c>ConcesionPrivilegio.RaizDeBootstrap</c>) sigue admitida exactamente como
    /// antes, y la rama de <c>SoporteLectura</c> ya exige fin, así que allí la condición siempre se
    /// aplica. En <c>privilegio_concesion_a_tercero</c> el fin ya es obligatorio y no hace falta rama.
    /// Las dos políticas son permisivas y se combinan con <c>OR</c>, así que la condición tiene que
    /// estar en ambas: son las dos únicas puertas de <c>INSERT</c>, y ningún <c>SECURITY DEFINER</c>
    /// inserta en esta tabla.
    /// </para>
    ///
    /// <para>
    /// <b>2160 horas y no <c>'90 days'</c>.</b> Sumar días a un <c>timestamptz</c> es aritmética de
    /// calendario en la zona horaria de la sesión: al cruzar un cambio de hora son 2159 o 2161 horas.
    /// La medida del dominio es una duración exacta (<c>TimeSpan.FromDays(90)</c>), y la vigencia máxima
    /// legítima se rechazaría una vez al año bajo una sesión que no esté en UTC. Si cambia
    /// <c>MaximoDiasDeVigencia</c>, cambia aquí con una migración:
    /// <c>Las_concesiones_de_privilegio_tienen_una_politica_por_escritura</c> vigila el valor en el
    /// catálogo.
    /// </para>
    ///
    /// <para>
    /// <b>5 minutos, en los dos sentidos</b>, la misma ventana que <c>sesion_privilegiada_apertura</c>
    /// para <c>InicioEnUtc</c>. El inicio lo fija la aplicación con <c>DateTime.UtcNow</c> antes de
    /// <c>SaveChanges</c>, y la tolerancia tiene que cubrir dos cosas. El desfase entre los relojes de
    /// aplicación y base: segundos, porque sincronizan por NTP. Y el tiempo entre esa lectura y el
    /// <c>INSERT</c>: el peor caso legítimo son los reintentos de <c>EnableRetryOnFailure</c>, que
    /// repiten el <c>SaveChanges</c> con el mismo inicio. 7 intentos de hasta 30 s de
    /// <c>CommandTimeout</c>, con esperas de hasta 30 s entre ellos, son del orden de 5 minutos en el
    /// peor caso teórico; una ventana menor rechazaría reintentos que hoy se recuperan. Si una caída
    /// la superara, el alta falla cerrada con <c>42501</c> y no escribe nada, y el usuario repite el
    /// alta, que nace con un inicio nuevo. Lo que concede de más es despreciable: la vigencia real
    /// nunca pasa de 90 días y 5 minutos desde el alta.
    /// </para>
    ///
    /// <para>
    /// <b><c>now()</c> y no <c>clock_timestamp()</c>.</b> <c>now()</c> es
    /// <c>transaction_timestamp()</c>: queda fijo al empezar la transacción, y
    /// <c>clock_timestamp()</c> sigue el reloj. En el camino legítimo son equivalentes, porque
    /// <c>SaveChanges</c> abre su transacción justo antes del <c>INSERT</c>. En una transacción larga
    /// que lea el reloj dentro de ella, <c>now()</c> se queda atrás y consume la ventana superior, y
    /// por eso cuenta con los mismos 5 minutos. La cota que protege es la superior, y <c>now()</c>
    /// nunca es posterior al reloj real: retener una transacción abierta solo acorta lo que se puede
    /// conceder, no lo alarga. Se mantiene la referencia estable que ya usa
    /// <c>sesion_privilegiada_apertura</c>, para que las dos ventanas se lean igual.
    /// </para>
    ///
    /// <para>
    /// Ningún <c>GRANT</c> cambia, ni el esquema, ni <c>deploy/bootstrap/roles-de-cluster.sql</c>.
    /// </para>
    /// </summary>
    public partial class TopeVigenciaConcesionEnPoliticasDeAlta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER POLICY privilegio_autoconcesion ON public."ConcesionesPrivilegio"
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
                                      AND NOT e."Consumido"))))
                        AND (("VigenciaHasta" IS NULL)
                            OR (("VigenciaHasta" <= ("VigenciaDesde" + '2160:00:00'::interval))
                                AND ("VigenciaDesde" >= (now() - '00:05:00'::interval))
                                AND ("VigenciaDesde" <= (now() + '00:05:00'::interval))))));

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
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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

                ALTER POLICY privilegio_autoconcesion ON public."ConcesionesPrivilegio"
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
                """);
        }
    }
}
