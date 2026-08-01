using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cubre el cierre con Escape (hallazgo P0-8 de docs/business/MATURITY_REVIEW.md
/// — DESIGN_SYSTEM.md prometía "Escape para cerrar overlays" y Modal no lo
/// implementaba). El focus trap en sí vive en dialogo-foco.js y no es
/// testable aquí sin navegador real — ver el comentario de mejor esfuerzo en
/// Modal.razor.ActivarTrampaFocoAsync.
/// </summary>
public class ModalTests : BunitContext
{
    [Fact]
    public void Escape_cierra_el_modal_cuando_CerrarAlHacerClicFuera_es_true()
    {
        bool? valorNotificado = null;
        var cut = Render<Modal>(parametros => parametros
            .Add(p => p.Visible, true)
            .Add(p => p.VisibleChanged, v => valorNotificado = v)
            .Add(p => p.Titulo, "Prueba")
            .AddChildContent("<p>Contenido</p>"));

        cut.Find(".modal-contenido").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        valorNotificado.Should().BeFalse();
    }

    [Fact]
    public void Escape_no_cierra_el_modal_con_trabajo_sin_guardar()
    {
        bool? valorNotificado = null;
        var cut = Render<Modal>(parametros => parametros
            .Add(p => p.Visible, true)
            .Add(p => p.VisibleChanged, v => valorNotificado = v)
            .Add(p => p.Titulo, "Prueba")
            .Add(p => p.CerrarAlHacerClicFuera, false)
            .AddChildContent("<p>Contenido</p>"));

        cut.Find(".modal-contenido").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        valorNotificado.Should().BeNull();
    }

    [Fact]
    public void Otras_teclas_no_cierran_el_modal()
    {
        bool? valorNotificado = null;
        var cut = Render<Modal>(parametros => parametros
            .Add(p => p.Visible, true)
            .Add(p => p.VisibleChanged, v => valorNotificado = v)
            .Add(p => p.Titulo, "Prueba")
            .AddChildContent("<p>Contenido</p>"));

        cut.Find(".modal-contenido").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        valorNotificado.Should().BeNull();
    }
}
