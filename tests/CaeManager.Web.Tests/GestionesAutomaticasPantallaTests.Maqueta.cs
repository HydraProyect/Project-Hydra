using Bunit;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using FluentAssertions;

namespace CaeManager.Web.Tests;

// Revisión 1:1 contra «Gestion Pasos TALVEG.dc.html» (2026-11-11): textos de la pastilla y del detalle del paso abierto.
public partial class GestionesAutomaticasPantallaTests
{
    [Theory]
    [InlineData(EstadoAcreditacion.PendienteDeSubir, "Pendiente de subir", "bp-subir")]
    [InlineData(EstadoAcreditacion.Subida, "Pendiente de validar", "bp-val")]
    [InlineData(EstadoAcreditacion.Aceptada, "Validado", "bp-ok")]
    [InlineData(EstadoAcreditacion.Rechazada, "Rechazado", "bp-rech")]
    public void La_pastilla_del_paso_abierto_usa_el_texto_corto_de_la_maqueta_igual_que_la_lista(EstadoAcreditacion estado, string texto, string clase)
    {
        // En la maqueta la pastilla del paso abierto sale de CORTO[estado], el mismo texto que la fila de la lista;
        // no de los nombres largos de la acreditación («Subido y al día», «Pendiente de validación»).
        var pasos = SeisPasosNuevos();
        pasos[0] = pasos[0] with { Estado = estado, Hecho = GestionAutomatica.PasoHecho(estado) };
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));
        cut.Find("[data-filtro-pasos='todos']").Click();
        cut.Find($"[data-paso='{pasos[0].AcreditacionId}']").Click();

        var pastilla = cut.Find("[data-pastilla-paso]");
        pastilla.TextContent.Should().Be(texto);
        pastilla.ClassList.Should().Contain(clase);
        cut.Find($"[data-paso='{pasos[0].AcreditacionId}'] .gp-paso-estado").TextContent.Should().Be(texto);
    }

    [Fact]
    public void El_detalle_de_un_paso_validado_va_en_minuscula_tras_la_pastilla_como_en_la_maqueta()
    {
        // Maqueta: pastilla «Validado» y, tras un punto medio, «validado hasta 14/10/2027» en minúscula;
        // en la lista de la izquierda, en cambio, «Validado hasta 14/10/2027» con mayúscula.
        var pasos = SeisPasosNuevos();
        pasos[0] = pasos[0] with { Estado = EstadoAcreditacion.Aceptada, Hecho = true, FechaVencimientoEnPlataforma = new DateOnly(2027, 10, 14) };
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));
        cut.Find("[data-filtro-pasos='todos']").Click();
        cut.Find($"[data-paso='{pasos[0].AcreditacionId}']").Click();

        cut.Find("[data-detalle-paso]").TextContent.Should().Be("· validado hasta 14/10/2027");
        cut.Find($"[data-paso='{pasos[0].AcreditacionId}'] .gp-paso-estado").TextContent.Should().Be("Validado hasta 14/10/2027");
    }

    [Fact]
    public void Un_paso_pendiente_de_subir_no_enseña_detalle_tras_la_pastilla()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));

        cut.FindAll("[data-detalle-paso]").Should().BeEmpty();
    }
}
