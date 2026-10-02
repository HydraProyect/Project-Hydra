using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerEstadoCentro;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Centros;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// D-17: el estado del panel de Centro trae el MISMO cumplimiento que la lista (<c>CalcularCumplimientoAsync</c>), y
/// <c>null</c> —nada medido— cuando no hay Trabajador×TipoDocumento obligatorio aplicable. Fuera del alcance del usuario
/// la query sigue devolviendo null sin calcular nada.
/// </summary>
public class ObtenerEstadoCentroQueryTests
{
    private static readonly Guid CentroId = Guid.NewGuid();

    private sealed class CalculoFalso(FraccionCumplimiento? fraccion) : ICalculoEstadoCentroService
    {
        public int Llamadas { get; private set; }

        public Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken)
        {
            Llamadas++;
            return Task.FromResult<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>>(
                new Dictionary<Guid, ResultadoEstadoCentro> { [CentroId] = new(EstadoCentro.Vigente, []) });
        }

        public Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken)
        {
            Llamadas++;
            IReadOnlyDictionary<Guid, FraccionCumplimiento> resultado = fraccion is null
                ? new Dictionary<Guid, FraccionCumplimiento>()
                : new Dictionary<Guid, FraccionCumplimiento> { [CentroId] = fraccion };
            return Task.FromResult(resultado);
        }
    }

    [Theory]
    [InlineData(0, 0, null)]    // denominador 0: nada medido
    [InlineData(3, 4, 75)]
    [InlineData(0, 2, 0)]       // medido y a cero: no es «sin datos»
    [InlineData(5, 5, 100)]
    public async Task El_estado_trae_el_cumplimiento_de_la_lista_y_null_si_no_hay_nada_medido(int alDia, int requeridos, int? esperado)
    {
        var handler = new ObtenerEstadoCentroQueryHandler(new CalculoFalso(new FraccionCumplimiento(alDia, requeridos)), new AlcanceDatosServiceFalso());

        var dto = await handler.Handle(new ObtenerEstadoCentroQuery(CentroId), CancellationToken.None);

        dto.Should().NotBeNull();
        dto!.CumplimientoPorcentaje.Should().Be(esperado);
    }

    [Fact]
    public async Task Sin_fraccion_calculada_para_el_centro_el_cumplimiento_es_null()
    {
        var handler = new ObtenerEstadoCentroQueryHandler(new CalculoFalso(null), new AlcanceDatosServiceFalso());

        var dto = await handler.Handle(new ObtenerEstadoCentroQuery(CentroId), CancellationToken.None);

        dto!.CumplimientoPorcentaje.Should().BeNull();
    }

    [Fact]
    public async Task Un_centro_fuera_de_alcance_no_devuelve_nada_ni_calcula()
    {
        var calculo = new CalculoFalso(new FraccionCumplimiento(1, 1));
        var handler = new ObtenerEstadoCentroQueryHandler(
            calculo, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [Guid.NewGuid()]));

        var dto = await handler.Handle(new ObtenerEstadoCentroQuery(CentroId), CancellationToken.None);

        dto.Should().BeNull();
        calculo.Llamadas.Should().Be(0);
    }
}
