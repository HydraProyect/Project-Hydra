using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Cierra un hueco de defensa en profundidad: <c>ClavesApi</c> (P3-29,
    /// 20260802101112_CrearClaveApi) se creó después de
    /// 20260801120000_HabilitarRlsPostgres y nunca se añadió a la lista de
    /// tablas con RLS de esa migración — el filtro global de EF (primera
    /// línea) ya la protegía, pero la segunda línea (RLS sobre
    /// <c>cae_app_runtime</c>, ver RUNBOOK-RLS.md) no la cubría. Inofensivo
    /// hoy porque ese rol restringido no está activado en ningún entorno.
    /// Misma política exacta que las 40 tablas originales — no reinventa el
    /// criterio, solo lo extiende a esta tabla.
    /// </summary>
    public partial class HabilitarRlsClavesApi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE ""ClavesApi"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""ClavesApi"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""ClavesApi"";
CREATE POLICY aislamiento_tenant ON ""ClavesApi""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS aislamiento_tenant ON ""ClavesApi"";
ALTER TABLE ""ClavesApi"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""ClavesApi"" DISABLE ROW LEVEL SECURITY;
");
        }
    }
}
