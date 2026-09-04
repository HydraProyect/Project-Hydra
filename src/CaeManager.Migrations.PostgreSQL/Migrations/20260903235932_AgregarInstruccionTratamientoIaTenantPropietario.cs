using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarInstruccionTratamientoIaTenantPropietario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InstruccionesTratamientoIaTenantPropietario",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionDpaAceptada = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VersionAnexoSubencargadosAceptada = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FechaAceptacionUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OrigenInstruccion = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RegistradaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevocadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MotivoRevocacion = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstruccionesTratamientoIaTenantPropietario", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InstruccionesTratamientoIaTenantPropietario_TenantId_FechaA~",
                table: "InstruccionesTratamientoIaTenantPropietario",
                columns: new[] { "TenantId", "FechaAceptacionUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InstruccionesTratamientoIaTenantPropietario_TenantId_Vigente",
                table: "InstruccionesTratamientoIaTenantPropietario",
                column: "TenantId",
                filter: "\"RevocadaEnUtc\" IS NULL");

            // RLS en la misma migración que crea la tabla (mismo criterio que
            // AgregarOperacionImportacion): es EntidadConTenant, y los
            // ratchets CoberturaRlsDelModeloTests/PoliticasRlsCubrenModeloTests
            // exigen política para toda tabla con TenantId. El filtro global
            // de EF ya la protege; esto es la segunda línea, sobre
            // cae_app_runtime (RUNBOOK-RLS.md). Categoría 1 ("tabla
            // tenantizada"): FORCE + política aislamiento_tenant, ninguna
            // otra — es un registro evidenciario de cumplimiento de un único
            // Tenant propietario, no un catálogo global que enlace dos
            // tenants (a diferencia de DelegacionTenant/AceptacionTerminos,
            // ver el comentario de clase del dominio).
            migrationBuilder.Sql(@"
ALTER TABLE ""InstruccionesTratamientoIaTenantPropietario"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""InstruccionesTratamientoIaTenantPropietario"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""InstruccionesTratamientoIaTenantPropietario"";
CREATE POLICY aislamiento_tenant ON ""InstruccionesTratamientoIaTenantPropietario""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS aislamiento_tenant ON ""InstruccionesTratamientoIaTenantPropietario"";
ALTER TABLE ""InstruccionesTratamientoIaTenantPropietario"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""InstruccionesTratamientoIaTenantPropietario"" DISABLE ROW LEVEL SECURITY;
");

            migrationBuilder.DropTable(
                name: "InstruccionesTratamientoIaTenantPropietario");
        }
    }
}
