using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// "Nunca apilar más de 3 visibles simultáneamente" (UX_PATTERNS.md,
/// "Toasts", P2 #28 de docs/business/MATURITY_REVIEW.md). Servicio de C#
/// puro, sin Blazor de por medio — no hace falta bUnit.
/// </summary>
public class ToastServiceTests
{
    [Fact]
    public void Un_cuarto_toast_desplaza_al_mas_antiguo()
    {
        var servicio = new ToastService();

        servicio.Mostrar("primero");
        servicio.Mostrar("segundo");
        servicio.Mostrar("tercero");
        servicio.Mostrar("cuarto");

        servicio.Mensajes.Should().HaveCount(ToastService.MaximoVisibles);
        servicio.Mensajes.Select(m => m.Mensaje).Should().Equal("segundo", "tercero", "cuarto");
    }

    [Fact]
    public void Un_toast_de_error_tambien_puede_ser_desplazado()
    {
        var servicio = new ToastService();

        servicio.Mostrar("error viejo", TonoToast.Error);
        servicio.Mostrar("segundo");
        servicio.Mostrar("tercero");
        servicio.Mostrar("cuarto");

        servicio.Mensajes.Select(m => m.Mensaje).Should().NotContain("error viejo");
    }

    [Fact]
    public void Hasta_tres_toasts_conviven_sin_desplazarse()
    {
        var servicio = new ToastService();

        servicio.Mostrar("primero");
        servicio.Mostrar("segundo");
        servicio.Mostrar("tercero");

        servicio.Mensajes.Select(m => m.Mensaje).Should().Equal("primero", "segundo", "tercero");
    }

    [Fact]
    public void Descartar_quita_el_toast_indicado()
    {
        var servicio = new ToastService();
        servicio.Mostrar("único", TonoToast.Error);
        var id = servicio.Mensajes.Single().Id;

        servicio.Descartar(id);

        servicio.Mensajes.Should().BeEmpty();
    }
}
