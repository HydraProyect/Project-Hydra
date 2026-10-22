using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
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

    private static string? Nulo(string rol) => rol.Length == 0 ? null : rol;

    private static async Task<CurrentUserService> ServicioTrasLaRestriccionAsync(bool entraConfigurado, bool esSso, string rolReal) =>
        Servicio(await Transformacion(entraConfigurado).TransformAsync(Principal(esSso, rolReal)));

    private static RestriccionLoginLocalClaimsTransformation Transformacion(bool entraConfigurado) =>
        new(Options.Create(new AzureAdOptions
        {
            TenantId = entraConfigurado ? "tenant" : null,
            ClientId = entraConfigurado ? "client" : null,
            ClientSecret = entraConfigurado ? "secreto" : null,
        }));

    private static ClaimsPrincipal Principal(bool esSso, string rol)
    {
        var identidad = new ClaimsIdentity(authenticationType: "Prueba");
        identidad.AddClaim(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));
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
