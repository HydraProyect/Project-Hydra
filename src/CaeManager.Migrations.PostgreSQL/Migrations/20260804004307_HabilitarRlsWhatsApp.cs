using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// RLS para las tres tablas nuevas de WhatsApp, en la misma tanda que las
    /// crea (<c>AgregarWhatsAppComunicaciones</c>) — mismo criterio que
    /// <c>HabilitarRlsAdjuntosMensajeCorreo</c>: no repetir el hallazgo P1-3.
    /// El filtro global de EF ya las protege; esto es la segunda línea (RLS
    /// sobre <c>cae_app_runtime</c>, ver RUNBOOK-RLS.md). Nota: el índice
    /// único global de <c>LineasWhatsApp.PhoneNumberId</c> (resolución de
    /// tenant del webhook) no contradice el RLS — la consulta del resolver
    /// corre con <c>IgnoreQueryFilters</c> bajo el rol de la aplicación,
    /// igual que <c>WebhookTenantResolver</c> con Microsoft 365.
    /// </summary>
    public partial class HabilitarRlsWhatsApp : Migration
    {
        private static readonly string[] Tablas = ["LineasWhatsApp", "MiembrosPoolLinea", "ContactosWhatsApp"];

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
