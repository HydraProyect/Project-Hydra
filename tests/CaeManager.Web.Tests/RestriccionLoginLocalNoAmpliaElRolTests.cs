using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// La restricción de login local (<see cref="RestriccionLoginLocalClaimsTransformation"/>) compuesta
/// con <see cref="CurrentUserService"/>, que es quien entrega el rol efectivo y el perfil que decide
/// el alcance a <c>AlcanceDatosService</c>. Invariante: la restricción solo quita escritura; nunca da
/// a la sesión un rol con más alcance que su rol real.
///
/// <para>
/// El defecto que fija (2026-10-22): con Entra ID configurado, una cuenta con rol Cliente (acotada a su
/// Cliente empresarial, <c>ApplicationUser.ClienteId</c>, en la rama <c>(Roles.Cliente, id)</c> de
/// <c>AlcanceDatosService.ObtenerAlcanceRealAsync</c>) que entraba con contraseña quedaba con rol
/// Consulta, y Consulta alcanza el Tenant entero (<see cref="Roles.AlcanzaTodaLaOrganizacion"/>): la
/// «restricción» le abría todo el Tenant, notas internas incluidas. Una cuenta sin rol pasaba igual de
/// no ver nada a verlo todo.
/// </para>
/// </summary>
public class RestriccionLoginLocalNoAmpliaElRolTests
{
    private const string SinRol = "";

    /// <summary>El Tenant propietario declarado en <c>AzureAd:TenantHydraId</c>: el único cuyas cuentas entran por Microsoft.</summary>
    private static readonly Guid TenantDeclarado = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    public static TheoryData<string, string, string> RestringidaPorLoginLocal => new()
    {
        // rol real, rol efectivo esperado, perfil que decide el alcance esperado
        { Roles.Administrador, Roles.Administrador, Roles.Administrador },
        { Roles.DireccionCae, Roles.Consulta, Roles.DireccionCae },
        { Roles.CoordinadorCae, Roles.Consulta, Roles.CoordinadorCae },
        { Roles.GestorCae, Roles.Consulta, Roles.GestorCae },
        { Roles.Consulta, Roles.Consulta, Roles.Consulta },
        { Roles.Cliente, Roles.Cliente, Roles.Cliente },
        { SinRol, SinRol, SinRol },
    };

    [Theory]
    [MemberData(nameof(RestringidaPorLoginLocal))]
    public async Task Con_Entra_configurado_y_login_local_el_rol_efectivo_es_el_esperado(
        string rolReal, string rolEsperado, string perfilEsperado)
    {
        var servicio = await ServicioTrasLaRestriccionAsync(entraConfigurado: true, esSso: false, rolReal);

        (await servicio.ObtenerRolEfectivoAsync()).Should().Be(Nulo(rolEsperado));
        (await servicio.ObtenerPerfilQueDecideAlcanceAsync()).Should().Be(Nulo(perfilEsperado));
    }

    [Fact]
    public async Task Un_usuario_de_Cliente_entrado_con_contrasena_sigue_acotado_a_su_Cliente_empresarial()
    {
        var servicio = await ServicioTrasLaRestriccionAsync(entraConfigurado: true, esSso: false, Roles.Cliente);
        var rol = await servicio.ObtenerRolEfectivoAsync();

        rol.Should().Be(Roles.Cliente, "AlcanceDatosService acota por ClienteId solo con el rol Cliente");
        Roles.AlcanzaTodaLaOrganizacion(rol).Should().BeFalse("la restricción no puede abrirle el Tenant entero");
        Roles.ConEscrituraCsv.Split(',').Should().NotContain(rol);
    }

    [Fact]
    public async Task Una_cuenta_sin_rol_entrada_con_contrasena_no_recibe_Consulta()
    {
        var servicio = await ServicioTrasLaRestriccionAsync(entraConfigurado: true, esSso: false, SinRol);

        (await servicio.ObtenerRolEfectivoAsync()).Should().BeNull("sin rol no alcanza nada; Consulta alcanzaría el Tenant entero");
    }

