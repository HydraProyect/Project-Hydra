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
    /// <b>La identidad de la transacción.</b> Cada concesión guarda en <c>TransaccionDeAlta</c>
    /// (<c>xid8</c>) el <c>pg_current_xact_id()</c> de la transacción que la crea, y en
    /// <c>InicioTransaccionDeAlta</c> su <c>now()</c>. El <c>WITH CHECK</c> exige que los dos coincidan
    /// con los de la transacción que escribe el alcance. <c>xid8</c> lleva la época: son 64 bits que no
    /// dan la vuelta, y por eso no sirve <c>xmin</c>, que trunca a 32 bits y coincidiría con una
    /// transacción posterior tras unos 4.000 millones de ellas, que quien concedió puede forzar.
    /// <c>pg_current_xact_id()</c> devuelve siempre la transacción de nivel superior, así que el alta
    /// dentro de un punto de guardado también pasa, cosa que <c>xmin</c> no permitía.
    /// </para>
    ///
    /// <para>
    /// <b>Por qué hace falta el inicio.</b> <c>xid8</c> solo es único dentro de un clúster. Las copias
    /// son <c>pg_dump</c> (<c>scripts/backup-borg.sh</c>), y <c>pg_restore</c> carga los datos en un
    /// clúster cuya cuenta de transacciones empieza de cero, así que una concesión restaurada guarda un
    /// número que ese clúster aún no ha usado y que una transacción nueva puede alcanzar. Una cota de
    /// fecha no lo cierra: con <c>now()</c>, fijado al empezar la transacción, bastaría abrirla dentro
    /// de la ventana y mantenerla sin xid; con <c>clock_timestamp()</c> quedarían los minutos de la
    /// ventana, en los que un clúster recién restaurado aún puede consumir los identificadores que le
    /// faltan. El inicio se compara por igualdad y no depende de ninguna ventana: toda transacción del
    /// clúster restaurado empieza después que la que creó la concesión, también si se mantiene abierta,
    /// y el par no se repite. Dentro de un clúster, dos transacciones pueden empezar en el mismo
    /// microsegundo, y de eso responde <c>xid8</c>. La caducidad del contexto firmado no aportaría nada:
    /// no ata la escritura a la concesión.
    /// </para>
    ///
    /// <para>
    /// <b>Trigger y no <c>DEFAULT</c>.</b> Un <c>DEFAULT</c> se puede falsificar: la tabla concede
    /// <c>INSERT</c> entero a <c>cae_app_runtime</c>, que podría escribir a mano un identificador futuro
    /// y ampliar su concesión cuando la cuenta llegara a él. El trigger <c>BEFORE INSERT</c>
    /// <c>concesion_privilegio_sella_su_alta</c> pisa siempre lo que venga, y un <c>GRANT</c> por columnas
    /// dependería de que nadie vuelva a conceder la tabla entera. Los <c>UPDATE</c> no lo tocan bajo RLS:
    /// <c>concesion_privilegio_solo_se_cierra</c> compara todas las columnas salvo <c>Estado</c>,
    /// <c>VigenciaHasta</c> y <c>Version</c>, y las columnas nuevas entran solas en esa comparación. Un
    /// cierre tampoco reabre nada: <c>Estado = 'Vigente'</c> sigue en el <c>EXISTS</c>. Un
    /// <c>pg_restore</c> de solo datos sobre un esquema migrado dispararía el trigger y sellaría las
    /// concesiones con la transacción de la restauración, que ya no vuelve.
    /// </para>
    ///
    /// <para>
    /// <b>Filas existentes y ranura anterior.</b> <c>ADD COLUMN</c> sin valor por defecto deja
    /// <c>NULL</c> en las concesiones anteriores, y <c>NULL</c> nunca coincide: ninguna admite alcance
    /// nuevo. Las columnas no entran en el modelo de EF Core porque ni el dominio ni la aplicación las
    /// leen ni las escriben, así que ninguna versión las conoce y el trigger las rellena en todas: la
    /// ranura anterior sirviendo no nota el cambio. No cambia el modelo ni el snapshot.
    /// </para>
    ///
    /// <para>
    /// <b>Lo que no cambia.</b> Quien concede puede crear otra concesión para la misma persona: es un
    /// acto nuevo, con su fila y su vigencia propias, y lo limitan sus políticas de alta. Esta regla
    /// hace inmutable el alcance de cada concesión, no el conjunto de concesiones de una persona.
    /// </para>
    ///
    /// <para>
    /// <b>Las altas legítimas, medidas en el código.</b> <c>ConcesionPrivilegio.SobreTenants</c> la
    /// llama solo <c>ConcederPrivilegioCommand</c>, que añade la concesión con sus filas hijas en la
    /// colección del agregado y hace un único <c>SaveChanges</c>: EF Core las escribe, padre antes que
    /// hijas, en una transacción. Las concesiones globales (<c>RaizDeBootstrap</c>,
    /// <c>SoporteLecturaGlobal</c>) no llevan filas hijas y esta política nunca les aplicó, así que el
    /// bootstrap y la auto-concesión raíz no cambian. La siembra con el propietario y la retirada de un
    /// Tenant de demo no pasan por RLS.
    /// </para>
    ///
    /// <para>
    /// Las filas de alcance ya existentes no cambian. Ningún <c>GRANT</c> cambia, ni
    /// <c>deploy/bootstrap/roles-de-cluster.sql</c>.
    /// </para>
    /// </summary>
    public partial class AlcanceDeConcesionSoloEnSuCreacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE public."ConcesionesPrivilegio"
                    ADD COLUMN "TransaccionDeAlta" xid8,
                    ADD COLUMN "InicioTransaccionDeAlta" timestamp with time zone;

                CREATE FUNCTION public.app_concesion_privilegio_sella_su_alta() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    NEW."TransaccionDeAlta" := pg_catalog.pg_current_xact_id();
                    NEW."InicioTransaccionDeAlta" := pg_catalog.now();
                    RETURN NEW;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_concesion_privilegio_sella_su_alta() FROM PUBLIC;
                CREATE TRIGGER concesion_privilegio_sella_su_alta
                    BEFORE INSERT ON public."ConcesionesPrivilegio"
                    FOR EACH ROW EXECUTE FUNCTION public.app_concesion_privilegio_sella_su_alta();

                ALTER POLICY alcance_por_quien_concede ON public."TenantsAlcanzadosPorConcesion"
                    WITH CHECK (((EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
                            WHERE ((c."Id" = "TenantsAlcanzadosPorConcesion"."ConcesionPrivilegioId")
                                AND (c."ConcedidaPorUsuarioId" = (SELECT public.app_ctx_usuario_id()))
                                AND (c."UsuarioPlataformaId" <> (SELECT public.app_ctx_usuario_id()))
                                AND (c."EsAlcanceGlobal" = false)
                                AND ((c."Estado")::text = 'Vigente'::text)
                                AND (c."TransaccionDeAlta" = pg_catalog.pg_current_xact_id())
                                AND (c."InicioTransaccionDeAlta" = pg_catalog.now()))))
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

                DROP TRIGGER concesion_privilegio_sella_su_alta ON public."ConcesionesPrivilegio";
                DROP FUNCTION public.app_concesion_privilegio_sella_su_alta();
                ALTER TABLE public."ConcesionesPrivilegio"
                    DROP COLUMN "InicioTransaccionDeAlta",
                    DROP COLUMN "TransaccionDeAlta";
                """);
        }
    }
}
