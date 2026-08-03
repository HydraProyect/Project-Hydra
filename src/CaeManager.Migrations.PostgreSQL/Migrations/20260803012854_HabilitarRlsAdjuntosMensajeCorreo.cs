using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Cierra el mismo hueco que <c>HabilitarRlsClavesApi</c>/<c>HabilitarRlsIntegraciones</c>
    /// (P1-3), esta vez para <c>AdjuntosMensajeCorreo</c> — para no repetir
    /// ese hallazgo, el RLS se añade en la misma tanda de trabajo que crea
    /// la tabla en vez de esperar a otra ronda de auditoría. El filtro
    /// global de EF ya la protegía; esto es solo la segunda línea (RLS
    /// sobre <c>cae_app_runtime</c>, ver RUNBOOK-RLS.md), inofensiva hoy
    /// porque ese rol restringido no está activado en ningún entorno.
    /// </summary>
    public partial class HabilitarRlsAdjuntosMensajeCorreo : Migration
    {
        private const string Tabla = "AdjuntosMensajeCorreo";

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
DROP POLICY IF EXISTS aislamiento_tenant ON ""{Tabla}"";
ALTER TABLE ""{Tabla}"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""{Tabla}"" DISABLE ROW LEVEL SECURITY;
");
        }
    }
}
