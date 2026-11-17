using Bunit;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class TablaConversacionesTests : BunitContext
{
    public TablaConversacionesTests() => Services.AddLocalization();

    [Fact]
    public void La_tabla_muestra_empresa_estado_y_persona_asignada_y_abre_el_hilo()
    {
        var usuarioId = Guid.NewGuid();
        var conversacion = CrearConversacion() with { EjecutivoAsignadoId = usuarioId };
        Guid? abierta = null;
        var cut = Render<TablaConversaciones>(p => p
            .Add(c => c.Conversaciones, [conversacion])
            .Add(c => c.NombresAsignados, new Dictionary<Guid, string> { [usuarioId] = "Marta Olano" })
            .Add(c => c.OnAbrir, id => abierta = id));

        var celdas = cut.FindAll("[role=cell]");
        celdas[2].TextContent.Should().Be("Empresa de prueba");
        celdas[3].TextContent.Should().Be("Abierta");
        celdas[4].TextContent.Should().Be("Marta Olano");
        // Un botón nativo se activa con Intro/Espacio y emite click; no depende de un li con onclick.
        cut.Find("button").GetAttribute("type").Should().Be("button");
        cut.Find("button").Click();
        abierta.Should().Be(conversacion.Id);
    }

    [Fact]
    public void Sin_asignacion_no_inventa_una_persona_y_la_tabla_tiene_seis_columnas_sin_Empresa_gestionada()
    {
        var cut = Render<TablaConversaciones>(p => p.Add(c => c.Conversaciones, [CrearConversacion()]));

        cut.FindAll("[role=cell]")[4].TextContent.Should().Be("Sin asignar");
        cut.FindAll("[role=columnheader]").Select(e => e.TextContent.Trim())
            .Should().Equal("Prioridad", "Conversación", "Cliente empresarial", "Estado", "Asignada a", "Plazo");
        cut.Markup.Should().NotContain("SLA").And.NotContain("Empresa gestionada");
    }

    [Theory]
    [InlineData(-25, "Fuera de plazo · 25 min", "plazo-conversacion-vencido")]
    [InlineData(40, "Por vencer · 40 min", "plazo-conversacion-por-vencer")]
    [InlineData(300, "Quedan 5 h", "plazo-conversacion-holgado")]
    public void La_columna_Plazo_enseña_el_mismo_texto_y_tono_que_la_fila(int minutos, string texto, string clase)
    {
        var cut = Render<TablaConversaciones>(p => p.Add(c => c.Conversaciones,
            [CrearConversacion() with { SlaRestante = TimeSpan.FromMinutes(minutos), VencimientoSlaUtc = DateTime.UtcNow.AddMinutes(minutos) }]));

        var celda = cut.FindAll("[role=cell]")[5];
        celda.QuerySelector(".plazo-conversacion")!.TextContent.Trim().Should().Be(texto);
        celda.QuerySelector(".plazo-conversacion")!.ClassList.Should().Contain(clase);
    }

    [Fact]
    public void Un_plazo_a_cero_con_el_vencimiento_ya_pasado_es_fuera_de_plazo_y_no_por_vencer()
    {
        var cut = Render<TablaConversaciones>(p => p.Add(c => c.Conversaciones,
            [CrearConversacion() with { SlaRestante = TimeSpan.Zero, VencimientoSlaUtc = DateTime.UtcNow.AddHours(-10) }]));

        var plazo = cut.FindAll("[role=cell]")[5].QuerySelector(".plazo-conversacion")!;
        plazo.TextContent.Trim().Should().Be("Fuera de plazo");
        plazo.ClassList.Should().Contain("plazo-conversacion-vencido");
    }

    [Fact]
    public void Sin_plazo_la_celda_de_Plazo_queda_vacia()
    {
        var cut = Render<TablaConversaciones>(p => p.Add(c => c.Conversaciones, [CrearConversacion()]));

        cut.FindAll("[role=cell]")[5].TextContent.Trim().Should().BeEmpty();
    }

    [Fact]
    public void El_numero_de_ticket_va_delante_de_la_linea_del_remitente_y_el_cero_no_se_pinta()
    {
        var con = Render<TablaConversaciones>(p => p.Add(c => c.Conversaciones, [CrearConversacion() with { NumeroTicket = 10479 }]));
        var sin = Render<TablaConversaciones>(p => p.Add(c => c.Conversaciones, [CrearConversacion() with { NumeroTicket = 0 }]));

        con.Find(".tabla-conversaciones-remitente").TextContent.Should().StartWith("Nº 10479 · contacto@example.invalid")
            .And.MatchRegex(@" · \d{2}/\d{2}/\d{4} \d{2}:\d{2}$", "la fecha va separada del remitente por « · »");
        sin.Find(".tabla-conversaciones-remitente").TextContent.Should().StartWith("contacto@example.invalid").And.NotContain("Nº");
    }

    [Fact]
    public void La_nota_al_pie_da_la_regla_real_del_servidor_y_no_la_de_8_a_18_de_la_maqueta()
    {
        var cut = Render<TablaConversaciones>(p => p.Add(c => c.Conversaciones, [CrearConversacion()]));

        var nota = cut.Find(".tabla-conversaciones-nota").TextContent;
        nota.Should().Contain("de 8:00 a 10:00 y de 10:30 a 15:00, y de 16:00 a 17:00")
            .And.Contain("Crítica, 1 h. Urgente, 4 h. Normal, 1 día laborable.")
            .And.Contain("salvo los viernes, en julio y agosto y la víspera de un festivo")
            .And.Contain("Los festivos los fija cada Operador CAE.")
            .And.Contain("menos de 2 h");
        nota.Should().NotContain("8:00 a 18:00");
    }

    [Fact]
    public void La_prioridad_lleva_un_punto_decorativo_y_el_estado_es_texto_plano_sin_pastilla()
    {
        var cut = Render<TablaConversaciones>(p => p.Add(c => c.Conversaciones, [CrearConversacion()]));

        var prioridad = cut.Find("[role=cell].tabla-prioridad");
        prioridad.QuerySelector(".tabla-prioridad-punto")!.GetAttribute("aria-hidden").Should().Be("true");
        prioridad.TextContent.Trim().Should().NotBeEmpty("el punto no sustituye al texto de la prioridad");
        cut.Find("[role=cell].tabla-estado").TextContent.Should().Be("Abierta");
        cut.FindAll("[data-pieza='pastilla']").Should().BeEmpty();
    }

    private static ConversacionListaDto CrearConversacion() => new(
        Guid.NewGuid(), Guid.NewGuid(), "Empresa de prueba", "Documentación pendiente", EstadoConversacion.Abierta,
        null, "contacto@example.invalid", "Adjunto documentos.", DateTime.UtcNow, 1,
        CanalConversacion.Correo, null, DireccionMensaje.Entrante);
}
