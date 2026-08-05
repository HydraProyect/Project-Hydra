using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Cierra el mismo hueco que <c>HabilitarRlsClavesApi</c>, esta vez para las
    /// 4 tablas del conector Microsoft 365 (P3-33, 20260802153427_CrearIntegracionesMicrosoft365):
    /// se crearon después de 20260801120000_HabilitarRlsPostgres y se quedaron
    /// fuera de la lista de tablas con RLS — el filtro global de EF (primera
    /// línea) ya las protegía, pero la segunda línea (RLS sobre
    /// <c>cae_app_runtime</c>, ver RUNBOOK-RLS.md) no las cubría. Inofensivo hoy
    /// porque ese rol restringido no está activado en ningún entorno. Misma
    /// política exacta que el resto de tablas — no reinventa el criterio, solo
    /// lo extiende a estas 4.
    /// </summary>
    public partial class HabilitarRlsIntegraciones : Migration
    {
        private static readonly string[] Tablas =
            ["ConexionesIntegracion", "CredencialesIntegracion", "SuscripcionesWebhook", "EventosWebhook"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var tabla in Tablas)
            {
                migrationBuilder.Sql($@"
ALTER TABLE ""{tabla}"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""{tabla}"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""{tabla}"";
CREATE POLICY aislamiento_tenant ON ""{tabla}""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var tabla in Tablas)
            {
                migrationBuilder.Sql($@"
DROP POLICY IF EXISTS aislamiento_tenant ON ""{tabla}"";
ALTER TABLE ""{tabla}"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""{tabla}"" DISABLE ROW LEVEL SECURITY;
");
            }
        }
    }
}
