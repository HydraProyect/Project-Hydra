using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Panel de huecos de una macro. Lo que observa: la vista previa en vivo, que «Insertar» no se puede
/// pulsar hasta elegir TODO (control positivo: con todo elegido sí), que lo insertado sale escapado y
/// saneado con un nombre de Centro hostil, y que «Hoy» usa el día de negocio Europe/Madrid. No observa
/// el envío del correo ni la bandeja entera.
/// </summary>
public class MacroHuecosTests : BunitContext
{
    private const string Cuerpo = "Solicito el alta de {{centro}} para {{trabajador}} con efecto desde {{fecha}}.";
    private static readonly Guid CentroId = Guid.NewGuid();
    private static readonly Guid CentroHostilId = Guid.NewGuid();
    private static readonly Guid TrabajadorId = Guid.NewGuid();

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }

    public MacroHuecosTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddSingleton<ISanitizadorHtmlService, GanssSanitizadorHtmlService>();
        // 11/10 22:30 UTC = 12/10 00:30 en Madrid (CEST): «hoy» es el 12, no el 11.
        Services.AddSingleton<TimeProvider>(new RelojFijo(new DateTimeOffset(2026, 10, 11, 22, 30, 0, TimeSpan.Zero)));
    }

    private IRenderedComponent<MacroHuecos> Pinta(Action<string>? alInsertar = null, string cuerpo = Cuerpo) =>
        Render<MacroHuecos>(p => p
            .Add(c => c.Titulo, "Alta de Centro")
            .Add(c => c.Cuerpo, cuerpo)
            .Add(c => c.Centros, [
                new CentroSelectorDto(CentroId, "Centro Norte", "Cliente A", "Empresa A"),
                new CentroSelectorDto(CentroHostilId, "<img src=x onerror=alert(1)>Sur", "Cliente A", "Empresa A")])
            .Add(c => c.Trabajadores, [new TrabajadorSelectorDto(TrabajadorId, "M. Soto", null, null)])
            .Add(c => c.OnInsertar, EventCallback.Factory.Create<string>(this, t => alInsertar?.Invoke(t))));

    private static void Elige(IRenderedComponent<MacroHuecos> cut, string etiqueta, string valor) =>
        cut.FindAll("label").First(l => l.TextContent.Trim() == etiqueta).ParentElement!.QuerySelector("select, input")!.Change(valor);

    /// <summary>CampoTexto notifica con 300 ms de debounce: se espera a que la vista previa muestre el día.</summary>
    private static void EligeFecha(IRenderedComponent<MacroHuecos> cut)
    {
        cut.Find("input[type=date]").Input("2026-10-12");
        cut.WaitForAssertion(() => cut.Find("[data-testid=macro-huecos-previa]").TextContent.Should().Contain("12/10/2026"), TimeSpan.FromSeconds(15));
    }

    private static bool InsertarDeshabilitado(IRenderedComponent<MacroHuecos> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Contains("Insertar")).HasAttribute("disabled");

    [Fact]
    public void Sin_elegir_nada_la_vista_previa_marca_los_huecos_y_no_se_puede_insertar()
    {
        var cut = Pinta();

        cut.Find("[data-testid=macro-huecos-previa]").TextContent.Should().Contain("‹centro›").And.Contain("‹trabajador›").And.Contain("‹fecha›");
        InsertarDeshabilitado(cut).Should().BeTrue();
    }

    [Fact]
    public void Con_todo_elegido_la_vista_previa_se_actualiza_y_se_inserta()
    {
        string? insertado = null;
        var cut = Pinta(t => insertado = t);

        Elige(cut, "Centro", CentroId.ToString());
        Elige(cut, "Trabajador", TrabajadorId.ToString());
        EligeFecha(cut);

        cut.Find("[data-testid=macro-huecos-previa]").TextContent
            .Should().Be("Solicito el alta de Centro Norte para M. Soto con efecto desde 12/10/2026.");
        InsertarDeshabilitado(cut).Should().BeFalse();

        cut.FindAll("button").First(b => b.TextContent.Contains("Insertar")).Click();
        insertado.Should().Be("Solicito el alta de Centro Norte para M. Soto con efecto desde 12/10/2026.");
    }

    [Fact]
    public void Un_nombre_de_centro_con_marcado_no_llega_a_la_respuesta_como_marcado()
    {
        string? insertado = null;
        var cut = Pinta(t => insertado = t);

        Elige(cut, "Centro", CentroHostilId.ToString());
        Elige(cut, "Trabajador", TrabajadorId.ToString());
        EligeFecha(cut);
        cut.FindAll("button").First(b => b.TextContent.Contains("Insertar")).Click();

        insertado.Should().NotBeNull();
        insertado.Should().NotContain("<img").And.NotContain("onerror=alert(1)>");
        insertado.Should().Contain("&lt;img");
    }

    [Fact]
    public void Un_identificador_que_no_esta_en_los_catalogos_cuenta_como_sin_elegir()
    {
        var cut = Pinta();

        Elige(cut, "Centro", Guid.NewGuid().ToString());
        Elige(cut, "Trabajador", TrabajadorId.ToString());
        EligeFecha(cut);

        InsertarDeshabilitado(cut).Should().BeTrue();
    }

    [Fact]
    public void Hoy_es_el_dia_de_negocio_de_Madrid_no_el_de_UTC()
    {
        var cut = Pinta();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Hoy").Click();

        cut.Find("[data-testid=macro-huecos-previa]").TextContent.Should().Contain("12/10/2026");
    }

    [Fact]
    public void Solo_pide_los_huecos_que_la_macro_lleva()
    {
        var cut = Pinta(cuerpo: "Alta en {{centro}}.");

        cut.FindAll("label").Select(l => l.TextContent.Trim()).Should().Equal("Centro");
    }
}
