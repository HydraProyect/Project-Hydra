using Bunit;
using CaeManager.Application.Documentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El motivo bajo la pastilla de estado de un propietario (Trabajadores; Vehículos y Empresas lo reutilizan):
/// el texto que se ve (<see cref="MotivoIncidenciasUi"/>) y el desglose con sus botones
/// (<see cref="MotivoIncidenciasDocumentales"/>).
/// </summary>
public class MotivoIncidenciasDocumentalesTests : BunitContext
{
    private static readonly DateOnly Hoy = new(2026, 10, 8);

    public MotivoIncidenciasDocumentalesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private static IncidenciaDocumentalDto Incidencia(string tipo, EstadoDocumento estado, int? diasHastaVencer = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), tipo, estado, diasHastaVencer is { } dias ? Hoy.AddDays(dias) : null);

    // --- El texto -----------------------------------------------------------------------------

    [Fact]
    public void Sin_incidencias_no_hay_motivo()
    {
        MotivoIncidenciasUi.Texto([], Hoy).Should().BeNull();
    }

    [Theory]
    [InlineData(EstadoDocumento.Urgente, 5, "Formación Art. 19 · Caduca en 5 días")]
    [InlineData(EstadoDocumento.Proximo, 20, "Formación Art. 19 · Caduca en 20 días")]
    [InlineData(EstadoDocumento.Urgente, 1, "Formación Art. 19 · Caduca en 1 día")]
    [InlineData(EstadoDocumento.Urgente, 0, "Formación Art. 19 · Caduca hoy")]
    public void Una_sola_por_vencer_dice_el_tipo_y_cuanto_le_queda(EstadoDocumento estado, int dias, string esperado)
    {
        MotivoIncidenciasUi.Texto([Incidencia("Formación Art. 19", estado, dias)], Hoy).Should().Be(esperado);
    }

    [Theory]
    [InlineData(EstadoDocumento.Vencido, -12)]
    [InlineData(EstadoDocumento.SinConfirmar, null)]
    public void Una_sola_vencida_o_sin_confirmar_dice_solo_el_tipo_porque_la_pastilla_ya_dice_que_le_pasa(
        EstadoDocumento estado, int? dias)
    {
        MotivoIncidenciasUi.Texto([Incidencia("Aptitud médica", estado, dias)], Hoy).Should().Be("Aptitud médica");
    }

    [Fact]
    public void Varias_dicen_cuantas_hay_de_cada_clase_de_la_mas_grave_a_la_menos()
    {
        var incidencias = new[]
        {
            Incidencia("Aptitud médica", EstadoDocumento.Vencido, -12),
            Incidencia("Contrato", EstadoDocumento.Vencido, -3),
            Incidencia("Formación Art. 19", EstadoDocumento.Urgente, 5),
            Incidencia("Entrega de EPI", EstadoDocumento.SinConfirmar),
        };

        MotivoIncidenciasUi.Texto(incidencias, Hoy).Should().Be("2 vencidos · 1 por vencer · 1 sin confirmar");
    }

    [Fact]
    public void Por_vencer_suma_Urgente_y_Proximo()
    {
        var incidencias = new[]
        {
            Incidencia("Formación Art. 19", EstadoDocumento.Urgente, 5),
            Incidencia("Entrega de EPI", EstadoDocumento.Proximo, 25),
            Incidencia("Aptitud médica", EstadoDocumento.Proximo, 28),
        };

        MotivoIncidenciasUi.Texto(incidencias, Hoy).Should().Be("3 por vencer");
    }

    [Fact]
    public void Solo_se_nombran_las_clases_que_hay()
    {
        var incidencias = new[]
        {
            Incidencia("Aptitud médica", EstadoDocumento.Vencido, -12),
            Incidencia("Entrega de EPI", EstadoDocumento.SinConfirmar),
            Incidencia("Contrato", EstadoDocumento.SinConfirmar),
        };

        MotivoIncidenciasUi.Texto(incidencias, Hoy).Should().Be("1 vencido · 2 sin confirmar");
    }

    [Fact]
    public void Un_tipo_que_ya_no_es_legible_se_nombra_como_Documento()
    {
        MotivoIncidenciasUi.Texto([Incidencia(string.Empty, EstadoDocumento.Vencido, -1)], Hoy).Should().Be("Documento");
    }

    [Theory]
    [InlineData(8, 10, "8/10")]
    [InlineData(0, 3, "0/3")]
    public void Registrados_vigentes_es_la_fraccion(int vigentes, int registrados, string esperado)
    {
        MotivoIncidenciasUi.RegistradosVigentes(vigentes, registrados).Should().Be(esperado);
    }

    [Fact]
    public void Sin_documentos_registrados_no_hay_fraccion()
    {
        MotivoIncidenciasUi.RegistradosVigentes(0, 0).Should().BeNull("la celda pinta una raya, no «0/0»");
    }

    // --- El componente ------------------------------------------------------------------------

    private IRenderedComponent<MotivoIncidenciasDocumentales> Renderizar(
        IReadOnlyList<IncidenciaDocumentalDto> incidencias, bool interactiva, Action<IncidenciaDocumentalDto>? alPulsar = null) =>
        Render<MotivoIncidenciasDocumentales>(parametros =>
        {
            parametros.Add(p => p.Incidencias, incidencias).Add(p => p.Interactiva, interactiva).Add(p => p.Hoy, Hoy);
            if (alPulsar is not null)
                parametros.Add(p => p.AlPulsar, EventCallback.Factory.Create(this, alPulsar));
        });

    [Fact]
    public void Sin_incidencias_no_pinta_nada()
    {
        Renderizar([], interactiva: true, _ => { }).Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Con_escritura_cada_incidencia_es_un_boton_con_su_estado_su_tipo_y_su_fecha()
    {
        var incidencias = new[]
        {
            Incidencia("Aptitud médica", EstadoDocumento.Vencido, -12),
            Incidencia("Formación Art. 19", EstadoDocumento.Urgente, 5),
            Incidencia("Entrega de EPI", EstadoDocumento.SinConfirmar),
        };

        var cut = Renderizar(incidencias, interactiva: true, _ => { });

        cut.Find(".motivo-incidencias-texto").TextContent.Trim().Should().Be("1 vencido · 1 por vencer · 1 sin confirmar");
        var disparador = cut.Find("button.ventana-contexto-disparador");
        disparador.GetAttribute("aria-label").Should().StartWith("1 vencido · 1 por vencer · 1 sin confirmar",
            "el nombre accesible empieza por el texto visible (WCAG 2.5.3)").And.Contain("3 documentos piden atención");
        cut.Find(".ventana-contexto-titulo").TextContent.Trim().Should().Be("3 documentos piden atención");

        var botones = cut.FindAll("button.ventana-contexto-elemento");
        botones.Should().HaveCount(3);
        botones.Select(b => b.QuerySelector("[data-pieza='pastilla']")!.TextContent.Trim()).Should().Equal("Vencido", "Por vencer", "Sin confirmar");
        botones.Select(b => b.QuerySelector(".motivo-incidencias-tipo")!.TextContent.Trim())
            .Should().Equal("Aptitud médica", "Formación Art. 19", "Entrega de EPI");
        botones[0].QuerySelector(".ventana-contexto-elemento-secundario")!.TextContent.Trim().Should().Be("26/09/2026");
        botones[2].QuerySelector(".ventana-contexto-elemento-secundario").Should().BeNull("sin fecha de vencimiento no hay fecha que enseñar");
        cut.Find(".ventana-contexto-pie").TextContent.Should().Contain("Clic en una para corregirla aquí");
    }

    [Fact]
    public async Task Pulsar_una_incidencia_avisa_con_esa_incidencia()
    {
        var incidencias = new[]
        {
            Incidencia("Aptitud médica", EstadoDocumento.Vencido, -12),
            Incidencia("Formación Art. 19", EstadoDocumento.Urgente, 5),
        };
        var pulsadas = new List<IncidenciaDocumentalDto>();
        var cut = Renderizar(incidencias, interactiva: true, pulsadas.Add);

        await cut.FindAll("button.ventana-contexto-elemento")[1].ClickAsync(new MouseEventArgs());

        pulsadas.Should().ContainSingle().Which.Should().BeSameAs(incidencias[1]);
    }

    [Fact]
    public void Quien_solo_consulta_ve_las_mismas_lineas_sin_botones()
    {
        var incidencias = new[]
        {
            Incidencia("Aptitud médica", EstadoDocumento.Vencido, -12),
            Incidencia("Entrega de EPI", EstadoDocumento.SinConfirmar),
        };

        var cut = Renderizar(incidencias, interactiva: false, _ => { });

        cut.FindAll("button").Should().BeEmpty("a quien solo consulta no se le ofrece un formulario que el comando le va a denegar");
        cut.Find(".ventana-contexto").GetAttribute("role").Should().Be("note");
        cut.Find(".ventana-contexto").GetAttribute("aria-label").Should().StartWith("1 vencido · 1 sin confirmar");
        var lineas = cut.FindAll(".ventana-linea");
        lineas.Should().HaveCount(2, "control positivo: el desglose se sigue viendo");
        lineas[0].TextContent.Should().Contain("Vencido").And.Contain("Aptitud médica").And.Contain("26/09/2026");
        cut.FindAll(".ventana-contexto-pie").Should().BeEmpty();
    }

    [Fact]
    public void Con_escritura_pero_sin_nadie_que_escuche_se_queda_en_lectura()
    {
        var cut = Renderizar([Incidencia("Aptitud médica", EstadoDocumento.Vencido, -12)], interactiva: true);

        cut.FindAll("button").Should().BeEmpty("sin AlPulsar no hay nada que abrir");
        cut.FindAll(".ventana-linea").Should().ContainSingle();
    }
}
