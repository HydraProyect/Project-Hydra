using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    public partial class ProtegeLecturaIaInicialSalud : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- La creación del trigger y el barrido comparten la transacción de la migración.
                -- #1905 no está en producción: allí no puede haber activaciones posteriores que perder.
                -- En la primera aplicación se apagan también activaciones legítimas indistinguibles
                -- de las filas creadas durante el relevo; al reaplicar se conservan.
                LOCK TABLE public."TiposDocumento" IN SHARE ROW EXCLUSIVE MODE;
                DO $migracion$
                DECLARE
                    trigger_existia boolean;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'ProtegeLecturaIaInicialSalud necesita un migrador superusuario o con BYPASSRLS';
                    END IF;
                    SELECT EXISTS (
                        SELECT 1 FROM pg_catalog.pg_trigger
                        WHERE tgrelid = 'public."TiposDocumento"'::regclass
                          AND tgname = 'proteger_lectura_ia_inicial_salud'
                          AND NOT tgisinternal
                    ) INTO trigger_existia;

                CREATE OR REPLACE FUNCTION public.proteger_lectura_ia_inicial_salud() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
                BEGIN
                    IF NEW."Sensibilidad" = 'CategoriaEspecialSalud' AND
                       (TG_OP = 'INSERT' OR OLD."Sensibilidad" IS DISTINCT FROM 'CategoriaEspecialSalud') THEN
                        NEW."LecturaIaActiva" := false;
                        NEW."VerificacionIaActiva" := false;
                        NEW."DeteccionTrabajadoresActiva" := false;
                    END IF;
                    RETURN NEW;
                END
                $$;
                REVOKE ALL ON FUNCTION public.proteger_lectura_ia_inicial_salud() FROM PUBLIC;

                DROP TRIGGER IF EXISTS proteger_lectura_ia_inicial_salud ON public."TiposDocumento";
                CREATE TRIGGER proteger_lectura_ia_inicial_salud
                    BEFORE INSERT OR UPDATE ON public."TiposDocumento"
                    FOR EACH ROW EXECUTE FUNCTION public.proteger_lectura_ia_inicial_salud();

                    IF NOT trigger_existia THEN
                        UPDATE public."TiposDocumento"
                        SET "LecturaIaActiva" = false,
                            "VerificacionIaActiva" = false,
                            "DeteccionTrabajadoresActiva" = false
                        WHERE "Sensibilidad" = 'CategoriaEspecialSalud';
                    END IF;
                END
                $migracion$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Se conserva la protección al retroceder: retirar el trigger reabriría el relevo inseguro.
        }
    }
}
