using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

/// <summary>
/// Lleva a la base la barrera de rol de la lectura de <c>RegistrosActividadSoporte</c> (sigue a #2016 / #2303;
/// OWASP ASVS 4.0.3 V4.1.3, mínimo privilegio). Hasta ahora la tabla solo aislaba por Tenant
/// (<c>aislamiento_tenant</c>), y cada fila lleva el <c>UsuarioSoporteId</c> del técnico de Soporte TALVEG: cualquier
/// usuario del Tenant propietario con una consulta nueva, o con una regresión en Application, la leía entera. La
/// comprobación de rol solo existía en <c>IAutorizacionDelegacionTenant</c>.
///
/// <para>
/// <b><c>solo_el_administrador_o_la_plataforma_de_la_visita</c></b>, <c>AS RESTRICTIVE FOR SELECT TO
/// cae_app_runtime</c>: se suma con AND a <c>aislamiento_tenant</c>, así que solo estrecha. Una fila se ve si se cumple
/// una de dos ramas:
/// </para>
/// <list type="bullet">
/// <item><b>Administrador del Tenant propietario</b>: la fila es del Tenant del contexto firmado y quien firma el
/// contexto es su Administrador. El mismo predicado que <c>administrador_del_tenant_objetivo</c> de
/// <c>SesionesPrivilegiadas</c> (<c>app_es_administrador_del_tenant</c>), leído del contexto firmado
/// (<c>app_ctx_usuario_id()</c>, <c>app_ctx_tenant_id()</c>) como las políticas posteriores a
/// <c>ContraeContextoRlsFirmado</c>. La función ya responde <c>false</c> fuera del Tenant del contexto
/// (<c>CierraFuncionesDeAdministradorAlTenantDelContexto</c>).</item>
/// <item><b>La plataforma, sobre sus propias visitas por la vía heredada</b>: la fila cuelga de una Delegación de
/// Tenant de propósito Soporte cuyo extremo consultora es el Tenant de origen del contexto, marcado como plataforma,
/// y cuyo extremo cliente es el Tenant de la fila. Es, en SQL, la rama <c>esLaPlataforma</c> que
/// <c>ObtenerActividadSoporteQuery</c> ya aplica hoy (sin rol, solo Tenant de origen plataforma y delegación suya), y
/// la que recorre <c>FlujoSoporteTests</c> (E2E): sin ella Soporte TALVEG dejaría de poder revisar la traza de su
/// propia visita. No alcanza las filas por Sesión Privilegiada (<c>DelegacionTenantId</c> nulo).</item>
/// </list>
///
/// <para>
/// Lo que no cambia: el INSERT del runtime (<c>TrazaSoporteService</c>, <c>AbrirAccesoSoporteCommand</c>,
/// <c>CerrarAccesoSoporteCommand</c>) no lleva <c>RETURNING</c> (clave Guid de la aplicación, ninguna columna generada
/// en la base), así que una política de SELECT no lo toca; la escritura por Sesión Privilegiada
/// (<c>app_registrar_actividad_soporte_por_sesion</c>) y la purga (<c>app_purgar_registros_auditoria</c>) son
/// <c>SECURITY DEFINER</c> del propietario, que evita la RLS; <c>cae_app_soporte</c> conserva su SELECT tal cual (la
/// política es solo para <c>cae_app_runtime</c>, como <c>administrador_del_tenant_objetivo</c>).
/// </para>
///
/// <para>
/// Efecto en otra tabla sin tocar su política: la rama G3 de <c>cuentas_lectura</c> (<c>AspNetUsers</c>) consulta
/// esta tabla con los privilegios de quien lee, así que la cuenta de Soporte TALVEG que visitó el Tenant solo la ve
/// ahora su Administrador (o la plataforma por su visita), igual que la actividad que la nombra.
/// </para>
///
/// <para>
/// Expand/contract: añade una política y no cambia esquema, firmas ni permisos. La versión anterior de la aplicación
/// solo enseña la actividad desde <c>ObtenerActividadSoporteQuery</c>, que ya devuelve las mismas filas al
/// Administrador y a la plataforma.
/// </para>
/// </summary>
public partial class SoloElAdministradorLeeLaActividadDeSoporte : Migration
{
    internal const string Politica = "solo_el_administrador_o_la_plataforma_de_la_visita";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            CREATE POLICY {Politica} ON public."RegistrosActividadSoporte"
                AS RESTRICTIVE FOR SELECT TO cae_app_runtime
                USING (
                    ("TenantId" = (SELECT public.app_ctx_tenant_id())
                     AND (SELECT public.app_es_administrador_del_tenant(
                             (SELECT public.app_ctx_usuario_id()), (SELECT public.app_ctx_tenant_id()))))
                    OR EXISTS (
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
            DROP POLICY {Politica} ON public."RegistrosActividadSoporte";
            """);
    }
}
