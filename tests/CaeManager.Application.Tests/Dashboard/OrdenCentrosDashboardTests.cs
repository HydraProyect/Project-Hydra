using CaeManager.Application.Centros;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using FluentAssertions;

namespace CaeManager.Application.Tests.Dashboard;

public class OrdenCentrosDashboardTests
{
    [Fact]
    public void Los_Centros_nuevos_del_Cuadro_desempatan_por_los_dos_identificadores()
    {
        var uno = new Guid(1, 0, 0, new byte[8]);
        var dos = new Guid(2, 0, 0, new byte[8]);
        CentrosNuevosPorClienteDto[] filas = [new(dos, uno, "Igual", 1), new(uno, dos, "Igual", 1), new(uno, uno, "Igual", 1)];

        ObtenerCuadroDireccionQueryHandler.SumarCentrosNuevosPorCliente(filas)
            .Should().Equal(filas.Reverse());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(8)]
    public async Task Lectura_calculo_y_camino_mixto_ordenan_los_empates_antes_de_recortar(int leidos)
    {
        var centros = Enumerable.Range(0, 8)
            .Select(i => new Centro(Guid.NewGuid(), Guid.NewGuid(), i < 6 ? "Igual" : "Antes"))
            .OrderBy(c => c.Nombre, StringComparer.Ordinal).ThenBy(c => c.Id).ToArray();
        var esperado = centros.Take(5).Select(c => c.Id).ToArray();
        var calculo = new Calculo();
        var lector = new Lector(centros.Take(leidos).Select(c => c.Id).ToHashSet());
        var handler = new ObtenerCatalogoKpisQueryHandler(
            new Contexto(centros.Reverse().ToArray()), null!, calculo, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, lector);

        var resultado = await handler.CalcularCumplimientoAsync(null, CancellationToken.None);

        resultado.CentrosConMenorCumplimiento.Select(c => c.CentroId).Should().Equal(esperado);
        resultado.Porcentaje.Should().Be(50);
        resultado.TotalRequeridos.Should().Be(16);
        calculo.Ids.Should().BeEquivalentTo(centros.Skip(leidos).Select(c => c.Id));
    }

    private sealed class Contexto(Centro[] centros) : ICentrosQueryContext
    {
        public IQueryable<Centro> Centros => new TestAsyncQueryable<Centro>(centros.AsQueryable());
        public IQueryable<CanalGestionDocumental> CanalesGestionDocumental => throw new NotSupportedException();
    }

    private sealed class Lector(HashSet<Guid> ids) : ILectorCumplimientoDeDireccion
    {
        public Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CumplimientoDeCentrosAsync(
            IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, FraccionCumplimiento>>(
                centroIds.Where(ids.Contains).ToDictionary(id => id, _ => new FraccionCumplimiento(1, 2)));

        public Task<FraccionCumplimiento?> CumplimientoExigidoAsync(
            IReadOnlySet<(Guid EmpresaId, Guid CentroId)> centrosPorEmpresa, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Calculo : ICalculoEstadoCentroService
    {
        public IReadOnlyList<Guid> Ids { get; private set; } = [];

        public Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken)
        {
            Ids = centroIds;
            return Task.FromResult<IReadOnlyDictionary<Guid, FraccionCumplimiento>>(
                centroIds.ToDictionary(id => id, _ => new FraccionCumplimiento(1, 2)));
        }

        public Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ParDocumentalExigido>> ObtenerParesExigidosAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ParDocumentalDeEmpresaExigido>> ObtenerParesDeEmpresaExigidosAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
