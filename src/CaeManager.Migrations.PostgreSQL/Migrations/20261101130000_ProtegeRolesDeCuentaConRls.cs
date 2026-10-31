using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

/// <summary>
/// RLS sobre <c>AspNetUserRoles</c>, que hasta aquí dejaba a <c>cae_app_runtime</c> y a
/// <c>cae_app_soporte</c> leer por SQL qué rol tiene cada cuenta de cualquier Tenant (OWASP A01; ASVS 4.0.3
/// V4.1.3 y V4.2.1).
///
/// <para>
/// La fila no lleva Tenant propio: pertenece a la cuenta. Dos políticas, separadas como en
/// <c>AsignacionesOperadorDelegado</c>:
/// </para>
/// <list type="bullet">
/// <item><c>roles_cuenta_lectura</c> (solo SELECT): la cuenta se ve en <c>AspNetUsers</c>. El rol sigue a la
/// cuenta: lo que ve quien opera otro Tenant sigue siendo lo que ya veía al cruzar las dos tablas.</item>
/// <item><c>roles_cuenta_escritura</c> (todas las órdenes, USING y WITH CHECK): el Tenant de origen de la
/// cuenta es el Tenant activo. Es la frontera de <c>EditarUsuarioCommand</c> y
/// <c>AsignarRolACuentaCommand</c>, que solo gobiernan cuentas propias del Tenant activo: ver a un Operador
/// CAE externo delegado no da derecho a cambiar su rol.</item>
/// </list>
///
/// <para>
/// Con FORCE, como toda tabla con RLS que no tenga un camino que la excluya
/// (<c>CoberturaRlsDelModeloTests</c>). Las funciones <c>SECURITY DEFINER</c> que leen o escriben aquí
/// (<c>app_es_administrador_del_tenant</c>, <c>app_serializar_alta_de_administrador</c>,
/// <c>app_conceder_administrador_por_plataforma</c> y las demás) corren como el rol propietario, superusuario
/// en todos los entornos, que evita RLS. Si dejara de serlo seguirían funcionando: <c>AspNetUsers</c> no lleva
/// FORCE, así que la lectura ve la cuenta sin contexto, y la concesión por plataforma escribe en una cuenta
/// del Tenant del contexto. Las claves foráneas en cascada no pasan por las políticas.
/// </para>
///
/// <para>
/// La subconsulta no recurre: ninguna política de <c>AspNetUsers</c>, ni de las tablas que esta consulta, mira
/// <c>AspNetUserRoles</c> directamente.
/// </para>
///
/// <para>
/// Antes de que haya Tenant no se ve ninguna fila: <c>AlmacenUsuarios.GetRolesAsync</c> y
/// <c>TenantClaimsPrincipalFactory</c> leen el rol en el Tenant de la propia cuenta, igual que las búsquedas
/// de identificación, y no abren ninguna otra vía.
/// </para>
/// </summary>
public partial class ProtegeRolesDeCuentaConRls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE public."AspNetUserRoles" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE ONLY public."AspNetUserRoles" FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS roles_cuenta_lectura ON public."AspNetUserRoles";
            DROP POLICY IF EXISTS roles_cuenta_escritura ON public."AspNetUserRoles";
            CREATE POLICY roles_cuenta_lectura ON public."AspNetUserRoles" FOR SELECT
                USING (EXISTS (SELECT 1 FROM public."AspNetUsers" u
                               WHERE u."Id" = "AspNetUserRoles"."UserId"));
            CREATE POLICY roles_cuenta_escritura ON public."AspNetUserRoles"
                USING (EXISTS (SELECT 1 FROM public."AspNetUsers" u
                               WHERE u."Id" = "AspNetUserRoles"."UserId"
                                 AND u."TenantId" = (SELECT public.app_ctx_tenant_id())))
                WITH CHECK (EXISTS (SELECT 1 FROM public."AspNetUsers" u
                                    WHERE u."Id" = "AspNetUserRoles"."UserId"
                                      AND u."TenantId" = (SELECT public.app_ctx_tenant_id())));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS roles_cuenta_lectura ON public."AspNetUserRoles";
            DROP POLICY IF EXISTS roles_cuenta_escritura ON public."AspNetUserRoles";
            ALTER TABLE ONLY public."AspNetUserRoles" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE public."AspNetUserRoles" DISABLE ROW LEVEL SECURITY;
            """);
    }
}
