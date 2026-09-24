using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// RLS de las tareas del asistente de flujos, en la misma tanda que crea
    /// las tablas (RUNBOOK-RLS.md). Dos capas, las dos en la base:
    ///
    /// <para>
    /// <b>aislamiento_tenant</b>, idéntica a la de toda tabla con TenantId: la
    /// tarea es del Tenant en el que se trabaja.
    /// </para>
    ///
    /// <para>
    /// <b>solo_su_persona</b>, <c>AS RESTRICTIVE</c>: dentro del Tenant, cada
    /// tarea es de una sola persona, la de <c>app.usuario_id</c> (la identidad
    /// autenticada que fija TenantRlsConnectionInterceptor). Restrictiva quiere
    /// decir que se combina con AND con la de tenant: solo estrecha, nunca
    /// ensancha. En la raíz compara la columna; en turnos y pasos exige que la
    /// tarea padre sea visible, y la subconsulta sobre la raíz ya pasa por las
    /// dos políticas de la raíz. Sin usuario (jobs de fondo, siembra) no se ve
    /// ninguna tarea: fallo cerrado, y ningún proceso de fondo las necesita.
    /// </para>
    ///
    /// <para>
    /// Soporte TALVEG no lee tareas del asistente: son la conversación de una
    /// persona. Mismo REVOKE que NotasInternasConversacion, por el mismo
    /// motivo (cae_app_soporte recibe SELECT sobre toda tabla nueva).
    /// </para>
    /// </summary>
    public partial class HabilitarRlsTareasAsistente : Migration
    {
        private const string Raiz = "TareasAsistente";
        private static readonly string[] Hijas = ["TurnosTareaAsistente", "PasosTareaAsistente"];

        private const string UsuarioDeSesion = "NULLIF(current_setting('app.usuario_id', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var tabla in (string[])[Raiz, .. Hijas])
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

            migrationBuilder.Sql($@"
DROP POLICY IF EXISTS solo_su_persona ON ""{Raiz}"";
CREATE POLICY solo_su_persona ON ""{Raiz}"" AS RESTRICTIVE
    USING (""ActorRealUsuarioId"" = {UsuarioDeSesion})
    WITH CHECK (""ActorRealUsuarioId"" = {UsuarioDeSesion});
");

            foreach (var hija in Hijas)
            {
                migrationBuilder.Sql($@"
DROP POLICY IF EXISTS solo_su_persona ON ""{hija}"";
CREATE POLICY solo_su_persona ON ""{hija}"" AS RESTRICTIVE
    USING (EXISTS (SELECT 1 FROM ""{Raiz}"" t WHERE t.""Id"" = ""{hija}"".""TareaAsistenteId""))
    WITH CHECK (EXISTS (SELECT 1 FROM ""{Raiz}"" t WHERE t.""Id"" = ""{hija}"".""TareaAsistenteId""));
");
            }

            foreach (var tabla in (string[])[Raiz, .. Hijas])
            {
                migrationBuilder.Sql($@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cae_app_soporte') THEN
        REVOKE ALL PRIVILEGES ON ""{tabla}"" FROM cae_app_soporte;
    END IF;
END $$;
");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var tabla in (string[])[.. Hijas, Raiz])
            {
                migrationBuilder.Sql($@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cae_app_soporte') THEN
        GRANT SELECT ON ""{tabla}"" TO cae_app_soporte;
    END IF;
END $$;
DROP POLICY IF EXISTS solo_su_persona ON ""{tabla}"";
DROP POLICY IF EXISTS aislamiento_tenant ON ""{tabla}"";
ALTER TABLE ""{tabla}"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""{tabla}"" DISABLE ROW LEVEL SECURITY;
");
            }
        }
    }
}
