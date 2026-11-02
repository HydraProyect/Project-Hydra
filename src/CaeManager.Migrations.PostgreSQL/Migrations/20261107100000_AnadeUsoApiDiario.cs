using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Rastro agregado de <c>/api/v1</c> (C-11, decisión D-5 opción a). <c>UsosApiDiarios</c> nace con RLS
    /// forzada y la política <c>aislamiento_tenant</c>. <c>cae_app_runtime</c> lee e inserta, y de lo ya escrito
    /// solo puede cambiar la columna <c>Peticiones</c>, y solo hacia arriba: no cambia de quién es una fila, no la
    /// borra y no rebaja lo contado, porque un rastro de salida que se puede poner a cero no prueba nada. Lo de
    /// hacia arriba lo impone un trigger, ya que un privilegio por columna no distingue valores. La migración retira
    /// antes lo que los privilegios por defecto de la línea base conceden a una tabla nueva. El código
    /// anterior de un relevo azul/verde no toca la tabla, así que no hay ventana rota.
    /// </summary>
    public partial class AnadeUsoApiDiario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UsosApiDiarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoClave = table.Column<int>(type: "integer", nullable: false),
                    Dia = table.Column<DateOnly>(type: "date", nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Peticiones = table.Column<long>(type: "bigint", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsosApiDiarios", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsosApiDiarios_TenantId_ApiKeyId_TipoClave_Dia_Endpoint",
                table: "UsosApiDiarios",
                columns: new[] { "TenantId", "ApiKeyId", "TipoClave", "Dia", "Endpoint" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE public."UsosApiDiarios" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."UsosApiDiarios" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."UsosApiDiarios"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."UsosApiDiarios" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT ON TABLE public."UsosApiDiarios" TO cae_app_runtime;
                GRANT UPDATE ("Peticiones") ON TABLE public."UsosApiDiarios" TO cae_app_runtime;

                CREATE FUNCTION public.uso_api_diario_solo_crece() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
                BEGIN
                    IF NEW."Peticiones" < OLD."Peticiones" THEN
                        RAISE EXCEPTION 'El contador de UsosApiDiarios no se puede rebajar' USING ERRCODE = '42501';
                    END IF;
                    RETURN NEW;
                END
                $$;
                REVOKE ALL ON FUNCTION public.uso_api_diario_solo_crece() FROM PUBLIC;

                CREATE TRIGGER uso_api_diario_solo_crece
                    BEFORE UPDATE ON public."UsosApiDiarios"
                    FOR EACH ROW EXECUTE FUNCTION public.uso_api_diario_solo_crece();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UsosApiDiarios");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.uso_api_diario_solo_crece();");
        }
    }
}
