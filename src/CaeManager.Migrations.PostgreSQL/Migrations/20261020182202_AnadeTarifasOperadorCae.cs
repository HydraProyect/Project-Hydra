using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeTarifasOperadorCae : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TarifasOperadorCae",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PropietarioTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Concepto = table.Column<int>(type: "integer", nullable: false),
                    PrecioUnitario = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    MonedaIso = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Activa = table.Column<bool>(type: "boolean", nullable: false),
                    VigenteDesdeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RegistradaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TarifasOperadorCae", x => x.Id);
                    table.CheckConstraint("CK_TarifasOperadorCae_OperadorNoEsElTenantBeneficiario", "\"PropietarioTenantId\" <> \"TenantId\"");
                    table.CheckConstraint("CK_TarifasOperadorCae_PrecioNoNegativo", "\"PrecioUnitario\" >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_TarifasOperadorCae_TenantId_PropietarioTenantId_Concepto_Vi~",
                table: "TarifasOperadorCae",
                columns: new[] { "TenantId", "PropietarioTenantId", "Concepto", "VigenteDesdeUtc" });

            // Tabla del Operador CAE externo (plano Comercial): RLS estándar por TenantId, como toda EntidadConTenant.
            // Historial de versiones: el runtime solo inserta y lee; ni actualiza ni borra (la purga del Tenant corre
            // como propietario, RetiradaTenantDemoService). Los privilegios por defecto del esquema conceden de más
            // (UPDATE y DELETE al runtime, lectura a Soporte): se revocan antes de conceder lo justo. Soporte TALVEG no
            // es Operador CAE y no lee sus precios. Una versión no puede empezar en el pasado: si pudiera, cambiaría
            // la valoración de un mes ya cerrado (se valora con la versión vigente al cerrarlo). El margen de un minuto
            // cubre la distancia entre el reloj de la aplicación, que fija VigenteDesdeUtc, y now() de la transacción.
            migrationBuilder.Sql("""
                ALTER TABLE public."TarifasOperadorCae" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."TarifasOperadorCae" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."TarifasOperadorCae"
                    USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
                    WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                REVOKE ALL ON TABLE public."TarifasOperadorCae" FROM cae_app_runtime;
                GRANT SELECT,INSERT ON TABLE public."TarifasOperadorCae" TO cae_app_runtime;
                REVOKE ALL ON TABLE public."TarifasOperadorCae" FROM cae_app_soporte;

                CREATE FUNCTION public.tarifa_operador_cae_sin_vigencia_retroactiva() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF NEW."VigenteDesdeUtc" < now() - interval '1 minute' THEN
                        RAISE EXCEPTION 'La tarifa % no puede empezar antes de registrarse: cambiaría meses ya cerrados.', NEW."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER "TR_TarifasOperadorCae_SinVigenciaRetroactiva" BEFORE INSERT ON public."TarifasOperadorCae"
                    FOR EACH ROW EXECUTE FUNCTION public.tarifa_operador_cae_sin_vigencia_retroactiva();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TarifasOperadorCae");

            migrationBuilder.Sql("""DROP FUNCTION IF EXISTS public.tarifa_operador_cae_sin_vigencia_retroactiva();""");
        }
    }
}
