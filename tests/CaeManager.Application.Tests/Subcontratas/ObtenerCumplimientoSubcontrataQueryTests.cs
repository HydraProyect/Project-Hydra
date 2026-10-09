using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Queries.ObtenerCumplimientoSubcontrata;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Subcontratas;

/// <summary>
/// La consulta del anillo de la página Subcontrata 360: la fracción sale del cálculo compartido con el listado y
/// solo se entrega dentro del alcance de lectura de quien pregunta.
/// </summary>
public class ObtenerCumplimientoSubcontrataQueryTests
{
    [Fact]
    public async Task Devuelve_la_fraccion_que_calcula_el_servicio_compartido()
    {
        var subcontrataId = Guid.NewGuid();
        var calculo = new CalculoFalso { Fracciones = { [subcontrataId] = new FraccionCumplimiento(5, 6) } };
        var handler = new ObtenerCumplimientoSubcontrataQueryHandler(
            calculo, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: [subcontrataId]));

        var resultado = await handler.Handle(new ObtenerCumplimientoSubcontrataQuery(subcontrataId), CancellationToken.None);

        resultado.Should().Be(new FraccionCumplimiento(5, 6));
        resultado!.Porcentaje.Should().Be(83);
        calculo.IdsPedidos.Should().Equal(subcontrataId);
    }

    [Fact]
    public async Task Fuera_del_alcance_de_lectura_devuelve_null_sin_calcular_nada()
    {
        var calculo = new CalculoFalso();
        var handler = new ObtenerCumplimientoSubcontrataQueryHandler(
            calculo, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: []));

        var resultado = await handler.Handle(new ObtenerCumplimientoSubcontrataQuery(Guid.NewGuid()), CancellationToken.None);

        resultado.Should().BeNull();
        calculo.IdsPedidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_subcontrata_visible_sin_nada_exigido_no_tiene_porcentaje()
    {
        var subcontrataId = Guid.NewGuid();
        var handler = new ObtenerCumplimientoSubcontrataQueryHandler(
            new CalculoFalso(), new AlcanceDatosServiceFalso(tieneAccesoTotal: true));

        var resultado = await handler.Handle(new ObtenerCumplimientoSubcontrataQuery(subcontrataId), CancellationToken.None);

        resultado.Should().Be(FraccionCumplimiento.SinRequisitos);
        resultado!.Porcentaje.Should().BeNull();
    }

    private sealed class CalculoFalso : ICalculoEstadoSubcontrataService
    {
        public Dictionary<Guid, FraccionCumplimiento> Fracciones { get; } = [];
        public List<Guid> IdsPedidos { get; } = [];

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<IncidenciaSubcontrataDto>>> CalcularAsync(
            IReadOnlyList<Guid> subcontrataIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException("El anillo no pide incidencias.");

        public Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
            IReadOnlyList<Guid> subcontrataIds, CancellationToken cancellationToken)
        {
            IdsPedidos.AddRange(subcontrataIds);
            return Task.FromResult<IReadOnlyDictionary<Guid, FraccionCumplimiento>>(Fracciones);
        }
    }
}
