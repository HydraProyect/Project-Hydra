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

                CREATE OR REPLACE FUNCTION proteger_lectura_ia_inicial_salud() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Sensibilidad" = 'CategoriaEspecialSalud' AND
                       (TG_OP = 'INSERT' OR OLD."Sensibilidad" IS DISTINCT FROM 'CategoriaEspecialSalud') THEN
                        NEW."LecturaIaActiva" := false;
                        NEW."VerificacionIaActiva" := false;
                    END IF;
                    RETURN NEW;
                END
                $$;

                DROP TRIGGER IF EXISTS proteger_lectura_ia_inicial_salud ON "TiposDocumento";
                CREATE TRIGGER proteger_lectura_ia_inicial_salud
                    BEFORE INSERT OR UPDATE ON "TiposDocumento"
                    FOR EACH ROW EXECUTE FUNCTION proteger_lectura_ia_inicial_salud();

                UPDATE "TiposDocumento"
                SET "LecturaIaActiva" = false, "VerificacionIaActiva" = false
                WHERE "Sensibilidad" = 'CategoriaEspecialSalud';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Se conserva la protecci?n al retroceder: retirar el trigger reabrir?a el relevo inseguro.
        }
    }
}
