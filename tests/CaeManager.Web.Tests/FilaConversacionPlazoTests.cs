using Bunit;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El plazo, la prioridad y el Nº de ticket en la tercera línea de la fila de la lista (maqueta 2026-11-01, E10). Los tres tonos del plazo salen de
/// <c>ConversacionListaDto.SlaRestante</c> tal cual lo da el servidor (negativo si venció, nulo si no hay plazo): la fila no recalcula nada.
/// </summary>
public class FilaConversacionPlazoTests : BunitContext
{
    public FilaConversacionPlazoTests() => Services.AddLogging().AddLocalization();

    [Fact]
    public void Fuera_de_plazo_se_pinta_en_peligro_con_el_tiempo_vencido()
    {
        var plazo = Plazo(Render(Crear(TimeSpan.FromMinutes(-25))));

        plazo.TextContent.Trim().Should().Be("Fuera de plazo · 25 min");
        plazo.ClassList.Should().Contain("plazo-conversacion-vencido");
    }

    [Fact]
    public void Por_vencer_con_menos_de_dos_horas_se_pinta_en_advertencia()
    {
        var plazo = Plazo(Render(Crear(TimeSpan.FromMinutes(40))));

        plazo.TextContent.Trim().Should().Be("Por vencer · 40 min");
        plazo.ClassList.Should().Contain("plazo-conversacion-por-vencer");
    }

    [Fact]
    public void Con_tiempo_de_sobra_dice_cuanto_queda_en_el_tono_apagado()
    {
        var plazo = Plazo(Render(Crear(TimeSpan.FromHours(5))));

        plazo.TextContent.Trim().Should().Be("Quedan 5 h");
        plazo.ClassList.Should().Contain("plazo-conversacion-holgado")
            .And.NotContain("plazo-conversacion-por-vencer").And.NotContain("plazo-conversacion-vencido");
    }

    [Theory]
    [InlineData(119, "Por vencer · 1 h 59", "plazo-conversacion-por-vencer")]
    [InlineData(120, "Quedan 2 h", "plazo-conversacion-holgado")]
    [InlineData(80, "Por vencer · 1 h 20", "plazo-conversacion-por-vencer")]
    public void El_umbral_de_por_vencer_son_dos_horas_exactas_y_las_horas_llevan_sus_minutos(int minutos, string texto, string clase)
    {
        var plazo = Plazo(Render(Crear(TimeSpan.FromMinutes(minutos))));

        plazo.TextContent.Trim().Should().Be(texto);
        plazo.ClassList.Should().Contain(clase);
    }

    [Fact]
    public void Un_plazo_a_punto_de_vencer_no_se_redondea_a_cero_minutos()
    {
        Plazo(Render(Crear(TimeSpan.FromSeconds(20)))).TextContent.Trim().Should().Be("Por vencer · 1 min");
        Plazo(Render(Crear(TimeSpan.FromSeconds(-20)))).TextContent.Trim().Should().Be("Fuera de plazo · 1 min");
    }

    [Fact]
    public void Sin_plazo_no_pinta_nada()
    {
        var fila = Render(Crear(null));

        fila.FindAll(".plazo-conversacion").Should().BeEmpty();
        fila.Markup.Should().NotContain("Sin plazo");
    }

    [Fact]
    public void El_titulo_del_plazo_da_la_regla_y_el_vencimiento_en_hora_peninsular_sin_hablar_de_SLA()
    {
        var vence = new DateTime(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc);
        var fila = Render(Crear(TimeSpan.FromHours(3), PrioridadConversacion.Urgente) with { VencimientoSlaUtc = vence });

        var titulo = Plazo(fila).GetAttribute("title")!;
        titulo.Should().Be("Urgente: 4 h laborables · Vence el 17/11/2026 10:00 (horario laboral)");
        titulo.Should().NotContain("SLA");
    }

    [Fact]
    public void El_numero_de_ticket_va_sin_prefijo_y_el_cero_significa_sin_numero()
    {
        Render(Crear(TimeSpan.FromHours(5)) with { NumeroTicket = 10479 }).Find(".fila-ticket").TextContent.Trim().Should().Be("Nº 10479");
        Render(Crear(TimeSpan.FromHours(5)) with { NumeroTicket = 0 }).FindAll(".fila-ticket").Should().BeEmpty();
    }

    [Theory]
    [InlineData(PrioridadConversacion.Normal, "Normal", false)]
    [InlineData(PrioridadConversacion.Urgente, "Urgente", true)]
    [InlineData(PrioridadConversacion.Critica, "Crítica", true)]
    public void La_prioridad_lleva_un_punto_decorativo_y_su_texto_y_el_rojo_es_solo_de_urgente_y_critica(
        PrioridadConversacion prioridad, string texto, bool peligro)
    {
        var fila = Render(Crear(null, prioridad));

        var pieza = fila.Find(".fila-prioridad");
        pieza.TextContent.Trim().Should().Be(texto);
        pieza.QuerySelector(".fila-prioridad-punto")!.GetAttribute("aria-hidden").Should().Be("true");
        pieza.ClassList.Contains("fila-prioridad-urgente").Should().Be(prioridad == PrioridadConversacion.Urgente);
        pieza.ClassList.Contains("fila-prioridad-critica").Should().Be(prioridad == PrioridadConversacion.Critica);
        (pieza.ClassList.Contains("fila-prioridad-urgente") || pieza.ClassList.Contains("fila-prioridad-critica")).Should().Be(peligro);
    }

    [Fact]
    public void La_tercera_linea_junta_plazo_prioridad_y_ticket_y_la_fila_no_trae_casilla_de_lote()
    {
        var fila = Render(Crear(TimeSpan.FromMinutes(40)) with { NumeroTicket = 10479 });

        var linea = fila.Find(".bandeja-fila-linea3");
        linea.QuerySelector(".plazo-conversacion")!.TextContent.Trim().Should().StartWith("Por vencer");
        linea.QuerySelector(".fila-prioridad")!.TextContent.Trim().Should().Be("Normal");
        linea.QuerySelector(".fila-ticket")!.TextContent.Trim().Should().Be("Nº 10479");
        // Bloqueados o fuera de alcance: monograma de Empresa gestionada, casilla de lote y buzón.
        fila.FindAll("input[type=checkbox]").Should().BeEmpty();
    }

    private IRenderedComponent<FilaConversacion> Render(ConversacionListaDto conversacion) =>
        Render<FilaConversacion>(p => p.Add(c => c.Conversacion, conversacion));

    private static AngleSharp.Dom.IElement Plazo(IRenderedComponent<FilaConversacion> fila) => fila.Find(".plazo-conversacion");

    private static ConversacionListaDto Crear(TimeSpan? restante, PrioridadConversacion prioridad = PrioridadConversacion.Normal) => new(
        Guid.NewGuid(), null, null, "Asunto", EstadoConversacion.Abierta, null, "contacto@ejemplo.test", "Texto", DateTime.UtcNow, 1,
        CanalConversacion.Correo, null, DireccionMensaje.Entrante, Prioridad: prioridad,
        VencimientoSlaUtc: restante is null ? null : DateTime.UtcNow + restante, SlaRestante: restante);
}
