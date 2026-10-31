using System.Security.Claims;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Las rutas que identifican a una persona corren sin la selección del navegador, y solo ellas. El descarte vale para toda
/// la petición, también para los ámbitos que abra después, y deja la cookie intacta: la Sesión Privilegiada sigue abierta en
/// las demás pestañas. Lo que eso cambia en la identidad efectiva de PostgreSQL lo mide <c>FronteraCuentaFirmadaTests</c>.
/// </summary>
public class SeleccionDescartadaEnIdentificacionTests
{
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _sesionPrivilegiada = Guid.NewGuid();
    private readonly EphemeralDataProtectionProvider _proteccion = new();

    [Theory]
    [InlineData("/cuenta/iniciar-sesion", true)]
    [InlineData("/cuenta/verificar-2fa", true)]
    [InlineData("/cuenta/microsoft-callback", true)]
    [InlineData("/cuenta/no-he-sido-yo", true)]
    [InlineData("/CUENTA/INICIAR-SESION", true)]
    [InlineData("/cuenta/iniciar-sesion-microsoft", false)]
    [InlineData("/cuenta/cliente-activo", false)]
    [InlineData("/cuenta/soporte/salir", false)]
    [InlineData("/cuenta", false)]
    [InlineData("/", false)]
    public void Reconoce_las_rutas_por_segmentos(string ruta, bool esperado) =>
        SeleccionDescartadaEnIdentificacionMiddleware.EsRutaDeIdentificacion(ruta).Should().Be(esperado);

    [Fact]
    public async Task El_descarte_alcanza_a_un_ambito_nuevo_de_la_misma_peticion_y_deja_la_cookie_intacta()
    {
        var (contexto, cookie) = Peticion("/cuenta/iniciar-sesion");
        var seleccion = Seleccion(contexto);
        seleccion.SesionPrivilegiadaIdSeleccionada.Should().Be(_sesionPrivilegiada, "control: la cookie se lee antes del descarte");
        var siguienteLlamado = false;

        await new SeleccionDescartadaEnIdentificacionMiddleware(_ =>
        {
            siguienteLlamado = true;
            return Task.CompletedTask;
        }).InvokeAsync(contexto, seleccion);

        siguienteLlamado.Should().BeTrue("solo quita una coordenada, no corta la petición");
        seleccion.TenantIdSeleccionado.Should().BeNull();
        seleccion.SesionPrivilegiadaIdSeleccionada.Should().BeNull();

        var ambitoNuevo = Seleccion(contexto);
        ambitoNuevo.TenantIdSeleccionado.Should().BeNull();
        ambitoNuevo.AsignacionOperacionIdSeleccionada.Should().BeNull();
        ambitoNuevo.SesionPrivilegiadaIdSeleccionada.Should().BeNull();

        contexto.Request.Cookies[ClienteActivoSeleccionado.NombreCookie].Should().Be(cookie);
        contexto.Response.Headers.SetCookie.Should().BeEmpty("la Sesión Privilegiada sigue abierta en las demás pestañas");
    }

    [Theory]
    [InlineData("/cuenta/iniciar-sesion-microsoft")]
    [InlineData("/trabajadores")]
    public async Task Otra_ruta_conserva_la_seleccion(string ruta)
    {
        var (contexto, _) = Peticion(ruta);
        var seleccion = Seleccion(contexto);

        await new SeleccionDescartadaEnIdentificacionMiddleware(_ => Task.CompletedTask).InvokeAsync(contexto, seleccion);

        seleccion.SesionPrivilegiadaIdSeleccionada.Should().Be(_sesionPrivilegiada);
        Seleccion(contexto).TenantIdSeleccionado.Should().Be(_tenant, "un ámbito nuevo también la lee de la cookie");
    }

    private (DefaultHttpContext Contexto, string Cookie) Peticion(string ruta)
    {
        var contexto = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _usuario.ToString())], "test")),
        };
        contexto.Request.Path = ruta;
        var cookie = ClienteActivoSeleccionado.Proteger(_proteccion, _usuario, _tenant, null, _sesionPrivilegiada);
        contexto.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={cookie}";
        return (contexto, cookie);
    }

    private ClienteActivoSeleccionado Seleccion(HttpContext contexto) =>
        new(new HttpContextAccessor { HttpContext = contexto }, _proteccion);
}
