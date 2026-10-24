using System.Globalization;
using Bunit;
using CaeManager.Domain.Documentos;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class Centro360Gen2Tests
{
    [Fact]
    public void El_listado_mantiene_la_pastilla_Bloqueado_en_peligro_aunque_sus_documentos_estan_vigentes()
    {
        var id = Guid.NewGuid();
        var trabajador = Asignado("Ana Alba", EstadoDocumento.Vigente, Documento("Formación PRL", EstadoDocumento.Vigente));
        var mediador = Registrar(new MediatorFalso());
        mediador.Asignaciones[id] = [trabajador];
        mediador.Bloqueos[id] = [new(id, "Centro Norte", trabajador.TrabajadorId, trabajador.TrabajadorNombre,
            Guid.NewGuid(), "Seguro RC", EmpresaId: Guid.NewGuid(), Ambito: AmbitoAplicacion.Empresa)];
        var cut = Render<CaeManager.Web.Features.Centros.Components.AcordeonAsignacionesCentro>(p => p
            .Add(x => x.CentroId, id).Add(x => x.CentroNombre, "Centro Norte").Add(x => x.ModoListado, true));
        var pastilla = cut.Find(".fila-relacion-estado .badge");
        pastilla.TextContent.Trim().Should().Be("Bloqueado");
        pastilla.ClassList.Should().Contain("badge-peligro");
    }

    [Fact]
    public void Sin_exigidos_la_fila_muestra_guion_y_no_cero_de_cero()
    {
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso()).Asignaciones[id] = [Asignado("Ana Alba", EstadoDocumento.Vigente)];
        var cut = RenderizarAcordeon(id);
        cut.Find(".fila-trabajador-fraccion").TextContent.Should().Be("—");
        cut.Find(".fila-trabajador-fraccion").TextContent.Should().NotContain("0/0");
    }

    [Theory]
    [InlineData("es-ES", EstadoDocumento.Vencido, "1 vencido", "1 vencidos")]
    [InlineData("es-ES", EstadoDocumento.Faltante, "1 pendiente", "1 pendientes")]
    [InlineData("ca-ES", EstadoDocumento.Vencido, "1 vençut", "1 vençuts")]
    [InlineData("ca-ES", EstadoDocumento.Faltante, "1 pendent", "1 pendents")]
    public void El_pie_usa_el_singular_documental_en_es_y_ca(string cultura, EstadoDocumento estado, string singular, string pluralIncorrecto)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
            var id = Guid.NewGuid();
            Registrar(new MediatorFalso()).Asignaciones[id] =
                [Asignado("Ana Alba", estado, Documento("Aptitud médica", estado, new(2026, 10, 10)))];
            var cut = RenderizarAcordeon(id);
            cut.Find("[data-totales-documentales]").TextContent.Should().Contain(singular).And.NotContain(pluralIncorrecto);
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }
}
