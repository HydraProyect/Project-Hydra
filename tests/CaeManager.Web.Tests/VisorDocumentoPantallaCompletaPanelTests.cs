using Bunit;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CaeManager.Web.Tests;

/// <summary>
/// El panel de la derecha del visor de documento a pantalla completa (L-66 y L-67, PR-A3a): el visor es del Núcleo y no conoce quién
/// pone el panel; aquí se fija el contrato del hueco <c>Panel</c> con un panel de pega: lo que recibe (adjunto actual con su estado,
/// pendientes, total), lo que el visor hace con lo que devuelve (resolver, dejar para después, teclas) y el botón de la cabecera que
/// lo muestra u oculta. El panel real de revisión de adjuntos vive en Comunicaciones y tiene sus propios tests.
/// </summary>
public class VisorDocumentoPantallaCompletaPanelTests : BunitContext
{
    /// <summary>Panel de pega: pinta lo que recibe y reenvía las salidas del visor, como lo haría el panel real.</summary>
    private sealed class PanelDePega : ComponentBase
    {
        [Parameter, EditorRequired] public ContextoPanelVisor Contexto { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            b.OpenElement(0, "aside");
            b.AddAttribute(1, "id", "vz-revision");
            b.AddAttribute(2, "data-nombre", Contexto.Adjunto.NombreArchivo);
            b.AddAttribute(3, "data-estado", Contexto.Adjunto.Estado.ToString());
            b.AddAttribute(4, "data-pendientes", Contexto.Pendientes);
            b.AddAttribute(5, "data-total", Contexto.Total);
            b.AddAttribute(6, "onkeydown", Contexto.Tecla);
            b.AddEventStopPropagationAttribute(7, "onkeydown", true);
            Boton(b, 10, "asignar", () => Contexto.Resolver.InvokeAsync(new ResolucionAdjuntoVisor(Contexto.Adjunto.Id, EstadoAdjuntoVisor.Asignado)));
            Boton(b, 20, "rechazar", () => Contexto.Resolver.InvokeAsync(new ResolucionAdjuntoVisor(Contexto.Adjunto.Id, EstadoAdjuntoVisor.Rechazado)));
            Boton(b, 30, "reabrir", () => Contexto.Resolver.InvokeAsync(new ResolucionAdjuntoVisor(Contexto.Adjunto.Id, EstadoAdjuntoVisor.Pendiente)));
            Boton(b, 40, "dejar", () => Contexto.Dejar.InvokeAsync());
            b.CloseElement();
        }

