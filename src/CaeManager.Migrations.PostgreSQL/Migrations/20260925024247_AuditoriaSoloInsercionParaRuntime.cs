using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// <b>RegistrosAuditoria pasa a ser de solo inserción para
    /// <c>cae_app_runtime</c></b> (P0-3 del plan de madurez 2026-09-24). Hasta
    /// aquí heredaba los cuatro verbos del <c>GRANT ... ON ALL TABLES</c> de
    /// <c>HabilitarRlsPostgres</c>, así que una sesión de runtime comprometida
    /// —o un fallo de código— podía reescribir o borrar el rastro de su propio
    /// Tenant propietario. Es el mismo cierre que
    /// <c>HabilitarRlsRegistrosAccesoDocumentoSensible</c> aplicó al otro
    /// registro de auditoría, y por las mismas razones:
    ///
    /// <list type="bullet">
    /// <item><b>REVOKE y no políticas RLS por verbo.</b> El REVOKE rechaza con
    /// <c>42501</c> al arrancar el ejecutor; una política que no autoriza el
    /// UPDATE lo deja en cero filas sin error, y además rompería
    /// <c>CoberturaRlsDelModeloTests</c>/<c>PoliticasRlsCubrenModeloTests</c>,
    /// que exigen exactamente <c>aislamiento_tenant</c>.</item>
    /// <item><b>Ningún camino legítimo del runtime lo necesita</b> (medido al
    /// escribir esto): <c>AuditoriaInterceptor</c> solo hace <c>AddRange</c>,
    /// la entidad no tiene setters públicos, no hay <c>ExecuteUpdate</c> ni
    /// <c>ExecuteDelete</c> sobre ella, ninguna FK con <c>CASCADE</c> ni
    /// <c>SET NULL</c> la alcanza y la purga la conserva
    /// (<c>TipoDatoPurgable</c>). El único borrado —la retirada del Tenant de
    /// demo— corre con <c>FabricaContextoDeBootstrap</c>, es decir, con el rol
    /// propietario, al que este REVOKE no afecta.</item>
    /// </list>
    ///
    /// <para>
    /// Privilegio de tabla, no de clúster: vive en la base y lo aplica la propia
    /// migración, así que <c>deploy/bootstrap/roles-de-cluster.sql</c> no cambia.
    /// Sin cambio de modelo de EF: el snapshot no se toca.
    /// </para>
    /// </summary>
    public partial class AuditoriaSoloInsercionParaRuntime : Migration
    {
        private const string Tabla = "RegistrosAuditoria";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Sin guarda de existencia del rol, a propósito: un clúster sin
            // cae_app_runtime es un contrato incumplido y tiene que romper
            // con 42704 — misma convención que las migraciones de RLS previas.
            migrationBuilder.Sql($@"
REVOKE UPDATE, DELETE ON ""{Tabla}"" FROM cae_app_runtime;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guardado por la existencia del rol: deshacer no puede exigir la
            // precondición que deshacer existe para abandonar.
            migrationBuilder.Sql($@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cae_app_runtime') THEN
        GRANT UPDATE, DELETE ON ""{Tabla}"" TO cae_app_runtime;
    END IF;
END $$;
");
        }
    }
}