    [Theory]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.GestorCae)]
    public async Task Un_rol_editor_entrado_con_contrasena_pierde_la_escritura(string rolReal)
    {
        var servicio = await ServicioTrasLaRestriccionAsync(entraConfigurado: true, esSso: false, rolReal);

        Roles.ConEscrituraCsv.Split(',').Should().NotContain(await servicio.ObtenerRolEfectivoAsync());
    }

    /// <summary>
    /// D-1 (decisión Chris 2026-10-25): la restricción alcanza solo a las cuentas del Tenant propietario
    /// declarado en <c>AzureAd:TenantHydraId</c>, las únicas que el inicio con Microsoft puede abrir. Una
    /// cuenta de otro Tenant propietario que entra con contraseña conserva su rol íntegro, sin perfil
    /// anterior, igual que si Entra no estuviera configurado.
    /// </summary>
    [Fact]
    public async Task Una_cuenta_de_otro_Tenant_propietario_entrada_con_contrasena_conserva_su_rol_integro()
    {
        var otroTenant = Guid.NewGuid().ToString();
        foreach (var rolReal in Roles.Todos.Append(SinRol))
        {
            var transformado = await Transformacion(entraConfigurado: true).TransformAsync(Principal(esSso: false, rolReal, otroTenant));
            var servicio = Servicio(transformado);

            transformado.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().Equal(rolReal.Length == 0 ? [] : [rolReal], $"rol real {rolReal}");
            transformado.HasClaim(c => c.Type == RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir)
                .Should().BeFalse($"rol real {rolReal}");
            (await servicio.ObtenerPerfilQueDecideAlcanceAsync()).Should().Be(Nulo(rolReal), $"rol real {rolReal}");
        }
    }

    [Theory]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.GestorCae)]
    public async Task Solo_la_cuenta_del_Tenant_declarado_pierde_la_escritura_con_contrasena(string rolReal)
    {
        var escritura = Roles.ConEscrituraCsv.Split(',');

        var delDeclarado = Servicio(await Transformacion(entraConfigurado: true).TransformAsync(Principal(esSso: false, rolReal)));
        var deOtro = Servicio(await Transformacion(entraConfigurado: true)
            .TransformAsync(Principal(esSso: false, rolReal, Guid.NewGuid().ToString())));

        escritura.Should().NotContain(await delDeclarado.ObtenerRolEfectivoAsync());
        (await deOtro.ObtenerRolEfectivoAsync()).Should().Be(rolReal);
        escritura.Should().Contain(rolReal, "control: el rol real es editor");
    }

    /// <summary>
    /// Fallo cerrado: sin Tenant declarado (nulo o vacío), nadie puede probar que su cuenta no es la que
    /// entraría por Microsoft, y la restricción sigue alcanzando a todas, como antes de D-1 (el callback
    /// rechaza entonces a todo el mundo y el arranque avisa, ver <c>AzureAdOptions.ProblemasDeConfiguracion</c>).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sin_Tenant_declarado_la_restriccion_alcanza_a_las_cuentas_de_todos_los_Tenants(bool vacio)
    {
        var transformacion = Transformacion(entraConfigurado: true, vacio ? Guid.Empty : null);

        var resultado = await transformacion.TransformAsync(Principal(esSso: false, Roles.GestorCae, Guid.NewGuid().ToString()));

        resultado.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().Equal([Roles.Consulta]);
        resultado.FindAll(RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir).Select(c => c.Value)
            .Should().Equal([Roles.GestorCae]);
    }

    /// <summary>
    /// Fallo cerrado: si no consta, sin ambigüedad, que la cuenta es de otro Tenant propietario —sin claim
    /// de Tenant, ilegible, vacío o con varios, aunque ninguno sea el declarado—, se restringe.
    /// </summary>
    [Theory]
    [InlineData("sin-claim")]
    [InlineData("")]
    [InlineData("no-es-un-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("varios-otros")]
    [InlineData("otro-y-el-declarado")]
    public async Task Si_no_consta_otro_Tenant_propietario_la_restriccion_se_aplica(string claimTenant)
    {
        var identidad = new ClaimsIdentity(authenticationType: "Prueba");
        identidad.AddClaim(new Claim(ClaimTypes.Role, Roles.GestorCae));
        string[] claims = claimTenant switch
        {
            "sin-claim" => [],
            "varios-otros" => [Guid.NewGuid().ToString(), Guid.NewGuid().ToString()],
            "otro-y-el-declarado" => [Guid.NewGuid().ToString(), TenantDeclarado.ToString()],
            _ => [claimTenant],
        };
        foreach (var claim in claims) identidad.AddClaim(new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, claim));

        var resultado = await Transformacion(entraConfigurado: true).TransformAsync(new ClaimsPrincipal(identidad));

        resultado.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().Equal([Roles.Consulta]);
    }

    /// <summary>Por SSO, o sin Entra configurado, la restricción es inerte: el rol real, sin perfil anterior.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Sin_restriccion_el_rol_efectivo_es_el_real_en_todos_los_roles(bool entraConfigurado, bool esSso)
    {
        foreach (var rolReal in Roles.Todos.Append(SinRol))
        {
            var principal = Principal(esSso, rolReal);
            var transformado = await Transformacion(entraConfigurado).TransformAsync(principal);
            var servicio = Servicio(transformado);

            (await servicio.ObtenerRolEfectivoAsync()).Should().Be(Nulo(rolReal), $"rol real {rolReal}");
            (await servicio.ObtenerPerfilQueDecideAlcanceAsync()).Should().Be(Nulo(rolReal), $"rol real {rolReal}");
            transformado.HasClaim(c => c.Type == RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir)
                .Should().BeFalse($"rol real {rolReal}");
        }
    }

    /// <summary>
    /// La invariante sobre el alcance de datos real (<see cref="AlcanceDatosService"/>), en el Tenant de
    /// origen: si la sesión limitada alcanza el Tenant entero, la misma cuenta sin limitar también. Antes
    /// del arreglo, Cliente y la cuenta sin rol (por el rol Consulta) y Gestor CAE y Coordinador CAE (por
    /// <c>RolQueDecideAlcanceAsync</c>, que solo miraba el perfil anterior por la vía de Operación) la
    /// rompían: con su rol real dependen de su Cliente empresarial o de su Asignación de Cartera.
    /// </summary>
    [Fact]
    public async Task En_el_Tenant_de_origen_la_sesion_limitada_nunca_alcanza_el_Tenant_entero_si_el_rol_real_no_lo_alcanza()
    {
        var tenant = TenantDeclarado;
        foreach (var rolReal in Roles.Todos.Append(SinRol).Append("RolInexistente"))
        {
            var real = await AccesoTotalAsync(entraConfigurado: false, rolReal, tenant);
            var limitado = await AccesoTotalAsync(entraConfigurado: true, rolReal, tenant);

            if (!real) limitado.Should().BeFalse($"rol real {rolReal}: la restricción solo quita escritura");
        }
    }

    [Theory]
    [InlineData(Roles.Administrador, true)]
    [InlineData(Roles.DireccionCae, true)]
    [InlineData(Roles.Consulta, true)]
    [InlineData(Roles.GestorCae, false)]
    [InlineData(Roles.CoordinadorCae, false)]
    [InlineData(Roles.Cliente, false)]
    [InlineData(SinRol, false)]
    public async Task En_el_Tenant_de_origen_el_acceso_total_de_la_sesion_limitada_es_el_del_rol_real(string rolReal, bool esperado)
    {
        var tenant = TenantDeclarado;

        (await AccesoTotalAsync(entraConfigurado: true, rolReal, tenant)).Should().Be(esperado);
        (await AccesoTotalAsync(entraConfigurado: false, rolReal, tenant)).Should().Be(esperado, "control: el rol real");
    }

    /// <summary>La transformación puede correr dos veces sobre el mismo principal: la segunda no cambia nada.</summary>
    [Theory]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.GestorCae)]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Cliente)]
    [InlineData(SinRol)]
    [InlineData("RolInexistente")]
    public async Task Es_idempotente_en_cada_rol(string rolReal)
    {
        var transformacion = Transformacion(entraConfigurado: true);
        var primera = await transformacion.TransformAsync(Principal(esSso: false, rolReal));
        var roles = primera.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        var perfil = primera.FindAll(RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir).Select(c => c.Value).ToList();

        var segunda = await transformacion.TransformAsync(primera);

        segunda.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().Equal(roles);
        segunda.FindAll(RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir).Select(c => c.Value)
            .Should().Equal(perfil).And.ContainSingle();
    }

    /// <summary>Varios roles a la vez no debería existir; si existe, gana el más acotado y nunca se amplía.</summary>
    [Theory]
    [InlineData(new[] { Roles.DireccionCae, Roles.Cliente }, new[] { Roles.Cliente }, Roles.Cliente)]
    [InlineData(new[] { Roles.Cliente, "RolInexistente" }, new[] { Roles.Cliente }, Roles.Cliente)]
    [InlineData(new[] { Roles.Consulta, Roles.Cliente }, new[] { Roles.Cliente }, Roles.Cliente)]
    [InlineData(new[] { Roles.GestorCae, "RolInexistente" }, new[] { Roles.Consulta }, Roles.GestorCae)]
    [InlineData(new[] { Roles.Administrador, Roles.Cliente }, new[] { Roles.Administrador, Roles.Cliente }, null)]
    public async Task Con_varios_roles_la_restriccion_no_amplia(string[] rolesReales, string[] rolesEsperados, string? perfilEsperado)
    {
        var identidad = new ClaimsIdentity(authenticationType: "Prueba");
        foreach (var rol in rolesReales) identidad.AddClaim(new Claim(ClaimTypes.Role, rol));

        var resultado = await Transformacion(entraConfigurado: true).TransformAsync(new ClaimsPrincipal(identidad));

        resultado.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().Equal(rolesEsperados);
        var perfil = resultado.FindFirst(RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir)?.Value;
        perfil.Should().Be(perfilEsperado, "Administrador es la vía de escape y no se restringe");
    }

    private static async Task<bool> AccesoTotalAsync(bool entraConfigurado, string rolReal, Guid tenantOrigen)
    {
        var principal = Principal(esSso: false, rolReal, tenantOrigen.ToString());
        var servicio = Servicio(await Transformacion(entraConfigurado).TransformAsync(principal));

        var tenantActual = new TenantActualAmbiental { TenantId = tenantOrigen };
        // Sin proveedor de base de datos a propósito: decidir el acceso total no puede consultar la base.
        var contexto = new CaeManagerDbContext(new DbContextOptionsBuilder<CaeManagerDbContext>().Options,
            DataProtectionProvider.Create(nameof(RestriccionLoginLocalNoAmpliaElRolTests)), tenantActual);
        var alcance = new AlcanceDatosService(contexto, servicio, tenantActual, new SesionPrivilegiadaAusente());

        return await alcance.TieneAccesoTotalAsync();
    }

    private static string? Nulo(string rol) => rol.Length == 0 ? null : rol;

    private static async Task<CurrentUserService> ServicioTrasLaRestriccionAsync(bool entraConfigurado, bool esSso, string rolReal) =>
        Servicio(await Transformacion(entraConfigurado).TransformAsync(Principal(esSso, rolReal)));

    private static RestriccionLoginLocalClaimsTransformation Transformacion(bool entraConfigurado) =>
        Transformacion(entraConfigurado, entraConfigurado ? TenantDeclarado : null);

    private static RestriccionLoginLocalClaimsTransformation Transformacion(bool entraConfigurado, Guid? tenantHydraId) =>
        new(Options.Create(new AzureAdOptions
        {
            TenantId = entraConfigurado ? "tenant" : null,
            ClientId = entraConfigurado ? "client" : null,
            ClientSecret = entraConfigurado ? "secreto" : null,
            TenantHydraId = tenantHydraId,
        }));

    /// <summary>
    /// La sesión de una cuenta con el rol dado. Sin <paramref name="claimsTenant"/>, la cuenta es del Tenant
    /// propietario declarado (<see cref="TenantDeclarado"/>), como la de un Gestor CAE del Operador CAE
    /// externo que entra por Microsoft; con ellos, lleva exactamente esos claims de Tenant.
    /// </summary>
    private static ClaimsPrincipal Principal(bool esSso, string rol, params string[] claimsTenant)
    {
        var identidad = new ClaimsIdentity(authenticationType: "Prueba");
        identidad.AddClaim(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));
        foreach (var claimTenant in claimsTenant.Length == 0 ? [TenantDeclarado.ToString()] : claimsTenant)
            identidad.AddClaim(new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, claimTenant));
        if (rol.Length > 0) identidad.AddClaim(new Claim(ClaimTypes.Role, rol));
        if (esSso)
            identidad.AddClaim(new Claim(
                RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin, RestriccionLoginLocalClaimsTransformation.MetodoLoginSso));
        return new ClaimsPrincipal(identidad);
    }

    private static CurrentUserService Servicio(ClaimsPrincipal usuario) =>
        new(new ProveedorFijo(usuario), new SinHttpContext(), new SinSeleccion(), new ServiceCollection().BuildServiceProvider());

    private sealed class ProveedorFijo(ClaimsPrincipal usuario) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(usuario));
    }

    private sealed class SinHttpContext : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => null; set => throw new NotSupportedException(); }
    }

    private sealed class SinSeleccion : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