        private void Boton(RenderTreeBuilder b, int secuencia, string clase, Func<Task> alPulsar)
        {
            b.OpenElement(secuencia, "button");
            b.AddAttribute(secuencia + 1, "type", "button");
            b.AddAttribute(secuencia + 2, "class", clase);
            b.AddAttribute(secuencia + 3, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, alPulsar));
            b.CloseElement();
        }
    }

    private static AdjuntoVisor Adj(string nombre, EstadoAdjuntoVisor estado = EstadoAdjuntoVisor.Pendiente, int paginas = 1) =>
        new(Guid.NewGuid(), nombre, $"/comunicaciones/adjuntos/{Guid.NewGuid()}/archivo", paginas, "312 KB", estado);

    private IRenderedComponent<VisorDocumentoPantallaCompleta> Pintar(
        IReadOnlyList<AdjuntoVisor> lista, bool conPanel = true, int indice = 0,
        List<ResolucionAdjuntoVisor>? resueltos = null, Action<bool>? alCambiar = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        return Render<VisorDocumentoPantallaCompleta>(p =>
        {
            p.Add(x => x.Visible, true)
             .Add(x => x.Adjuntos, lista)
             .Add(x => x.IndiceInicial, indice)
             .Add(x => x.VisibleChanged, EventCallback.Factory.Create<bool>(this, v => alCambiar?.Invoke(v)))
             .Add(x => x.AdjuntoResuelto, EventCallback.Factory.Create<ResolucionAdjuntoVisor>(this, r => resueltos?.Add(r)));
            if (conPanel)
                p.Add(x => x.Panel, ctx => b =>
                {
                    b.OpenComponent<PanelDePega>(0);
                    b.AddComponentParameter(1, nameof(PanelDePega.Contexto), ctx);
                    b.CloseComponent();
                });
        });
    }

    private static string Posicion(IRenderedComponent<VisorDocumentoPantallaCompleta> cut) => cut.Find(".vz-cola-n").TextContent.Trim();

    private static AngleSharp.Dom.IElement Panel(IRenderedComponent<VisorDocumentoPantallaCompleta> cut) => cut.Find("aside#vz-revision");

    private static AngleSharp.Dom.IElement BotonRevision(IRenderedComponent<VisorDocumentoPantallaCompleta> cut) =>
        cut.Find("button[aria-controls='vz-revision']");

    private static Task TeclaPanelAsync(IRenderedComponent<VisorDocumentoPantallaCompleta> cut, string tecla) =>
        Panel(cut).KeyDownAsync(new KeyboardEventArgs { Key = tecla });

    [Fact]
    public void Sin_Panel_el_visor_es_de_lectura_sin_boton_de_revision_ni_panel()
    {
        var cut = Pintar([Adj("A.pdf")], conPanel: false);

        cut.FindAll("aside#vz-revision").Should().BeEmpty();
        cut.FindAll("button[aria-controls='vz-revision']").Should().BeEmpty();
        cut.Markup.Should().NotContain("revisión");
    }

    [Fact]
    public void Con_Panel_el_boton_de_la_cabecera_muestra_y_oculta_la_revision()
    {
        var cut = Pintar([Adj("A.pdf")]);

        // Abierto de partida: el lienzo lo pinta a la derecha y el botón ofrece ocultarlo.
        Panel(cut).Should().NotBeNull();
        BotonRevision(cut).TextContent.Trim().Should().Be("Ocultar la revisión");
        BotonRevision(cut).GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("header.vz-cab").QuerySelector("button[aria-controls='vz-revision']").Should().NotBeNull();

        BotonRevision(cut).Click();

        cut.FindAll("aside#vz-revision").Should().BeEmpty();
        BotonRevision(cut).TextContent.Trim().Should().Be("Ver la revisión");
        BotonRevision(cut).GetAttribute("aria-expanded").Should().Be("false");

        BotonRevision(cut).Click();

        Panel(cut).Should().NotBeNull();
        BotonRevision(cut).TextContent.Trim().Should().Be("Ocultar la revisión");
    }

    [Fact]
    public void El_panel_recibe_el_adjunto_actual_los_pendientes_y_el_total()
    {
        var cut = Pintar([Adj("A.pdf", EstadoAdjuntoVisor.Asignado), Adj("B.pdf"), Adj("C.pdf", EstadoAdjuntoVisor.Rechazado), Adj("D.pdf")], indice: 1);

        Panel(cut).GetAttribute("data-nombre").Should().Be("B.pdf");
        Panel(cut).GetAttribute("data-estado").Should().Be("Pendiente");
        Panel(cut).GetAttribute("data-pendientes").Should().Be("2");
        Panel(cut).GetAttribute("data-total").Should().Be("4");

        cut.FindAll(".vz-marca")[2].Click();

        Panel(cut).GetAttribute("data-nombre").Should().Be("C.pdf");
        Panel(cut).GetAttribute("data-estado").Should().Be("Rechazado");
    }

    [Fact]
    public void Resolver_marca_el_adjunto_pasa_al_siguiente_pendiente_y_avisa_a_quien_abrio_el_visor()
    {
        var a = Adj("A.pdf");
        var b = Adj("B.pdf", EstadoAdjuntoVisor.Asignado);
        var c = Adj("C.pdf");
        var resueltos = new List<ResolucionAdjuntoVisor>();
        var cut = Pintar([a, b, c], resueltos: resueltos);

        Panel(cut).QuerySelector("button.asignar")!.Click();

        resueltos.Should().Equal(new ResolucionAdjuntoVisor(a.Id, EstadoAdjuntoVisor.Asignado));
        cut.FindAll(".vz-marca")[0].ClassList.Should().Contain("vz-marca-asignado");
        // Salta el B, ya resuelto, y se queda en el C; el panel ve un solo pendiente.
        Posicion(cut).Should().Be("Documento 3 de 3");
        Panel(cut).GetAttribute("data-nombre").Should().Be("C.pdf");
        Panel(cut).GetAttribute("data-pendientes").Should().Be("1");

        // Rechazar el último pendiente: da la vuelta a la cola y, sin más pendientes, se queda en el adjunto.
        Panel(cut).QuerySelector("button.rechazar")!.Click();

        resueltos.Should().HaveCount(2);
        resueltos[1].Should().Be(new ResolucionAdjuntoVisor(c.Id, EstadoAdjuntoVisor.Rechazado));
        cut.FindAll(".vz-marca")[2].ClassList.Should().Contain("vz-marca-rechazado");
        Posicion(cut).Should().Be("Documento 3 de 3");
        cut.Find(".vz-cola").TextContent.Should().Contain("Todos los adjuntos revisados");
    }

    [Fact]
    public void Resolver_el_ultimo_adjunto_da_la_vuelta_a_la_cola_hasta_el_primer_pendiente()
    {
        var cut = Pintar([Adj("A.pdf"), Adj("B.pdf", EstadoAdjuntoVisor.Asignado), Adj("C.pdf")], indice: 2);

        Panel(cut).QuerySelector("button.asignar")!.Click();

        Posicion(cut).Should().Be("Documento 1 de 3");
    }

    [Fact]
    public void Volver_a_revisar_un_adjunto_lo_deja_pendiente_y_no_mueve_la_cola()
    {
        var a = Adj("A.pdf", EstadoAdjuntoVisor.Rechazado);
        var resueltos = new List<ResolucionAdjuntoVisor>();
        var cut = Pintar([a, Adj("B.pdf")], resueltos: resueltos);
        Panel(cut).GetAttribute("data-pendientes").Should().Be("1");

        Panel(cut).QuerySelector("button.reabrir")!.Click();

        resueltos.Should().Equal(new ResolucionAdjuntoVisor(a.Id, EstadoAdjuntoVisor.Pendiente));
        Posicion(cut).Should().Be("Documento 1 de 2");
        Panel(cut).GetAttribute("data-estado").Should().Be("Pendiente");
        Panel(cut).GetAttribute("data-pendientes").Should().Be("2");
        cut.FindAll(".vz-marca")[0].ClassList.Should().NotContain("vz-marca-rechazado");
    }

    [Fact]
    public void Dejar_para_despues_pasa_al_siguiente_pendiente_y_sin_otro_se_queda()
    {
        var cut = Pintar([Adj("A.pdf"), Adj("B.pdf", EstadoAdjuntoVisor.Asignado), Adj("C.pdf")]);

        Panel(cut).QuerySelector("button.dejar")!.Click();
        Posicion(cut).Should().Be("Documento 3 de 3");

        // Dejar no resuelve nada: el adjunto sigue pendiente y la cola da la vuelta.
        Panel(cut).QuerySelector("button.dejar")!.Click();
        Posicion(cut).Should().Be("Documento 1 de 3");
        cut.FindAll(".vz-marca")[0].ClassList.Should().NotContain("vz-marca-asignado");
    }

    [Fact]
    public void Dejar_para_despues_sin_otro_pendiente_no_mueve_la_cola()
    {
        var cut = Pintar([Adj("A.pdf"), Adj("B.pdf", EstadoAdjuntoVisor.Rechazado)]);

        Panel(cut).QuerySelector("button.dejar")!.Click();

        Posicion(cut).Should().Be("Documento 1 de 2");
    }

    [Fact]
    public void Lo_resuelto_en_el_visor_se_ve_aunque_la_lista_aun_llegue_sin_refrescar_y_cede_cuando_la_lista_coincide()
    {
        var a = Adj("A.pdf");
        var b = Adj("B.pdf");
        var cut = Pintar([a, b]);

        Panel(cut).QuerySelector("button.asignar")!.Click();
        // Quien abrió el visor todavía no refrescó: la marca resuelta no se pierde.
        cut.Render(p => p.Add(x => x.Adjuntos, [a, b]));
        cut.FindAll(".vz-marca")[0].ClassList.Should().Contain("vz-marca-asignado");

        // Refresca con el mismo estado: la lista vuelve a mandar y un cambio posterior de fuera se respeta.
        cut.Render(p => p.Add(x => x.Adjuntos, [a with { Estado = EstadoAdjuntoVisor.Asignado }, b]));
        cut.FindAll(".vz-marca")[0].ClassList.Should().Contain("vz-marca-asignado");
        cut.Render(p => p.Add(x => x.Adjuntos, [a, b]));
        cut.FindAll(".vz-marca")[0].ClassList.Should().NotContain("vz-marca-asignado");
    }

    [Fact]
    public async Task Las_teclas_del_panel_no_cambian_de_adjunto_pero_si_de_hoja_zoom_y_cierran()
    {
        var cierres = new List<bool>();
        var cut = Pintar([Adj("A.pdf", paginas: 3), Adj("B.pdf")], alCambiar: cierres.Add);

        // Las flechas del panel desplazan el panel: no cambian de adjunto.
        await TeclaPanelAsync(cut, "ArrowRight");
        Posicion(cut).Should().Be("Documento 1 de 2");

        await TeclaPanelAsync(cut, "]");
        cut.FindAll(".vz-pag")[1].GetAttribute("aria-current").Should().Be("true");

        await TeclaPanelAsync(cut, "+");
        cut.Find(".vz-pct").TextContent.Trim().Should().Be("110 %");

        await TeclaPanelAsync(cut, "Escape");
        cierres.Should().Equal(false);
    }

    [Fact]
    public async Task Una_tecla_del_panel_no_se_procesa_dos_veces_al_subir_al_dialogo()
    {
        var cut = Pintar([Adj("A.pdf", paginas: 3)]);

        await TeclaPanelAsync(cut, "]");

        // Si además subiera al diálogo, la hoja avanzaría dos veces.
        cut.FindAll(".vz-pag")[1].GetAttribute("aria-current").Should().Be("true");
        cut.FindAll(".vz-pag")[2].GetAttribute("aria-current").Should().Be("false");
    }

    [Fact]
    public void Al_cerrar_y_volver_a_abrir_el_panel_vuelve_a_estar_abierto_y_sin_lo_resuelto_antes()
    {
        var cut = Pintar([Adj("A.pdf"), Adj("B.pdf")]);
        Panel(cut).QuerySelector("button.asignar")!.Click();
        cut.FindAll(".vz-marca")[0].ClassList.Should().Contain("vz-marca-asignado");
        BotonRevision(cut).Click();
        cut.FindAll("aside#vz-revision").Should().BeEmpty();

        cut.Render(p => p.Add(x => x.Visible, false));
        cut.Render(p => p.Add(x => x.Visible, true));

        // Lo resuelto dentro del visor era de esa vez: quien lo abre manda la lista, que sigue con el adjunto pendiente.
        Posicion(cut).Should().Be("Documento 1 de 2");
        Panel(cut).GetAttribute("data-estado").Should().Be("Pendiente");
        BotonRevision(cut).TextContent.Trim().Should().Be("Ocultar la revisión");
    }
}
