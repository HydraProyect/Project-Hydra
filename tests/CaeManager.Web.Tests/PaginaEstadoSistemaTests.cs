using Bunit;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// PaginaEstadoSistema, el shell de Error/not-found/acceso-denegado (Estados
/// Sistema TALVEG.dc.html). Cubre las dos propiedades que la revisión de
/// 2026-09-12 encontró sin test: "Reintentar" solo se ofrece cuando de
/// verdad puede repetir la petición que falló, y "acceso-denegado" nunca
/// ofrece un destino al que el usuario denegado no pueda llegar.
///
/// <para>
/// El reload de "Reintentar" (<c>window.location.reload()</c>) vivía en JS
/// nativo — bUnit con <see cref="JSRuntimeMode.Loose"/> no ejecuta interop,
/// así que ese comportamiento nunca fue observable aquí. Ahora "Reintentar"
/// es una ancla real (<c>Boton.Href</c>): se puede comprobar leyendo el
/// <c>href</c> del marcado, sin JS de por medio.
/// </para>
/// </summary>
public class PaginaEstadoSistemaTests : BunitContext
{
    public PaginaEstadoSistemaTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    // ------------------------------------------------------------ Reintentar

    [Fact]
    public void Sin_ruta_de_origen_conocida_no_ofrece_Reintentar()
    {
        var cut = Renderizar(TipoEstadoSistema.Error, rutaReintentar: null);

        cut.Markup.Should().NotContain("Reintentar",
            "sin ruta de origen no hay nada que repetir de verdad — prometer un reintento que solo recarga /Error induce a error");
    }

    [Theory]
    [InlineData("https://evil.example/panel")]
    [InlineData("//evil.example/panel")]
    [InlineData("/\\evil.example")]
    public void Una_ruta_de_origen_no_local_no_se_ofrece_como_Reintentar(string rutaNoLocal)
    {
        var cut = Renderizar(TipoEstadoSistema.Error, rutaReintentar: rutaNoLocal);

        cut.Markup.Should().NotContain("Reintentar",
            "una ruta absoluta o protocol-relative convertiría \"Reintentar\" en un redirect abierto");
    }

    [Fact]
    public void Con_ruta_de_origen_local_conocida_Reintentar_navega_ahi_de_verdad()
    {
        var cut = Renderizar(TipoEstadoSistema.Error, rutaReintentar: "/documentos/subir");

        var enlace = cut.Find("a.boton-secundario");
        enlace.TextContent.Should().Be("Reintentar");
        enlace.GetAttribute("href").Should().Be("/documentos/subir",
            "Reintentar debe repetir la petición que de verdad falló, no recargar /Error");
    }

    [Theory]
    [InlineData(TipoEstadoSistema.NoEncontrado)]
    [InlineData(TipoEstadoSistema.AccesoDenegado)]
    public void Solo_Error_ofrece_Reintentar_aunque_llegue_una_ruta_de_origen(TipoEstadoSistema tipo)
    {
        var cut = Renderizar(tipo, rutaReintentar: "/documentos");

        cut.Markup.Should().NotContain("Reintentar");
    }

    // ------------------------------------------------------- Acceso denegado

    [Fact]
    public void AccesoDenegado_no_atribuye_una_causa_que_no_conoce()
    {
        var cut = Renderizar(TipoEstadoSistema.AccesoDenegado, rol: Roles.Cliente);

        cut.Markup.Should().NotContain("cartera",
            "la pantalla no sabe si la cartera es la causa — no debe inventarla");
        cut.Markup.Should().NotContain("administrador",
            "no debe nombrar una figura concreta que quizá no es quien concede el acceso, ni incumplir el contrato de terminología");
    }

    [Fact]
    public void AccesoDenegado_no_ofrece_Comunicaciones_a_quien_no_puede_entrar_ahi()
    {
        // RolesComunicaciones.Gestion excluye a propósito el rol Cliente — un
        // contacto de una empresa cliente externa no puede leer el correo de
        // las demás. Ofrecerle este destino sería una segunda denegación.
        var cut = Renderizar(TipoEstadoSistema.AccesoDenegado, rol: Roles.Cliente);

        cut.Markup.Should().NotContain("/comunicaciones",
            "el rol Cliente no está en RolesComunicaciones.Gestion: el enlace lo dejaría en otra pantalla de acceso denegado");
    }

    [Fact]
    public void AccesoDenegado_ofrece_Comunicaciones_a_quien_si_puede_entrar_ahi()
    {
        var cut = Renderizar(TipoEstadoSistema.AccesoDenegado, rol: Roles.GestorCae, comunicacionesActivo: true);

        cut.Markup.Should().Contain("/comunicaciones",
            "un Gestor CAE sí está en RolesComunicaciones.Gestion y el módulo está activo: el destino es alcanzable de verdad");
    }

    [Fact]
    public void AccesoDenegado_no_ofrece_Comunicaciones_si_el_modulo_esta_congelado()
    {
        // Bandeja.razor redirige a /not-found cuando ComunicacionesOptions.Activo
        // es false (P2 #26): ofrecer el enlace aquí llevaría a esa misma ruta muerta
        // aunque el rol sea el correcto.
        var cut = Renderizar(TipoEstadoSistema.AccesoDenegado, rol: Roles.GestorCae, comunicacionesActivo: false);

        cut.Markup.Should().NotContain("/comunicaciones",
            "con el módulo congelado, /comunicaciones no es un destino alcanzable aunque el rol sea el correcto");
    }

    // ---------------------------------------------------------------- arnés

    private IRenderedComponent<PaginaEstadoSistema> Renderizar(
        TipoEstadoSistema tipo, string? rutaReintentar = null, string? rol = null, bool comunicacionesActivo = true)
    {
        Services.AddSingleton<IOptions<ComunicacionesOptions>>(
            Options.Create(new ComunicacionesOptions { Activo = comunicacionesActivo }));

        // AuthorizeView exige IAuthorizationPolicyProvider aunque la prueba no
        // necesite comprobar ningún rol — sin esto, el render de AccesoDenegado
        // falla por un servicio que falta, no por el producto.
        var autorizacion = AddAuthorization();
        if (rol is not null)
            autorizacion.SetAuthorized("usuaria-de-prueba").SetRoles(rol);

        return Render<PaginaEstadoSistema>(parametros => parametros
            .Add(p => p.Tipo, tipo)
            .Add(p => p.RutaReintentar, rutaReintentar));
    }
}
