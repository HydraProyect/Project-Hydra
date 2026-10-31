using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// M-41 (Ola 2, accesibilidad): un campo con error debe exponerlo a la tecnología de apoyo —
/// aria-invalid y aria-describedby apuntando al id del mensaje de error real (WCAG 3.3.1, 4.1.2).
/// </summary>
public class CamposAccesibilidadErrorTests : BunitContext
{
    [Fact]
    public void CampoTexto_con_error_marca_aria_invalid_y_enlaza_el_mensaje()
    {
        var cut = Render<CampoTexto>(p => p.Add(c => c.Etiqueta, "Nombre").Add(c => c.MensajeError, "Obligatorio"));

        var input = cut.Find("input");
        var mensaje = cut.Find(".campo-mensaje-error");
        input.GetAttribute("aria-invalid").Should().Be("true");
        mensaje.Id.Should().NotBeNullOrEmpty();
        input.GetAttribute("aria-describedby").Should().Be(mensaje.Id);
    }

    [Fact]
    public void CampoTexto_sin_error_no_emite_atributos_de_error()
    {
        var cut = Render<CampoTexto>(p => p.Add(c => c.Etiqueta, "Nombre"));

        var input = cut.Find("input");
        input.HasAttribute("aria-invalid").Should().BeFalse();
        input.HasAttribute("aria-describedby").Should().BeFalse();
    }

    [Fact]
    public void CampoTexto_conserva_el_aria_describedby_del_llamador()
    {
        var cut = Render<CampoTexto>(p => p
            .Add(c => c.MensajeError, "Obligatorio")
            .AddUnmatched("aria-describedby", "ayuda-externa"));

        var mensaje = cut.Find(".campo-mensaje-error");
        cut.Find("input").GetAttribute("aria-describedby").Should().Be($"ayuda-externa {mensaje.Id}");
    }

    [Fact]
    public void CampoTextarea_con_error_marca_aria_invalid_y_enlaza_el_mensaje()
    {
        var cut = Render<CampoTextarea>(p => p.Add(c => c.Etiqueta, "Notas").Add(c => c.MensajeError, "Demasiado largo"));

        var area = cut.Find("textarea");
        var mensaje = cut.Find(".campo-mensaje-error");
        area.GetAttribute("aria-invalid").Should().Be("true");
        mensaje.Id.Should().NotBeNullOrEmpty();
        area.GetAttribute("aria-describedby").Should().Be(mensaje.Id);
    }

    [Fact]
    public void CampoTextarea_sin_error_no_emite_atributos_de_error()
    {
        var cut = Render<CampoTextarea>(p => p.Add(c => c.Etiqueta, "Notas"));

        var area = cut.Find("textarea");
        area.HasAttribute("aria-invalid").Should().BeFalse();
        area.HasAttribute("aria-describedby").Should().BeFalse();
    }
}
