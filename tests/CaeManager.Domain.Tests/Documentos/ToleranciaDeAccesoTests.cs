using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// Invariantes de la tolerancia de acceso: la personalización de un Centro (<see cref="TipoDocumentoCentro.ToleranciaDias"/>,
/// <c>null</c> = hereda) y la del Cliente empresarial (<see cref="ToleranciaDocumentoClienteEmpresarial"/>). Entre 0 y
/// la cota técnica; nunca negativa.
/// </summary>
public class ToleranciaDeAccesoTests
{
    private static readonly Guid TipoId = Guid.NewGuid();
    private static readonly Guid CentroId = Guid.NewGuid();

    [Fact]
    public void Una_fila_nueva_del_centro_hereda_la_tolerancia_por_defecto()
    {
        new TipoDocumentoCentro(TipoId, CentroId).ToleranciaDias.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(TipoDocumentoCentro.ToleranciaMaximaDias)]
    public void El_centro_acepta_una_tolerancia_entre_0_y_la_cota(int dias)
    {
        new TipoDocumentoCentro(TipoId, CentroId, toleranciaDias: dias).ToleranciaDias.Should().Be(dias);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(TipoDocumentoCentro.ToleranciaMaximaDias + 1)]
    public void El_centro_rechaza_una_tolerancia_negativa_o_por_encima_de_la_cota(int dias)
    {
        var alta = () => new TipoDocumentoCentro(TipoId, CentroId, toleranciaDias: dias);
        var fila = new TipoDocumentoCentro(TipoId, CentroId);
        var actualizar = () => fila.Actualizar(true, null, true, null, null, dias);

        alta.Should().Throw<ArgumentException>();
        actualizar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Actualizar_puede_volver_a_heredar_y_no_toca_el_resto_de_la_fila()
    {
        var fila = new TipoDocumentoCentro(TipoId, CentroId, periodicidadEspecialMeses: 12, bloqueaAcceso: true, toleranciaDias: 10);

        fila.Actualizar(true, 12, true, null, null, toleranciaDias: null);

        fila.ToleranciaDias.Should().BeNull();
        fila.PeriodicidadEspecialMeses.Should().Be(12);
        fila.BloqueaAcceso.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(TipoDocumentoCentro.ToleranciaMaximaDias)]
    public void El_cliente_empresarial_acepta_una_tolerancia_entre_0_y_la_cota(int dias)
    {
        new ToleranciaDocumentoClienteEmpresarial(Guid.NewGuid(), TipoId, dias).ToleranciaDias.Should().Be(dias);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(TipoDocumentoCentro.ToleranciaMaximaDias + 1)]
    public void El_cliente_empresarial_rechaza_una_tolerancia_fuera_de_rango(int dias)
    {
        var alta = () => new ToleranciaDocumentoClienteEmpresarial(Guid.NewGuid(), TipoId, dias);
        var tolerancia = new ToleranciaDocumentoClienteEmpresarial(Guid.NewGuid(), TipoId, 5);
        var establecer = () => tolerancia.Establecer(dias);

        alta.Should().Throw<ArgumentException>();
        establecer.Should().Throw<ArgumentException>();
        tolerancia.ToleranciaDias.Should().Be(5, "el valor invalido no debe dejar la entidad a medias");
    }

    [Fact]
    public void La_tolerancia_del_cliente_empresarial_exige_cliente_y_tipo()
    {
        var sinCliente = () => new ToleranciaDocumentoClienteEmpresarial(Guid.Empty, TipoId, 5);
        var sinTipo = () => new ToleranciaDocumentoClienteEmpresarial(Guid.NewGuid(), Guid.Empty, 5);

        sinCliente.Should().Throw<ArgumentException>();
        sinTipo.Should().Throw<ArgumentException>();
    }
}
