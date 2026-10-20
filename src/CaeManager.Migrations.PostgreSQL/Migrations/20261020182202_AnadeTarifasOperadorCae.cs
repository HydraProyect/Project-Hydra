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
                });

            migrationBuilder.CreateIndex(
                name: "IX_TarifasOperadorCae_TenantId_PropietarioTenantId_Concepto_Vi~",
                table: "TarifasOperadorCae",
                columns: new[] { "TenantId", "PropietarioTenantId", "Concepto", "VigenteDesdeUtc" });

            // Tabla del Operador CAE externo (plano Comercial): RLS estándar por TenantId, como toda EntidadConTenant.
            // Historial de versiones: el runtime inserta y lee, nunca actualiza (DELETE solo para la purga del Tenant).
            // Los privilegios por defecto del esquema conceden de más (UPDATE al runtime, lectura a Soporte): se
            // revocan antes de conceder lo justo. Soporte TALVEG no es Operador CAE y no lee sus precios.
            migrationBuilder.Sql("""
                ALTER TABLE public."TarifasOperadorCae" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."TarifasOperadorCae" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."TarifasOperadorCae"
                    USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
                    WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                REVOKE ALL ON TABLE public."TarifasOperadorCae" FROM cae_app_runtime;
                GRANT SELECT,INSERT,DELETE ON TABLE public."TarifasOperadorCae" TO cae_app_runtime;
                REVOKE ALL ON TABLE public."TarifasOperadorCae" FROM cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TarifasOperadorCae");
        }
    }
}
