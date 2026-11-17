using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

/// <summary>
/// Lleva a la base la barrera de rol de la lectura de <c>RegistrosActividadSoporte</c> (sigue a #2016 / #2303;
/// OWASP ASVS 4.0.3 V4.1.3, mínimo privilegio, y V4.2.1, acceso a datos por registro). Hasta ahora la tabla solo
/// aislaba por Tenant (<c>aislamiento_tenant</c>, todas las órdenes) y cada fila lleva el <c>UsuarioSoporteId</c> del
/// técnico de Soporte TALVEG: cualquier usuario del Tenant propietario la leía entera con una consulta nueva o con
/// una regresión en Application.
///
/// <para>
/// El patrón es el de <c>SesionesPrivilegiadas</c>: la lectura se abre con varias políticas PERMISSIVE de SELECT, una
/// por sujeto, y ninguna política restrictiva lleva una disyunción. <c>aislamiento_tenant</c> deja de cubrir SELECT y
/// conserva el resto de lo que cubría, para los mismos roles (PUBLIC) y con la misma expresión. PostgreSQL no admite
/// una política de «todas las órdenes salvo SELECT», así que se parte por orden:
/// </para>
/// <list type="bullet">
/// <item><c>aislamiento_tenant</c>, <c>FOR INSERT</c>: el <c>WITH CHECK</c> de siempre.</item>
/// <item><c>aislamiento_tenant_actualiza</c> (<c>FOR UPDATE</c>) y <c>aislamiento_tenant_borra</c> (<c>FOR DELETE</c>):
/// el <c>USING</c> y el <c>WITH CHECK</c> de siempre. Hoy ningún rol de la aplicación tiene UPDATE ni DELETE sobre la
/// tabla; se conservan para no quitar nada más que la lectura.</item>
/// <item><c>soporte_lee_por_tenant</c>, <c>FOR SELECT TO cae_app_soporte</c>: lo que <c>cae_app_soporte</c> veía hasta
/// hoy, las filas del Tenant del contexto. <c>cae_app_runtime</c> es miembro de <c>cae_app_soporte</c> con
/// <c>INHERIT FALSE</c>, así que esta política solo aplica tras el <c>SET ROLE</c> de la Sesión Privilegiada.</item>
/// <item><c>administrador_del_tenant_propietario_lee</c>, <c>FOR SELECT TO cae_app_runtime</c>: la fila es del Tenant
/// del contexto firmado y quien firma el contexto es su Administrador. El mismo predicado que
/// <c>administrador_del_tenant_objetivo</c> de <c>SesionesPrivilegiadas</c> (<c>app_es_administrador_del_tenant</c>,
/// que ya responde <c>false</c> fuera del Tenant del contexto). Cubre las dos vías: Delegación de Tenant de Soporte
/// y Sesión Privilegiada.</item>
/// <item><c>plataforma_lee_su_visita</c>, <c>FOR SELECT TO cae_app_runtime</c>: la fila es del Tenant del contexto
/// firmado (el ámbito explícito que abre la consulta sobre el Tenant visitado; sin esta condición la plataforma vería
/// desde su propio Tenant la traza de todas sus visitas, más de lo que ve hoy) y cuelga de una Delegación de Tenant de
/// propósito Soporte cuyo extremo consultora es el Tenant de origen del contexto, marcado como plataforma, y cuyo
/// extremo cliente es el Tenant de la fila. Es, en SQL, la rama de la plataforma que
/// <c>ObtenerActividadSoporteQuery</c> ya aplica (sin rol: cualquier cuenta del Tenant de plataforma de esa visita) y
/// la que recorre <c>FlujoSoporteTests</c> (E2E). No alcanza las filas por Sesión Privilegiada
/// (<c>DelegacionTenantId</c> nulo).</item>
/// </list>
///
/// <para>
/// Lo que no cambia: el INSERT del runtime (<c>TrazaSoporteService</c>, <c>AbrirAccesoSoporteCommand</c>,
/// <c>CerrarAccesoSoporteCommand</c>, la siembra de la demo) no lleva <c>RETURNING</c> (clave Guid de la aplicación,
/// ninguna columna generada en la base), así que no necesita ninguna política de SELECT. La escritura por Sesión
/// Privilegiada (<c>app_registrar_actividad_soporte_por_sesion</c>) y la purga (<c>app_purgar_registros_auditoria</c>)
/// son <c>SECURITY DEFINER</c> del propietario, que es superusuario y evita la RLS (la purga aborta si no lo es).
/// </para>
///
/// <para>
/// Efecto en otra tabla sin tocar su política: la rama G3 de <c>cuentas_lectura</c> (<c>AspNetUsers</c>) consulta
/// esta tabla con los privilegios de quien lee, así que bajo <c>cae_app_runtime</c> la cuenta de Soporte TALVEG que
/// visitó el Tenant solo la ven ahora su Administrador y la plataforma de esa visita, igual que la actividad que la
/// nombra.
/// </para>
///
/// <para>
/// Compatibilidad con la ranura anterior: no cambia esquema, firmas ni permisos. La versión anterior de la aplicación
/// lee la tabla solo desde <c>ObtenerActividadSoporteQuery</c>, que recibe las mismas filas para el Administrador y
/// para la plataforma; quien no es Administrador recibe una lista vacía en vez de la traza, sin error.
/// </para>
/// </summary>
public partial class SoloElAdministradorLeeLaActividadDeSoporte : Migration
{
    internal const string Insercion = "aislamiento_tenant";
    internal const string Actualizacion = "aislamiento_tenant_actualiza";
    internal const string Borrado = "aislamiento_tenant_borra";
    internal const string LecturaSoporte = "soporte_lee_por_tenant";
    internal const string LecturaAdministrador = "administrador_del_tenant_propietario_lee";
    internal const string LecturaPlataforma = "plataforma_lee_su_visita";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            DROP POLICY aislamiento_tenant ON public."RegistrosActividadSoporte";

