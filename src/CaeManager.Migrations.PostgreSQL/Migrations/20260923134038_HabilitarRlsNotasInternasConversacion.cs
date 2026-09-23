using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Mismo criterio que HabilitarRlsEventosConversacion: el RLS se añade en
    /// la misma tanda que crea la tabla — ver RUNBOOK-RLS.md. La nota interna
    /// es conversación del equipo del Tenant propietario; sin esta política,
    /// el filtro global de EF sería la única barrera entre tenants.
    /// </summary>
    public partial class HabilitarRlsNotasInternasConversacion : Migration
    {
        private const string Tabla = "NotasInternasConversacion";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
ALTER TABLE ""{Tabla}"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""{Tabla}"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""{Tabla}"";
CREATE POLICY aislamiento_tenant ON ""{Tabla}""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");

            // D3-Soporte: Soporte TALVEG no lee notas internas. La consulta ya
            // lo deniega en Application (ObtenerNotasInternasConversacionQuery),
            // pero una sesión privilegiada lee con cae_app_soporte, que recibe
            // SELECT sobre toda tabla nueva por los privilegios por defecto de
            // RolSoporteSoloLectura. Sin este REVOKE, cualquier otra lectura que
            // un día tocara esta tabla bajo sesión privilegiada la vería entera
            // dentro del tenant objetivo. Si D3 abre la concesión expresa, el
            // GRANT va en la migración que construya ese camino.
            migrationBuilder.Sql($@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cae_app_soporte') THEN
        REVOKE ALL PRIVILEGES ON ""{Tabla}"" FROM cae_app_soporte;
    END IF;
END $$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cae_app_soporte') THEN
        GRANT SELECT ON ""{Tabla}"" TO cae_app_soporte;
    END IF;
END $$;
DROP POLICY IF EXISTS aislamiento_tenant ON ""{Tabla}"";
ALTER TABLE ""{Tabla}"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""{Tabla}"" DISABLE ROW LEVEL SECURITY;
");
        }
    }
}
