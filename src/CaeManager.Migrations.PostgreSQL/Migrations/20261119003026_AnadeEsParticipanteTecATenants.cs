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
    /// adelante, el alta del Tenant TEC de B-08) y el rol propietario que ejecuta las migraciones.
    /// <c>Tenants</c> no tiene RLS y <c>cae_app_runtime</c> tiene <c>UPDATE</c> e <c>INSERT</c> de tabla
    /// entera (<c>LineaBaseCompactada</c>): un <c>REVOKE</c> por columna no anula un <c>GRANT</c> de tabla,
    /// y rehacer esos <c>GRANT</c> queda fuera (decisión de Seguridad y plataforma). Por eso lo impide el
    /// trigger <c>BEFORE INSERT OR UPDATE OF "EsParticipanteTec"</c> <c>proteger_es_participante_tec</c>:
    /// rechaza con <c>42501</c> (<c>insufficient_privilege</c>) el <c>INSERT</c> con <c>true</c> y todo
    /// <c>UPDATE</c> que cambie el valor, salvo que <c>current_user</c> esté en la lista blanca. La lista
    /// es <c>cae_app_aprovisionamiento</c> y el migrador, que se reconoce como en las migraciones de
    /// protección vecinas (<c>rolsuper OR rolbypassrls</c>) o por ser el propietario de la tabla; ninguno
    /// de los tres roles de aplicación es superusuario ni tiene <c>BYPASSRLS</c>
    /// (<c>deploy/bootstrap/roles-de-cluster.sql</c>), y el propietario podría desactivar el trigger de
    /// todos modos. La función es <c>SECURITY INVOKER</c>, así que <c>current_user</c> es quien escribe, y
    /// fija su <c>search_path</c>. Un <c>UPDATE</c> que reescribe el mismo valor (EF con la entidad entera
    /// marcada) pasa. La ranura anterior no nota nada: no menciona la columna y su <c>INSERT</c> toma el
    /// <c>DEFAULT</c>.
    /// </para>
    ///
    /// <para>
    /// La migración da a <c>cae_app_aprovisionamiento</c> el <c>UPDATE</c> de esta columna, junto a las
    /// tres del logo que ya tenía (<c>LogoTenant</c>). No le da <c>INSERT</c> sobre <c>Tenants</c>: el alta
    /// del Tenant TEC lo necesitará y va en su propia PR de esquema.
    /// </para>
    ///
    /// <para>
    /// <b>Lo que el trigger no cubre.</b> Mira el valor de la columna, no la identidad de la fila. Con SQL
    /// arbitrario, <c>cae_app_runtime</c> aún podría quitar el marcador a un Tenant sin nombrar la columna:
    /// borrando la fila y volviéndola a insertar en falso (tiene <c>DELETE</c> de tabla), o intercambiando
    /// <c>"Id"</c> con una copia en falso. Ningún camino de la aplicación reescribe la clave ni borra y recrea
    /// un Tenant marcado, y con ese mismo SQL el runtime ya puede escribir <c>EsPlataforma</c>. Cerrarlo
    /// exigiría rechazar el <c>DELETE</c> de un Tenant marcado, que choca con la retirada de un Tenant de
    /// demo, y es una decisión aparte de Seguridad y plataforma.
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
                    ELSIF NEW."EsParticipanteTec" IS NOT DISTINCT FROM OLD."EsParticipanteTec" THEN
                        RETURN NEW;
                    END IF;
                    IF current_user = 'cae_app_aprovisionamiento'
                       OR EXISTS (SELECT 1 FROM pg_catalog.pg_roles r
                                   WHERE r.rolname = current_user
                                     AND (r.rolsuper OR r.rolbypassrls
                                          OR r.oid = (SELECT c.relowner FROM pg_catalog.pg_class c WHERE c.oid = TG_RELID))) THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'Solo cae_app_aprovisionamiento o el propietario ponen o cambian Tenants.EsParticipanteTec; el rol % no puede.', current_user
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_proteger_es_participante_tec() FROM PUBLIC;
                CREATE TRIGGER proteger_es_participante_tec
                    BEFORE INSERT OR UPDATE OF "EsParticipanteTec" ON public."Tenants"
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
