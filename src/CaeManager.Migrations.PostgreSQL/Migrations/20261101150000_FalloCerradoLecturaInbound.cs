using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    public partial class FalloCerradoLecturaInbound : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Reescritura(false));

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Reescritura(true));

        // pg_get_functiondef conserva el cuerpo vigente, incluidas las sustituciones de contexto firmado de P6.
        // Cada función pública se llama una vez por operación; sus auxiliares pueden ejecutarse por fila.
        // Fin de línea fijo: el Down busca la guardia literal, y una compilación con CRLF no debe cambiarla.
        private static string Reescritura(bool revertir) => $$"""
            DO $migracion$
            DECLARE
                firma text;
                oid_funcion regprocedure;
                definicion text;
                antes text;
                despues text;
                guardia text := $guardia$
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: la lectura Inbound no puede comprobar los datos del Tenant propietario', current_user;
                END IF;$guardia$;
            BEGIN
                IF {{(revertir ? "true" : "false")}} THEN
                    antes := 'BEGIN' || guardia;
                    despues := 'BEGIN';
                ELSE
                    antes := 'BEGIN';
                    despues := 'BEGIN' || guardia;
                END IF;
                FOREACH firma IN ARRAY ARRAY[
                    'public.app_inbound_autorizacion_del_contrato(uuid)',
                    'public.app_inbound_documentos_autorizados(uuid)',
                    'public.app_inbound_archivo_autorizado(uuid,uuid)'
                ] LOOP
                    oid_funcion := firma::regprocedure;
                    IF NOT (SELECT prosecdef FROM pg_catalog.pg_proc WHERE oid = oid_funcion) THEN
                        RAISE EXCEPTION 'La función % no es SECURITY DEFINER', firma;
                    END IF;
                    definicion := pg_catalog.pg_get_functiondef(oid_funcion);
                    IF (length(definicion) - length(replace(definicion, antes, ''))) / length(antes) <> 1
                       OR (NOT {{(revertir ? "true" : "false")}} AND position(guardia IN definicion) <> 0) THEN
                        RAISE EXCEPTION 'No se puede reescribir de forma inequívoca la función %', firma;
                    END IF;
                    EXECUTE replace(definicion, antes, despues);
                END LOOP;
            END
            $migracion$;
            """.ReplaceLineEndings("\n");
    }
}