            CREATE POLICY {Insercion} ON public."RegistrosActividadSoporte"
                FOR INSERT
                WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));

            CREATE POLICY {Actualizacion} ON public."RegistrosActividadSoporte"
                FOR UPDATE
                USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));

            CREATE POLICY {Borrado} ON public."RegistrosActividadSoporte"
                FOR DELETE
                USING ("TenantId" = (SELECT public.app_ctx_tenant_id()));

            CREATE POLICY {LecturaSoporte} ON public."RegistrosActividadSoporte"
                FOR SELECT TO cae_app_soporte
                USING ("TenantId" = (SELECT public.app_ctx_tenant_id()));

            CREATE POLICY {LecturaAdministrador} ON public."RegistrosActividadSoporte"
                FOR SELECT TO cae_app_runtime
                USING ("TenantId" = (SELECT public.app_ctx_tenant_id())
                       AND (SELECT public.app_es_administrador_del_tenant(
                               (SELECT public.app_ctx_usuario_id()), (SELECT public.app_ctx_tenant_id()))));

            CREATE POLICY {LecturaPlataforma} ON public."RegistrosActividadSoporte"
                FOR SELECT TO cae_app_runtime
                USING ("TenantId" = (SELECT public.app_ctx_tenant_id())
                       AND EXISTS (
                    SELECT 1
                    FROM public."DelegacionesTenant" d
                    JOIN public."Tenants" t ON t."Id" = d."TenantConsultoraId"
                    WHERE d."Id" = "RegistrosActividadSoporte"."DelegacionTenantId"
                      AND d."Proposito" = 'Soporte'
                      AND d."TenantClienteId" = "RegistrosActividadSoporte"."TenantId"
                      AND d."TenantConsultoraId" = (SELECT public.app_ctx_tenant_origen_id())
                      AND t."EsPlataforma"));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            DROP POLICY {LecturaPlataforma} ON public."RegistrosActividadSoporte";
            DROP POLICY {LecturaAdministrador} ON public."RegistrosActividadSoporte";
            DROP POLICY {LecturaSoporte} ON public."RegistrosActividadSoporte";
            DROP POLICY {Borrado} ON public."RegistrosActividadSoporte";
            DROP POLICY {Actualizacion} ON public."RegistrosActividadSoporte";
            DROP POLICY {Insercion} ON public."RegistrosActividadSoporte";

            CREATE POLICY aislamiento_tenant ON public."RegistrosActividadSoporte"
                USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
            """);
    }
}
