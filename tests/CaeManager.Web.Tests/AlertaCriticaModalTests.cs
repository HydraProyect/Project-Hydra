using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Comunicaciones.AlertasCriticas;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Incidencias;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Maqueta «Flujos 13 Comunicaciones», flujo «Alerta crítica»: modal que bloquea hasta decidir, con la segunda opinión de la IA
/// (coincide, no coincide, no disponible) y dos desenlaces. Las llamadas al modelo no existen aquí: la segunda opinión llega ya
/// calculada en el <see cref="AlertaCriticaDto"/>.
/// </summary>
public class AlertaCriticaModalTests : BunitContext
{
    private static readonly Guid CentroId = Guid.NewGuid();
    private static readonly Guid TrabajadorId = Guid.NewGuid();

    public AlertaCriticaModalTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private IRenderedComponent<AlertaCriticaModal> Renderizar(
        AlertaCriticaDto alerta,
        Action<AlertaCriticaModal.DatosIncidencia>? alRegistrar = null,
        Action? alNoEsCritica = null,
        bool visible = true) =>
        Render<AlertaCriticaModal>(p => p
            .Add(c => c.Visible, visible)
            .Add(c => c.Alerta, alerta)
            .Add(c => c.Asunto, "Obra Centro Norte")
            .Add(c => c.Centros, [new CentroSelectorDto(CentroId, "Centro Norte", "Constructora Norte", "Cubiertas Arrieta")])
            .Add(c => c.Trabajadores, [new TrabajadorSelectorDto(TrabajadorId, "Arrieta Sanz, Jon", null, null)])
            .Add(c => c.OnRegistrar, (AlertaCriticaModal.DatosIncidencia d) => alRegistrar?.Invoke(d))
            .Add(c => c.OnNoEsCritica, () => alNoEsCritica?.Invoke()));

    private static AlertaCriticaDto Alerta(bool? segundaOpinion, int? confianza) =>
        new("Está trabajando en cubierta sin el curso de altura", EstadoAlertaCritica.Pendiente, true, segundaOpinion, confianza);

    [Fact]
    public void Es_un_modal_bloqueante_de_560_px_con_dos_acciones_y_un_solo_primario()
    {
        var cut = Renderizar(Alerta(true, 91));

        cut.Find("h2").TextContent.Trim().Should().Be("Alerta crítica");
        // Bloquea hasta decidir: ni «Cancelar» ni X; solo las dos decisiones.
        cut.FindAll(".modal-pie button").Select(b => b.TextContent.Trim()).Should().Equal("No es crítica", "Registrar incidencia");
        cut.FindAll(".modal-pie .boton-primario").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Registrar incidencia");
        cut.FindAll("button[aria-label='Cerrar']").Should().BeEmpty();
        cut.Find(".alerta-critica-modal").Should().NotBeNull();
        cut.Markup.Should().Contain("Obra Centro Norte").And.Contain("Crítica");
    }

    [Fact]
    public void La_segunda_opinion_que_coincide_lo_dice_con_su_confianza()
    {
        var cut = Renderizar(Alerta(true, 91));

        var opinion = cut.Find(".alerta-critica-modal-opinion");
        opinion.TextContent.Should().Contain("Segunda opinión de la IA").And.Contain("Coincide").And.Contain("91 %");
        opinion.ClassList.Should().Contain("alerta-critica-modal-opinion-info");
    }

    [Fact]
    public void La_segunda_opinion_que_no_coincide_avisa_en_ambar_y_manda_leer_el_mensaje()
    {
        var cut = Renderizar(Alerta(false, 64));

        var opinion = cut.Find(".alerta-critica-modal-opinion");
        opinion.TextContent.Should().Contain("La segunda opinión no coincide").And.Contain("64 %").And.Contain("Lee el mensaje");
        opinion.ClassList.Should().Contain("alerta-critica-modal-opinion-aviso");
        // El mensaje que disparó la alerta sigue a la vista para leerlo antes de decidir.
        cut.Find("blockquote").TextContent.Should().Contain("sin el curso de altura");
    }

    [Fact]
    public void Sin_segunda_opinion_lo_dice_y_deja_decidir_con_el_texto()
    {
        var cut = Renderizar(Alerta(null, null));

        cut.Find(".alerta-critica-modal-opinion").TextContent.Should().Contain("Sin segunda opinión de la IA");
        cut.FindAll(".modal-pie button").Should().HaveCount(2);
    }

    [Fact]
    public void Registrar_incidencia_exige_un_Centro_y_entrega_lo_elegido()
    {
        AlertaCriticaModal.DatosIncidencia? datos = null;
        var cut = Renderizar(Alerta(true, 91), d => datos = d);

        var registrar = cut.Find(".modal-pie .boton-primario");
        registrar.HasAttribute("disabled").Should().BeTrue();
        registrar.GetAttribute("title").Should().Contain("Selecciona el Centro");

        var selects = cut.FindAll("select");
        selects[0].Change(CentroId.ToString());
        selects[1].Change(TrabajadorId.ToString());
        cut.Find(".modal-pie .boton-primario").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".modal-pie .boton-primario").Click();

        datos.Should().Be(new AlertaCriticaModal.DatosIncidencia(CentroId, TrabajadorId, TipoIncidencia.Incumplimiento, GravedadIncidencia.Grave));
    }

    [Fact]
    public void No_es_critica_avisa_a_la_pantalla_sin_pedir_Centro()
    {
        var noEsCritica = false;
        var cut = Renderizar(Alerta(false, 64), alNoEsCritica: () => noEsCritica = true);

        cut.FindAll(".modal-pie button")[0].Click();

        noEsCritica.Should().BeTrue();
    }

    [Fact]
    public void El_texto_de_la_alerta_se_pinta_como_texto_plano()
    {
        var cut = Renderizar(new AlertaCriticaDto("<script>alert(1)</script> denuncia", EstadoAlertaCritica.Pendiente, true, null, null));

        cut.FindAll(".alerta-critica-modal script").Should().BeEmpty();
        cut.Find("blockquote").TextContent.Should().Contain("<script>");
    }

    [Fact]
    public void Oculto_no_pinta_nada()
    {
        var cut = Renderizar(Alerta(true, 91), visible: false);

        cut.FindAll("h2").Should().BeEmpty();
    }
}
