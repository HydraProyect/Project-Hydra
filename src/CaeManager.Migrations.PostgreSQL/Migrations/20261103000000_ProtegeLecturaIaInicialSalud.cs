using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    public partial class ProtegeLecturaIaInicialSalud : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'ProtegeLecturaIaInicialSalud necesita un migrador superusuario o con BYPASSRLS';
                    END IF;
                END
                $$;

                CREATE OR REPLACE FUNCTION public.proteger_lectura_ia_inicial_salud() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
                BEGIN
                    IF NEW."Sensibilidad" = 'CategoriaEspecialSalud' AND
                       (TG_OP = 'INSERT' OR OLD."Sensibilidad" IS DISTINCT FROM 'CategoriaEspecialSalud') THEN
                        NEW."LecturaIaActiva" := false;
                        NEW."VerificacionIaActiva" := false;
                    END IF;
                    RETURN NEW;
                END
                $$;
                REVOKE ALL ON FUNCTION public.proteger_lectura_ia_inicial_salud() FROM PUBLIC;

                DROP TRIGGER IF EXISTS proteger_lectura_ia_inicial_salud ON public."TiposDocumento";
                CREATE TRIGGER proteger_lectura_ia_inicial_salud
                    BEFORE INSERT OR UPDATE ON public."TiposDocumento"
                    FOR EACH ROW EXECUTE FUNCTION public.proteger_lectura_ia_inicial_salud();

                UPDATE public."TiposDocumento"
                SET "LecturaIaActiva" = false, "VerificacionIaActiva" = false
                WHERE "Sensibilidad" = 'CategoriaEspecialSalud';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Se conserva la protección al retroceder: retirar el trigger reabriría el relevo inseguro.
        }
    }
}
