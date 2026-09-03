using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// RLS para RegistrosAccesoDocumentoSensible, en tanda separada de la que
    /// la crea — mismo criterio que HabilitarRlsSolicitudCertificacionTgss:
    /// no repetir el hallazgo P1-3 (una tabla EntidadConTenant nueva sin su
    /// política). El filtro global de EF ya la protege; esto es la segunda
    /// línea (RLS sobre cae_app_runtime, ver RUNBOOK-RLS.md) — DEC-36
    /// (REC-099) la exige explícitamente para este rastro.
    ///
    /// <para>
    /// <b>Distinto del resto de RLS del repositorio a propósito.</b> Todas las
    /// demás políticas de aislamiento por tenant llevan un único
    /// <c>USING</c>+<c>WITH CHECK</c> que se aplica a SELECT/INSERT/UPDATE/DELETE
    /// por igual — correcto para una tabla que un operador legítimo edita.
    /// Esta tabla es append-only por diseño (DEC-36: "no permitir
    /// modificación ni borrado ordinario"), y <c>cae_app_runtime</c> recibe
    /// SELECT/INSERT/UPDATE/DELETE sobre <b>toda</b> tabla nueva por
    /// <c>ALTER DEFAULT PRIVILEGES</c> (ver <c>HabilitarRlsPostgres</c>) — sin
    /// nada más, la aplicación podría actualizar o borrar filas de este
    /// rastro con una vía ordinaria (un bug, un <c>DbSet</c> mal usado), y la
    /// entidad sin métodos de mutación (ver <c>RegistroAccesoDocumentoSensible</c>)
    /// no lo impide a nivel de base — solo a nivel de código C#. Por eso esta
    /// migración añade dos políticas separadas (<c>FOR SELECT</c>/<c>FOR
    /// INSERT</c>, sin ninguna para UPDATE/DELETE) y revoca UPDATE/DELETE de
    /// <c>cae_app_runtime</c> específicamente sobre esta tabla — segunda
    /// línea de defensa en la base, coherente con la primera en el dominio.
    /// </para>
    /// </summary>
    public partial class HabilitarRlsRegistrosAccesoDocumentoSensible : Migration
    {
        private const string Tabla = "RegistrosAccesoDocumentoSensible";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
ALTER TABLE ""{Tabla}"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""{Tabla}"" FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS aislamiento_tenant ON ""{Tabla}"";
DROP POLICY IF EXISTS aislamiento_tenant_select ON ""{Tabla}"";
DROP POLICY IF EXISTS aislamiento_tenant_insert ON ""{Tabla}"";

CREATE POLICY aislamiento_tenant_select ON ""{Tabla}""
    FOR SELECT
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

CREATE POLICY aislamiento_tenant_insert ON ""{Tabla}""
    FOR INSERT
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

REVOKE UPDATE, DELETE ON ""{Tabla}"" FROM cae_app_runtime;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
GRANT UPDATE, DELETE ON ""{Tabla}"" TO cae_app_runtime;

DROP POLICY IF EXISTS aislamiento_tenant_select ON ""{Tabla}"";
DROP POLICY IF EXISTS aislamiento_tenant_insert ON ""{Tabla}"";
ALTER TABLE ""{Tabla}"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""{Tabla}"" DISABLE ROW LEVEL SECURITY;
");
        }
    }
}
