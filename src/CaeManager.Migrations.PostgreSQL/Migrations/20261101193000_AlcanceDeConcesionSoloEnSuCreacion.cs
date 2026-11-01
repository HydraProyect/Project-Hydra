using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// El alcance de una concesión de privilegio solo se fija al crearla (OWASP ASVS 4.0.3 V4.1.3,
    /// mínimo privilegio). <c>alcance_por_quien_concede</c>, de
    /// <c>PartePoliticasHijasDeConcesionPorOperacion</c>, dejaba al AdminPlataforma que concedió insertar
    /// más tarde otra fila en <c>TenantsAlcanzadosPorConcesion</c>, con cualquier Tenant sobre el que
    /// tuviera autoridad: una concesión viva crecía sin nuevo acto de concesión, sin una fila nueva en
    /// <c>ConcesionesPrivilegio</c> que dejara su fecha y su vigencia, y el Actor de Plataforma TALVEG
    /// que la tenía podía abrir sobre ese Tenant una Sesión Privilegiada.
    /// Hacía falta SQL bajo <c>cae_app_runtime</c> y el contexto firmado de quien concedió; ningún
    /// camino de la aplicación lo hace. Ninguna migración posterior tocó esta política:
    /// <c>PlataformaConcedeAdministrador</c> y <c>TopeVigenciaConcesionEnPoliticasDeAlta</c> cambiaron
    /// las de <c>ConcesionesPrivilegio</c> y <c>sesion_privilegiada_apertura</c>.
    ///
    /// <para>
    /// <b>La condición.</b> El <c>WITH CHECK</c> es copia íntegra del vigente con dos condiciones más.
    /// La primera es <c>c.xmin = pg_current_xact_id()::xid</c>: la versión visible de la concesión madre la escribió
    /// esta misma transacción. Se apoya en dos hechos de la base y no en un dato que el llamador pueda
    /// aportar. <c>xmin</c> no se escribe desde SQL, y el <c>EXISTS</c> ya exige
    /// <c>Estado = 'Vigente'</c>, un estado al que un <c>UPDATE</c> bajo RLS no puede llegar:
    /// <c>privilegio_cierre_de_vigente</c> solo admite la fila de llegada <c>Revocada</c> o
    /// <c>Expirada</c>, así que una versión de la concesión escrita por esta transacción y aún
    /// <c>Vigente</c> es necesariamente su <c>INSERT</c>. Reescribir la concesión para que
    /// <c>xmin</c> cambie la cierra, y cerrada no admite alcance.
    /// </para>
    ///
    /// <para>
    /// <b>Y una cota de fecha.</b> <c>xmin</c> son 32 bits y al congelar la fila conserva su valor, así
    /// que tras unos 4.000 millones de transacciones, que quien concedió puede forzar, una concesión
    /// vieja volvería a coincidir. <c>VigenciaDesde >= now() - 5 minutos</c> lo descarta sin tocar el
    /// alta legítima: en esta misma transacción <c>privilegio_concesion_a_tercero</c> exigió esa misma
    /// cota con el mismo <c>now()</c>, y <c>concesion_privilegio_solo_se_cierra</c> impide reescribirla.
    /// </para>
    ///
    /// <para>
    /// <b>Lo que no cambia.</b> Quien concede puede crear otra concesión para la misma persona: es un
    /// acto nuevo, con su fila y su vigencia propias, y lo limitan sus políticas de alta. Esta regla
    /// hace inmutable el alcance de cada concesión, no el conjunto de concesiones de una persona.
    /// </para>
    ///
    /// <para>
    /// <b>Solo con la fecha no bastaría.</b> Comparar únicamente con <c>VigenciaDesde</c> dejaría ampliar
    /// la concesión durante 5 minutos, y una columna nueva cambiaría el modelo y obligaría a la ranura
    /// anterior a escribirla.
    /// </para>
    ///
    /// <para>
    /// <b>Las altas legítimas, medidas en el código.</b> <c>ConcesionPrivilegio.SobreTenants</c> la
    /// llama solo <c>ConcederPrivilegioCommand</c>, que añade la concesión con sus filas hijas en la
    /// colección del agregado y hace un único <c>SaveChanges</c>, sin transacción exterior ni punto de
    /// guardado: EF Core las escribe, padre antes que hijas, en una transacción. El código es el mismo
    /// desde que existe esta política, así que la ranura anterior sirviendo no nota el cambio. Las
    /// concesiones globales (<c>RaizDeBootstrap</c>, <c>SoporteLecturaGlobal</c>) no llevan filas hijas
    /// y esta política nunca les aplicó, así que el bootstrap y la auto-concesión raíz no cambian. La
    /// siembra con el propietario y la retirada de un Tenant de demo no pasan por RLS.
    /// </para>
    ///
    /// <para>
    /// <b>Límite conocido.</b> <c>pg_current_xact_id()</c> devuelve la transacción de nivel superior y
    /// una fila escrita dentro de un punto de guardado lleva el identificador de la subtransacción. Si
    /// alguien envolviera el alta en una transacción exterior, EF Core abriría un punto de guardado y
    /// la concesión fallaría cerrada con <c>42501</c>. Está a favor de la seguridad, y lo fija
    /// <c>Un_alta_dentro_de_una_transaccion_exterior_falla_cerrada</c>.
    /// </para>
    ///
    /// <para>
    /// Las filas de alcance ya existentes no cambian. Ningún <c>GRANT</c> cambia, ni el esquema, ni el
    /// modelo, ni <c>deploy/bootstrap/roles-de-cluster.sql</c>.
    /// </para>
    /// </summary>
    public partial class AlcanceDeConcesionSoloEnSuCreacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER POLICY alcance_por_quien_concede ON public."TenantsAlcanzadosPorConcesion"
                    WITH CHECK (((EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
                            WHERE ((c."Id" = "TenantsAlcanzadosPorConcesion"."ConcesionPrivilegioId")
                                AND (c."ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))
                                AND (c."UsuarioPlataformaId" <> (SELECT public.app_ctx_usuario_id()))
                                AND (c."EsAlcanceGlobal" = false)
                                AND ((c."Estado")::text = 'Vigente'::text)
                                AND (c.xmin = (pg_catalog.pg_current_xact_id())::xid)
                                AND (c."VigenciaDesde" >= (now() - '00:05:00'::interval)))))
                        AND public.app_es_admin_plataforma_sobre((SELECT public.app_ctx_usuario_id()), "TenantId")));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER POLICY alcance_por_quien_concede ON public."TenantsAlcanzadosPorConcesion"
                    WITH CHECK (((EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
                            WHERE ((c."Id" = "TenantsAlcanzadosPorConcesion"."ConcesionPrivilegioId")
                                AND (c."ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))
                                AND (c."UsuarioPlataformaId" <> (SELECT public.app_ctx_usuario_id()))
                                AND (c."EsAlcanceGlobal" = false)
                                AND ((c."Estado")::text = 'Vigente'::text))))
                        AND public.app_es_admin_plataforma_sobre((SELECT public.app_ctx_usuario_id()), "TenantId")));
                """);
        }
    }
}
