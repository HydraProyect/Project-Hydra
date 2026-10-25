using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Identity;

/// <summary>
/// Verifica la capa extra de control del login SSO: mientras Entra ID esté
/// configurado, una sesión sin la marca de login por Microsoft de una cuenta
/// del Tenant propietario declarado en AzureAd:TenantHydraId (o de Tenant que no
/// conste) queda
/// sin escritura: un rol editor pasa a Consulta (salvo Administrador, vía de
/// escape deliberada para nunca perder acceso de administración), Cliente
/// conserva su rol y una cuenta sin rol sigue sin él, porque la restricción
/// nunca da más alcance que el rol real — y queda intacta mientras Entra
/// ID no esté configurado, para no bloquear el login local antes de
/// activar SSO.
/// </summary>
public class RestriccionLoginLocalClaimsTransformationTests
{
    private static ClaimsPrincipal CrearPrincipal(bool esSso, params string[] roles)
    {
        var identidad = new ClaimsIdentity(authenticationType: "Prueba");
        identidad.AddClaim(new Claim(ClaimTypes.Email, "usuario@empresa.com"));
        foreach (var rol in roles)
            identidad.AddClaim(new Claim(identidad.RoleClaimType, rol));
        if (esSso)
            identidad.AddClaim(new Claim(RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin, RestriccionLoginLocalClaimsTransformation.MetodoLoginSso));

        return new ClaimsPrincipal(identidad);
    }

    private static RestriccionLoginLocalClaimsTransformation CrearTransformacion(bool ssoConfigurado) =>
        new(Options.Create(new AzureAdOptions
        {
            TenantId = ssoConfigurado ? "tenant" : null,
            ClientId = ssoConfigurado ? "client" : null,
            ClientSecret = ssoConfigurado ? "secreto" : null,
            TenantHydraId = ssoConfigurado ? Guid.Parse("00000000-0000-0000-0000-0000000000aa") : null,
        }));

    [Fact]
    public async Task Con_sso_configurado_un_login_local_queda_limitado_a_consulta()
    {
        var transformacion = CrearTransformacion(ssoConfigurado: true);
        var principal = CrearPrincipal(esSso: false, Roles.GestorCae);

        var resultado = await transformacion.TransformAsync(principal);

        var rolesResultantes = resultado.FindAll(ClaimTypes.Role).Select(c => c.Value);
        rolesResultantes.Should().ContainSingle().Which.Should().Be(Roles.Consulta);
    }

    [Fact]
    public async Task Con_sso_configurado_un_administrador_con_login_local_conserva_su_rol_real()
    {
        var transformacion = CrearTransformacion(ssoConfigurado: true);
        var principal = CrearPrincipal(esSso: false, Roles.Administrador);

        var resultado = await transformacion.TransformAsync(principal);

        resultado.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().BeEquivalentTo([Roles.Administrador]);
    }

    [Fact]
    public async Task Con_sso_configurado_un_login_por_microsoft_conserva_el_rol_real()
    {
        var transformacion = CrearTransformacion(ssoConfigurado: true);
        var principal = CrearPrincipal(esSso: true, Roles.Administrador);

        var resultado = await transformacion.TransformAsync(principal);

        resultado.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().BeEquivalentTo([Roles.Administrador]);
    }

    [Fact]
    public async Task Sin_sso_configurado_el_login_local_conserva_el_rol_real()
    {
        var transformacion = CrearTransformacion(ssoConfigurado: false);
        var principal = CrearPrincipal(esSso: false, Roles.Administrador);

        var resultado = await transformacion.TransformAsync(principal);

        resultado.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().BeEquivalentTo([Roles.Administrador]);
    }

    [Fact]
    public async Task Es_idempotente_al_aplicarse_dos_veces_seguidas()
    {
        var transformacion = CrearTransformacion(ssoConfigurado: true);
        var principal = CrearPrincipal(esSso: false, Roles.GestorCae);

        var primeraPasada = await transformacion.TransformAsync(principal);
        var segundaPasada = await transformacion.TransformAsync(primeraPasada);

        segundaPasada.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().ContainSingle().Which.Should().Be(Roles.Consulta);
        segundaPasada.FindAll(RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir)
            .Select(c => c.Value).Should().Equal([Roles.GestorCae], "la segunda pasada no lo pisa con Consulta");
    }

    // Decisión Chris 2026-10-20: Consulta entra sin cartera en todos los Tenants beneficiarios de la
    // Asignación de Operación de su organización. El perfil anterior a la restricción decide el
    // alcance, para que limitar a un Gestor CAE a Consulta no le abra más Tenants que su cartera.

    [Fact]
    public async Task Con_sso_configurado_el_login_local_conserva_el_perfil_anterior_para_decidir_el_alcance()
    {
        var resultado = await CrearTransformacion(ssoConfigurado: true)
            .TransformAsync(CrearPrincipal(esSso: false, Roles.GestorCae));

        resultado.FindAll(RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir)
            .Select(c => c.Value).Should().Equal([Roles.GestorCae]);
    }

