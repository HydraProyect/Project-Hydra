using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

public class TipoDocumentoTests
{
    [Theory]
    [InlineData(AmbitoAplicacion.Trabajador)]
    [InlineData(AmbitoAplicacion.Cliente)]
    [InlineData(AmbitoAplicacion.Empresa)]
    [InlineData(AmbitoAplicacion.Vehiculo)]
    public void Crea_un_tipo_de_documento_con_el_ambito_indicado(AmbitoAplicacion ambito)
    {
        var tipo = new TipoDocumento("RLC", null, aplicaVencimientoAutomatico: false, orden: 1, ambito);

        tipo.AmbitoAplicacion.Should().Be(ambito);
    }

    [Fact]
    public void Actualizar_no_permite_cambiar_el_ambito()
    {
        var tipo = new TipoDocumento("RLC", null, aplicaVencimientoAutomatico: false, orden: 1, AmbitoAplicacion.Cliente);

        tipo.Actualizar("RLC", null, aplicaVencimientoAutomatico: false, orden: 2, esObligatorio: false, notas: null,
            descripcion: null, criteriosValidacion: null, seSolicitaA: null, observaciones: null);

        tipo.AmbitoAplicacion.Should().Be(AmbitoAplicacion.Cliente);
    }

    [Fact]
    public void EsObligatorio_por_defecto_es_falso_y_se_puede_actualizar()
    {
        var tipo = new TipoDocumento("RLC", null, aplicaVencimientoAutomatico: false, orden: 1, AmbitoAplicacion.Empresa);

        tipo.EsObligatorio.Should().BeFalse();

        tipo.Actualizar("RLC", null, aplicaVencimientoAutomatico: false, orden: 1, esObligatorio: true, notas: null,
            descripcion: null, criteriosValidacion: null, seSolicitaA: null, observaciones: null);

        tipo.EsObligatorio.Should().BeTrue();
    }
}
