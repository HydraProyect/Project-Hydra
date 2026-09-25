using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Transparencia para el Tenant propietario (ADR-011 § 8.7, incremento 2):
    /// su Administrador ve las Sesiones Privilegiadas que Soporte TALVEG abre
    /// sobre sus datos. Hasta hoy la única política de
    /// <c>SesionesPrivilegiadas</c> era <c>privilegio_del_usuario</c> (solo quien
    /// tiene la concesión) y el Administrador leía cero filas.
    ///
    /// <para>
    /// Política <b>permisiva y solo de lectura</b>: se suma a la existente para
    /// <c>SELECT</c> y no toca INSERT, UPDATE ni DELETE, que siguen siendo solo
    /// del titular de la concesión. Exige a la vez que el Tenant objetivo sea
    /// <c>app.tenant_id</c> y que <c>app.usuario_id</c> sea Administrador de ese
    /// Tenant: autoridad y aislamiento en la misma frontera, no solo en
    /// Application. Por tenant a secas, cualquier usuario del Tenant leería el
    /// historial de accesos de soporte.
    /// </para>
    ///
    /// <para>
    /// El rol vive en Identity, que la política no puede leer bajo
    /// <c>cae_app_runtime</c> sin abrirla entera; de ahí la función
    /// <c>SECURITY DEFINER</c> acotada, con el mismo patrón que
    /// <c>app_es_admin_plataforma</c> (20260916203428): <c>search_path</c> fijado,
    /// EXECUTE solo para <c>cae_app_runtime</c>, y devuelve un booleano sobre dos
    /// identificadores, nunca filas. Replica el predicado de
    /// <c>AutorizacionDelegacionPorAdministradorDelCliente</c>: pertenece al
    /// Tenant, no está desactivada (bloqueo más allá del umbral de 365 días de
    /// <c>ApplicationUser.UmbralDeCuentaDesactivada</c>) y tiene el rol
    /// Administrador. Dentro de una Sesión Privilegiada <c>app.usuario_id</c> es
    /// el Actor de Plataforma TALVEG, que nunca es Administrador del Tenant
    /// visitado: la política no le da nada que no tuviera.
    /// </para>
    ///
    /// <para>
    /// <b>Solo para <c>cae_app_runtime</c></b> (<c>TO</c>), el único rol con
    /// EXECUTE sobre la función. Sin él la política valdría para PUBLIC y, dentro
    /// de una Sesión Privilegiada, <c>cae_app_soporte</c> y
    /// <c>cae_app_aprovisionamiento</c> (adoptados con SET ROLE, sin heredar de
    /// runtime) evaluarían la función al leer su propia sesión y fallarían con
    /// 42501. Ninguno de los dos es el Administrador del Tenant: no pierden nada.
    /// </para>
    /// </summary>
    public partial class AccesosSoporteTalvegVisiblesParaAdministrador : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE FUNCTION app_es_administrador_del_tenant(usuario uuid, tenant uuid) RETURNS boolean
  LANGUAGE sql STABLE SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp AS $$
  SELECT EXISTS (
    SELECT 1
    FROM public.""AspNetUsers"" u
    JOIN public.""AspNetUserRoles"" ur ON ur.""UserId"" = u.""Id""
    JOIN public.""AspNetRoles"" r ON r.""Id"" = ur.""RoleId""
    WHERE u.""Id"" = usuario
      AND u.""TenantId"" = tenant
      AND (u.""LockoutEnd"" IS NULL OR u.""LockoutEnd"" <= now() + interval '365 days')
      AND r.""NormalizedName"" = 'ADMINISTRADOR');
$$;
REVOKE ALL ON FUNCTION app_es_administrador_del_tenant(uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app_es_administrador_del_tenant(uuid, uuid) TO cae_app_runtime;

CREATE POLICY administrador_del_tenant_objetivo ON ""SesionesPrivilegiadas""
    FOR SELECT
    TO cae_app_runtime
    USING (
        ""TenantObjetivoId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (SELECT app_es_administrador_del_tenant(
                NULLIF(current_setting('app.usuario_id', true), '')::uuid,
                NULLIF(current_setting('app.tenant_id', true), '')::uuid))
    );
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS administrador_del_tenant_objetivo ON ""SesionesPrivilegiadas"";
DROP FUNCTION IF EXISTS app_es_administrador_del_tenant(uuid, uuid);
");
        }
    }
}