    [Fact]
    public async Task Con_varios_roles_el_perfil_anterior_es_el_mas_acotado()
    {
        var resultado = await CrearTransformacion(ssoConfigurado: true)
            .TransformAsync(CrearPrincipal(esSso: false, Roles.CoordinadorCae, Roles.GestorCae));

        resultado.FindAll(RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir)
            .Select(c => c.Value).Should().Equal([Roles.GestorCae], "el Gestor CAE es el único acotado por cartera");
    }

    [Fact]
    public async Task Una_cuenta_sin_rol_sigue_sin_rol_y_queda_marcada_sin_perfil()
    {
        var resultado = await CrearTransformacion(ssoConfigurado: true)
            .TransformAsync(CrearPrincipal(esSso: false));

        resultado.FindAll(ClaimTypes.Role).Should().BeEmpty("Consulta le abriría el Tenant entero: la restricción nunca amplía");
        resultado.FindAll(RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir)
            .Select(c => c.Value).Should().Equal([string.Empty], "vacío es «sin perfil» (fallo cerrado)");
    }

    [Fact]
    public async Task Una_cuenta_con_solo_roles_desconocidos_queda_sin_rol_y_sin_perfil()
    {
        var resultado = await CrearTransformacion(ssoConfigurado: true)
            .TransformAsync(CrearPrincipal(esSso: false, "RolInexistente"));

        resultado.FindAll(ClaimTypes.Role).Should().BeEmpty();
        resultado.FindAll(RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir)
            .Select(c => c.Value).Should().Equal([string.Empty]);
    }

    // Defecto corregido el 2026-10-22: el usuario de Cliente, acotado a su Cliente empresarial por
    // ApplicationUser.ClienteId solo mientras su rol sea Cliente, pasaba a Consulta y veía el Tenant entero.

    [Fact]
    public async Task Con_sso_configurado_un_usuario_de_cliente_con_login_local_conserva_el_rol_cliente()
    {
        var resultado = await CrearTransformacion(ssoConfigurado: true)
            .TransformAsync(CrearPrincipal(esSso: false, Roles.Cliente));

        resultado.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().Equal([Roles.Cliente]);
        resultado.FindAll(RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir)
            .Select(c => c.Value).Should().Equal([Roles.Cliente]);
    }

    [Fact]
    public async Task Con_cliente_y_un_rol_editor_gana_cliente_por_ser_el_mas_acotado()
    {
        var transformacion = CrearTransformacion(ssoConfigurado: true);

        var primeraPasada = await transformacion.TransformAsync(CrearPrincipal(esSso: false, Roles.GestorCae, Roles.Cliente));
        var segundaPasada = await transformacion.TransformAsync(primeraPasada);

        segundaPasada.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().Equal([Roles.Cliente]);
        segundaPasada.FindAll(RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir)
            .Select(c => c.Value).Should().Equal([Roles.Cliente], "con el perfil Gestor CAE alcanzaría su cartera");
    }

    // D-1 (decisión Chris 2026-10-25): solo las cuentas del Tenant propietario declarado en
    // AzureAd:TenantHydraId pierden la escritura con contraseña. Sin claim de Tenant (los casos de
    // arriba) se restringe: fallo cerrado.

    private static readonly Guid TenantDeclarado = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private static ClaimsPrincipal ConTenant(ClaimsPrincipal principal, Guid tenant)
    {
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, tenant.ToString()));
        return principal;
    }

    [Fact]
    public async Task Con_sso_configurado_una_cuenta_del_Tenant_declarado_con_login_local_queda_en_consulta()
    {
        var resultado = await CrearTransformacion(ssoConfigurado: true)
            .TransformAsync(ConTenant(CrearPrincipal(esSso: false, Roles.GestorCae), TenantDeclarado));

        resultado.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().Equal([Roles.Consulta]);
    }

    [Fact]
    public async Task Con_sso_configurado_una_cuenta_de_otro_Tenant_con_login_local_conserva_su_rol()
    {
        var resultado = await CrearTransformacion(ssoConfigurado: true)
            .TransformAsync(ConTenant(CrearPrincipal(esSso: false, Roles.GestorCae), Guid.NewGuid()));

        resultado.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().Equal([Roles.GestorCae]);
        resultado.HasClaim(c => c.Type == RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir)
            .Should().BeFalse();
    }

    [Fact]
    public async Task Sin_restriccion_no_se_anade_el_perfil_anterior()
    {
        (await CrearTransformacion(ssoConfigurado: false).TransformAsync(CrearPrincipal(esSso: false, Roles.GestorCae)))
            .HasClaim(c => c.Type == RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir)
            .Should().BeFalse();
        (await CrearTransformacion(ssoConfigurado: true).TransformAsync(CrearPrincipal(esSso: true, Roles.GestorCae)))
            .HasClaim(c => c.Type == RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir)
            .Should().BeFalse();
    }
}
