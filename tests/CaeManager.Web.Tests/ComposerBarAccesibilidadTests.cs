using Bunit;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El composer de WhatsApp tenía un cuadro de texto sin etiqueta (solo
/// placeholder, que desaparece al escribir) y un botón de enviar que solo
/// pinta un icono: un lector de pantalla los anunciaba sin nombre. Los dos
/// llevan ahora <c>aria-label</c> desde <c>TextosComposer</c>.
///
/// <para>
/// Las claves no coinciden con su texto: si el localizador no encontrara el
/// recurso devolvería la clave («EtiquetaMensajeWhatsApp»), y el test lo vería.
/// No observa cómo lo anuncia cada lector de pantalla.
/// </para>
/// </summary>
public class ComposerBarAccesibilidadTests : BunitContext
{
    public ComposerBarAccesibilidadTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    [Fact]
    public void El_cuadro_de_texto_y_el_boton_de_enviar_de_WhatsApp_tienen_nombre_accesible()
    {
        var cut = Render<ComposerBar>(p => p
            .Add(c => c.Canal, CanalConversacion.WhatsApp)
            .Add(c => c.VentanaAbierta, true)
            .Add(c => c.Texto, "Hola"));

        cut.Find("textarea.composer-whatsapp-input").GetAttribute("aria-label").Should().Be("Mensaje de WhatsApp");
        cut.Find("button.composer-boton-enviar-circular").GetAttribute("aria-label").Should().Be("Enviar mensaje");
    }
}
