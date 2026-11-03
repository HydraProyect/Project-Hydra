using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

/// <summary>
/// Cierra <c>app_es_administrador_del_tenant(usuario, tenant)</c> y
/// <c>app_tenant_tiene_administrador_propio(tenant)</c> al Tenant del contexto firmado
/// (<c>app_ctx_tenant_id()</c>). Las dos son <c>SECURITY DEFINER</c> con EXECUTE para <c>cae_app_runtime</c>
/// (la segunda también para <c>cae_app_aprovisionamiento</c>) y leían <c>AspNetUsers</c> y
/// <c>AspNetUserRoles</c> sin RLS para cualquier Tenant que recibieran: un <c>SELECT</c> directo del runtime
/// decía si una cuenta de otro Tenant era Administrador, o si otro Tenant tenía Administrador propio. El FORCE
/// de <c>AspNetUserRoles</c> no lo cerraba, porque el propietario de la función evita RLS (OWASP A01;
/// ASVS 4.0.3 V4.1.3 y V4.2.1).
///
/// <para>
/// El EXECUTE no se puede retirar: las expresiones de las políticas se evalúan con los privilegios de quien
/// consulta. Por eso el cierre va dentro de la función. Todos los llamadores legítimos preguntan ya por el
/// Tenant activo:
/// </para>
/// <list type="bullet">
/// <item><c>administrador_del_tenant_objetivo</c> (<c>SesionesPrivilegiadas</c>, SELECT del runtime): el
/// Tenant del contexto.</item>
/// <item><c>solo_el_administrador_propone</c> y <c>solo_el_administrador_sustituye_la_propuesta</c>
/// (<c>ContactosSeguridadTenant</c>): el <c>TenantId</c> de la fila, que <c>aislamiento_tenant</c> ata a
/// <c>app_ctx_tenant_id()</c>.</item>
/// <item><c>posicion_en_el_encargo</c> (WITH CHECK de <c>EncargosAdministracion</c>): el
/// <c>PropietarioTenantId</c> de la fila, que la misma expresión exige igual al Tenant activo.</item>
/// <item><c>TenantConAdministradorPropioEnBase</c>, vía <c>TechoDeRolPorEncargo</c>: el Tenant activo de quien
/// administra por encargo, es decir, el Tenant propietario.</item>
/// </list>
///
/// <para>
/// Fuera del Tenant del contexto, o sin contexto firmado, cada función devuelve un valor fijo, el que falla
/// cerrado para su llamador: <c>app_es_administrador_del_tenant</c> responde <c>false</c> (no es
/// Administrador) y <c>app_tenant_tiene_administrador_propio</c> responde <c>true</c> (como si lo tuviera: el
/// encargo no sale pleno y el Operador CAE no puede renunciar por la vía del encargo pleno). No lanza error a
/// propósito: las políticas restrictivas de <c>ContactosSeguridadTenant</c> pueden evaluarse sobre filas de
/// otros Tenants antes de que <c>aislamiento_tenant</c> las descarte, y un error rompería la consulta legítima.
/// El <c>CASE</c> garantiza que la consulta a las tablas de cuentas no se ejecuta cuando el Tenant no coincide.
/// </para>
///
/// <para>
/// Se compara con el contexto firmado y no con el GUC <c>app.tenant_id</c>, que el runtime puede fijar con
/// <c>set_config</c>. <c>TenantSelladoInterceptor</c> vuelve a firmar el contexto al sellar otro Tenant, así que
/// los dos coinciden en toda conexión abierta por <c>TenantRlsConnectionInterceptor</c>.
/// </para>
///
/// <para>
/// Expand/contract: la firma, los permisos y el propietario no cambian (<c>CREATE OR REPLACE</c>), y la versión
/// anterior ya desplegada solo pregunta por el Tenant activo, así que sigue funcionando mientras el migrador corre.
/// </para>
/// </summary>
public partial class CierraFuncionesDeAdministradorAlTenantDelContexto : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION public.app_es_administrador_del_tenant(usuario uuid, tenant uuid) RETURNS boolean
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
              SELECT CASE
                WHEN tenant IS NOT NULL AND tenant = public.app_ctx_tenant_id() THEN EXISTS (
                  SELECT 1
                  FROM public."AspNetUsers" u
                  JOIN public."AspNetUserRoles" ur ON ur."UserId" = u."Id"
                  JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
                  WHERE u."Id" = usuario
                    AND u."TenantId" = tenant
                    AND (u."LockoutEnd" IS NULL OR u."LockoutEnd" <= now() + interval '365 days')
                    AND r."NormalizedName" = 'ADMINISTRADOR')
                ELSE false
              END;
            $$;

            CREATE OR REPLACE FUNCTION public.app_tenant_tiene_administrador_propio(p_tenant_id uuid) RETURNS boolean
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
              SELECT CASE
                WHEN p_tenant_id IS NOT NULL AND p_tenant_id = public.app_ctx_tenant_id() THEN EXISTS (
                  SELECT 1
                  FROM public."AspNetUsers" u
                  JOIN public."AspNetUserRoles" ur ON ur."UserId" = u."Id"
                  JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
                  WHERE u."TenantId" = p_tenant_id
                    AND r."NormalizedName" = 'ADMINISTRADOR'
                    AND (u."LockoutEnd" IS NULL OR u."LockoutEnd" <= now() + interval '365 days'))
                ELSE true
              END;
            $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION public.app_es_administrador_del_tenant(usuario uuid, tenant uuid) RETURNS boolean
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
              SELECT EXISTS (
                SELECT 1
                FROM public."AspNetUsers" u
                JOIN public."AspNetUserRoles" ur ON ur."UserId" = u."Id"
                JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
                WHERE u."Id" = usuario
                  AND u."TenantId" = tenant
                  AND (u."LockoutEnd" IS NULL OR u."LockoutEnd" <= now() + interval '365 days')
                  AND r."NormalizedName" = 'ADMINISTRADOR');
            $$;

            CREATE OR REPLACE FUNCTION public.app_tenant_tiene_administrador_propio(p_tenant_id uuid) RETURNS boolean
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
              SELECT EXISTS (
                SELECT 1
                FROM public."AspNetUsers" u
                JOIN public."AspNetUserRoles" ur ON ur."UserId" = u."Id"
                JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
                WHERE u."TenantId" = p_tenant_id
                  AND r."NormalizedName" = 'ADMINISTRADOR'
                  AND (u."LockoutEnd" IS NULL OR u."LockoutEnd" <= now() + interval '365 days'));
            $$;
            """);
    }
}
