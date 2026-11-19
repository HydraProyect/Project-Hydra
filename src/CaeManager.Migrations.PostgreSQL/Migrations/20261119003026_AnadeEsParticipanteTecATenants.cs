using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// <c>Tenants.EsParticipanteTec</c>, el marcador único de Tenant participante TEC (D3, acordado con
    /// Inbound el 2026-11-19). Nace en falso, sin backfill, y su <c>DEFAULT</c> cubre a la ranura anterior
    /// de un relevo azul/verde, que inserta sin conocerla y nunca la escribe. El <c>UpdateData</c> del
    /// Tenant #1 lo genera EF porque la siembra declara todos los marcadores, y deja el mismo valor.
    ///
    /// <para>
    /// <b>Quién puede escribirlo.</b> Solo <c>cae_app_aprovisionamiento</c> (el acto de plataforma y, más
    /// adelante, el alta del Tenant TEC de B-08) y un superusuario, que es quien migra. <c>Tenants</c> no
    /// tiene RLS y <c>cae_app_runtime</c> tiene <c>INSERT</c>, <c>UPDATE</c> y <c>DELETE</c> de tabla entera
    /// (<c>LineaBaseCompactada</c>): un <c>REVOKE</c> por columna no anula un <c>GRANT</c> de tabla, y
    /// rehacer esos <c>GRANT</c> queda fuera (decisión de Seguridad y plataforma). Por eso lo impide el
    /// trigger <c>proteger_es_participante_tec</c>, <c>BEFORE INSERT OR UPDATE OF "EsParticipanteTec", "Id"
    /// OR DELETE</c>, que rechaza con <c>42501</c> (<c>insufficient_privilege</c>), salvo que
    /// <c>current_user</c> esté en la lista blanca: el <c>INSERT</c> con <c>true</c>; el <c>UPDATE</c> que
    /// cambie el valor; el <c>UPDATE</c> que cambie el <c>"Id"</c> de una fila marcada antes o después del
    /// cambio; y el <c>DELETE</c> de una fila marcada. Las dos últimas reglas existen porque, sin ellas, el
    /// runtime quitaría el marcador sin nombrar la columna: borrando la fila y reinsertándola en falso, o
    /// intercambiando el <c>"Id"</c> con una copia en falso. Un <c>UPDATE</c> que reescribe el mismo valor
    /// (EF con la entidad entera marcada) pasa, y cambiar el <c>"Id"</c> o borrar una fila sin marcar,
    /// también. Ningún camino de la aplicación bajo runtime borra un Tenant ni cambia su <c>"Id"</c>: la
    /// retirada de un Tenant de demo borra con la identidad de bootstrap
    /// (<c>RetiradaTenantDemoService</c> con <c>FabricaContextoDeBootstrap</c>), que es superusuaria.
    /// </para>
    ///
    /// <para>
    /// <b>La lista blanca.</b> <c>current_user = 'cae_app_aprovisionamiento'</c> o un rol con
    /// <c>rolsuper</c>. En CI, staging y producción el migrador y el propietario de las tablas son
    /// <c>postgres</c>, superusuario, y esa rama los cubre. No se admite <c>rolbypassrls</c> ni ser el
    /// propietario de la tabla: ninguna de las dos ramas se ejercita en ningún entorno, y
    /// <c>rolbypassrls</c> abriría la puerta a cualquier rol con ese atributo aunque no tenga nada que ver
    /// con <c>Tenants</c>. Un propietario que no fuera superusuario no pasaría, pero podría desactivar el
    /// trigger: frente a él no hay barrera de todos modos. Ninguno de los tres roles de aplicación es
    /// superusuario (<c>deploy/bootstrap/roles-de-cluster.sql</c>). La función es <c>SECURITY INVOKER</c>,
    /// así que <c>current_user</c> es quien escribe, y fija su <c>search_path</c>.
    /// </para>
    ///
    /// <para>
    /// La migración da a <c>cae_app_aprovisionamiento</c> el <c>UPDATE</c> de esta columna, junto a las
    /// tres del logo que ya tenía (<c>LogoTenant</c>). No le da <c>INSERT</c> ni <c>DELETE</c> sobre
    /// <c>Tenants</c> ni <c>UPDATE</c> de <c>"Id"</c>, así que esas vías se las para el <c>GRANT</c> antes que
    /// el trigger. El alta del Tenant TEC necesitará el <c>INSERT</c> y va en su propia PR de esquema.
    /// </para>
    ///
    /// <para>
    /// <b>Lo que la base de datos no garantiza.</b> <c>cae_app_runtime</c> es miembro de
    /// <c>cae_app_aprovisionamiento</c> (y de <c>cae_app_soporte</c>) con <c>INHERIT FALSE</c> pero con
    /// <c>SET ROLE</c> (<c>deploy/bootstrap/roles-de-cluster.sql</c>), y <c>SET ROLE</c> se comprueba contra
    /// el usuario de la sesión: una sesión de runtime, también la que ya ha adoptado soporte, puede adoptar
    /// aprovisionamiento. Con SQL arbitrario, <c>SET ROLE cae_app_aprovisionamiento</c> pasa el trigger y el
    /// <c>GRANT</c>. La base de datos solo garantiza que no escribe la columna nadie cuyo
    /// <c>current_user</c> sea runtime o soporte; la elevación la decide el interceptor
    /// (<c>TenantRlsConnectionInterceptor.DebeAdoptarRolDeAprovisionamiento</c>), igual que con el logo.
    /// Además, el <c>GRANT</c> de la columna no tiene límite por fila, porque <c>Tenants</c> no tiene RLS:
    /// comprobar que el Tenant objetivo es el Tenant elevado le corresponde al servicio de plataforma de la
    /// PR de lógica.
    /// </para>
    ///
    /// <para>
    /// <b>Ranura anterior.</b> No nota nada: no menciona la columna, su <c>INSERT</c> toma el
    /// <c>DEFAULT</c>, no cambia el <c>"Id"</c> de ningún Tenant y solo borra Tenants desde la retirada de
    /// demo, con la identidad de bootstrap. Hasta la PR de lógica ninguna fila está marcada, así que ninguna
    /// de las reglas nuevas puede rechazarle nada.
    /// </para>
    /// </summary>
    public partial class AnadeEsParticipanteTecATenants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EsParticipanteTec",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "Tenants",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                column: "EsParticipanteTec",
                value: false);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION public.app_proteger_es_participante_tec() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."EsParticipanteTec" IS NOT TRUE THEN
                            RETURN NEW;
                        END IF;
                    ELSIF TG_OP = 'UPDATE' THEN
                        -- Pasa si el marcador no cambia y, además, o la clave no cambia o la fila no está
                        -- marcada (ni antes ni después, porque el marcador es el mismo).
                        IF NEW."EsParticipanteTec" IS NOT DISTINCT FROM OLD."EsParticipanteTec"
                           AND (NEW."Id" IS NOT DISTINCT FROM OLD."Id" OR OLD."EsParticipanteTec" IS NOT TRUE) THEN
                            RETURN NEW;
                        END IF;
                    ELSIF OLD."EsParticipanteTec" IS NOT TRUE THEN
                        RETURN OLD;
                    END IF;
                    IF current_user = 'cae_app_aprovisionamiento'
                       OR EXISTS (SELECT 1 FROM pg_catalog.pg_roles r
                                   WHERE r.rolname = current_user AND r.rolsuper) THEN
                        IF TG_OP = 'DELETE' THEN
                            RETURN OLD;
                        END IF;
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'Solo cae_app_aprovisionamiento o un superusuario ponen o quitan Tenants.EsParticipanteTec, cambian el Id de una fila marcada o la borran; el rol % no puede (%).', current_user, TG_OP
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_proteger_es_participante_tec() FROM PUBLIC;
                CREATE TRIGGER proteger_es_participante_tec
                    BEFORE INSERT OR UPDATE OF "EsParticipanteTec", "Id" OR DELETE ON public."Tenants"
                    FOR EACH ROW EXECUTE FUNCTION public.app_proteger_es_participante_tec();

                GRANT UPDATE ("EsParticipanteTec") ON TABLE public."Tenants" TO cae_app_aprovisionamiento;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER proteger_es_participante_tec ON public."Tenants";
                DROP FUNCTION public.app_proteger_es_participante_tec();
                REVOKE UPDATE ("EsParticipanteTec") ON TABLE public."Tenants" FROM cae_app_aprovisionamiento;
                """);

            migrationBuilder.DropColumn(
                name: "EsParticipanteTec",
                table: "Tenants");
        }
    }
}
