using Bunit;
using CaeManager.Web.Features.AltaAutonoma.Antiabuso;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.AltaAutonoma;

/// <summary>
/// Marcado de los componentes antiabuso del alta: lo que el formulario SSR envía, lo que el guion del reto lee y lo que
/// ve (o no ve) un lector de pantalla. bUnit no ejecuta JavaScript: el Web Worker lo prueba
/// <c>js/retoPruebaTrabajo.test.mjs</c> y el recorrido en navegador llega con la página del alta (A3).
/// </summary>
public class ComponentesAntiabusoAltaTests : BunitContext
{
    private readonly RelojManual _reloj = new();
    private readonly OpcionesAntiabusoAlta _opciones = new() { DificultadBits = 8 };

    public ComponentesAntiabusoAltaTests()
    {
        Services.AddLocalization();
        Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        Services.AddSingleton<TimeProvider>(_reloj);
        Services.AddSingleton(Options.Create(_opciones));
        Services.AddSingleton<RegistroRetosUsados>();
        Services.AddSingleton<ServicioRetoPruebaTrabajo>();
        Services.AddSingleton<ServicioTrampasFormulario>();
    }

    [Fact]
    public void El_reto_viaja_en_campos_ocultos_y_el_token_pintado_se_puede_resolver()
    {
        var corte = Render<RetoPruebaTrabajoFormulario>();

        var contenedor = corte.Find("[data-reto-prueba-trabajo]");
        var token = contenedor.GetAttribute("data-reto")!;
        contenedor.GetAttribute("data-bits").Should().Be("8");
        contenedor.GetAttribute("data-espera-minima-ms").Should().Be("3000", "el guion no repite un envío antes de la trampa de tiempo");
        contenedor.GetAttribute("data-vigencia-ms").Should().Be("1190000", "20 min del servidor menos los 10 s de margen del guion");
        contenedor.GetAttribute("data-texto-caducado").Should().NotBeNullOrWhiteSpace();
        contenedor.GetAttribute("data-trabajador").Should().EndWith("js/reto-prueba-trabajo-worker.js");

        corte.Find($"input[type=hidden][name={RetoPruebaTrabajoFormulario.CampoReto}]").GetAttribute("value").Should().Be(token);
        var solucion = corte.Find($"input[type=hidden][name={RetoPruebaTrabajoFormulario.CampoSolucion}]");
        solucion.GetAttribute("value").Should().BeEmpty("lo rellena el guion al resolver");
        solucion.HasAttribute("data-reto-solucion").Should().BeTrue();

        var nonce = Enumerable.Range(0, 100_000).Select(n => n.ToString())
            .First(n => ServicioRetoPruebaTrabajo.BitsCeroIniciales(token, n) >= 8);
        Services.GetRequiredService<ServicioRetoPruebaTrabajo>().Verificar(token, nonce)
            .Should().Be(ResultadoVerificacionReto.Valido);
    }

    [Fact]
    public void El_estado_se_anuncia_en_una_region_educada_y_con_textos_localizados()
    {
        var corte = Render<RetoPruebaTrabajoFormulario>();

        var estado = corte.Find("[data-reto-estado]");
        estado.GetAttribute("role").Should().Be("status");
        estado.GetAttribute("aria-live").Should().Be("polite");
        estado.TextContent.Should().Be("Comprobando que el envío lo hace una persona…");

        var contenedor = corte.Find("[data-reto-prueba-trabajo]");
        contenedor.GetAttribute("data-texto-listo").Should().Be("Comprobación completada.");
        contenedor.GetAttribute("data-texto-esperando").Should().NotBeNullOrWhiteSpace();
        contenedor.GetAttribute("data-texto-error").Should().NotBeNullOrWhiteSpace();
        corte.Find("noscript").TextContent.Should().Contain("JavaScript");
        corte.FindAll("[tabindex]:not([tabindex='-1']), button, a").Should().BeEmpty("el reto no pide ninguna interacción");
    }

    [Fact]
    public void Cada_pintado_emite_un_reto_nuevo()
    {
        var primero = Render<RetoPruebaTrabajoFormulario>().Find("[data-reto-prueba-trabajo]").GetAttribute("data-reto");
        var segundo = Render<RetoPruebaTrabajoFormulario>().Find("[data-reto-prueba-trabajo]").GetAttribute("data-reto");

        primero.Should().NotBe(segundo);
    }

    [Fact]
    public void El_campo_trampa_queda_fuera_de_la_vista_del_teclado_y_del_lector_de_pantalla()
    {
        var corte = Render<TrampasFormularioAlta>(p => p.Add(c => c.TokenReto, "reto"));

        var trampa = corte.Find($"input[name={ServicioTrampasFormulario.NombreCampoTrampa}]");
        trampa.GetAttribute("type").Should().Be("text");
        trampa.GetAttribute("value").Should().BeEmpty();
        trampa.GetAttribute("tabindex").Should().Be("-1");
        trampa.GetAttribute("autocomplete").Should().Be("off");

        var envoltorio = trampa.ParentElement!;
        envoltorio.GetAttribute("aria-hidden").Should().Be("true");
        envoltorio.GetAttribute("style").Should().Contain("left:-10000px").And.Contain("overflow:hidden");
        corte.Find($"label[for={trampa.Id}]").TextContent.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void El_formulario_del_reto_pinta_un_sello_atado_a_su_reto_que_solo_pasa_a_los_tres_segundos()
    {
        var corte = Render<RetoPruebaTrabajoFormulario>();
        var token = corte.Find("[data-reto-prueba-trabajo]").GetAttribute("data-reto");
        var sello = corte.Find($"input[type=hidden][name={ServicioTrampasFormulario.NombreCampoSello}]").GetAttribute("value");
        corte.FindAll($"input[name={ServicioTrampasFormulario.NombreCampoTrampa}]").Should().ContainSingle();
        var otroReto = Render<RetoPruebaTrabajoFormulario>().Find("[data-reto-prueba-trabajo]").GetAttribute("data-reto");
        var servicio = Services.GetRequiredService<ServicioTrampasFormulario>();

        servicio.Verificar(null, sello, token).Should().Be(ResultadoTrampasFormulario.EnvioDemasiadoRapido);
        _reloj.Avanzar(TimeSpan.FromSeconds(3));
        servicio.Verificar(null, sello, otroReto).Should().Be(ResultadoTrampasFormulario.SelloDeOtroReto);
        servicio.Verificar(null, sello, token).Should().Be(ResultadoTrampasFormulario.Superadas);
    }

    [Theory]
    [InlineData(5_000L, "1")]
    [InlineData(29_985_000L, "2999")]
    [InlineData(30_000_000L, "3000")]
    public void La_espera_del_guion_se_redondea_hacia_arriba_y_nunca_se_adelanta_al_servidor(long ticks, string esperado)
    {
        _opciones.TiempoMinimoEnvio = TimeSpan.FromTicks(ticks);

        var espera = Render<RetoPruebaTrabajoFormulario>().Find("[data-reto-prueba-trabajo]").GetAttribute("data-espera-minima-ms");

        espera.Should().Be(esperado);
    }
}
