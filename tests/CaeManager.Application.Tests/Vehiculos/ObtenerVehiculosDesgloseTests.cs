using CaeManager.Application.Documentos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Vehiculos;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;

namespace CaeManager.Application.Tests.Vehiculos;

public class ObtenerVehiculosDesgloseTests
{
    private sealed class VehiculosFalso(params Vehiculo[] vehiculos) : IVehiculosQueryContext
    {
        public IQueryable<Vehiculo> Vehiculos => new TestAsyncQueryable<Vehiculo>(vehiculos.AsQueryable());
    }

    private sealed class CalculoFalso : ICalculoEstadoDocumentalService
    {
        public List<Guid> Pedidos { get; } = [];
        public Task<IReadOnlyDictionary<Guid, DesgloseDocumentalDto>> CalcularDesgloseAsync(
            AmbitoAplicacion ambito, IReadOnlyList<Guid> propietarioIds, CancellationToken cancellationToken)
        {
            ambito.Should().Be(AmbitoAplicacion.Vehiculo);
            Pedidos.AddRange(propietarioIds);
            return Task.FromResult<IReadOnlyDictionary<Guid, DesgloseDocumentalDto>>(
                propietarioIds.ToDictionary(id => id, _ => new DesgloseDocumentalDto([], 7, 5)));
        }

        public Task<IReadOnlyDictionary<Guid, EstadoDocumento>> CalcularPeorEstadoAsync(
            AmbitoAplicacion ambito, IReadOnlyList<Guid> propietarioIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, EstadoDocumento>>(new Dictionary<Guid, EstadoDocumento>());

        public Task<IReadOnlyList<EstadoDocumento>> CalcularEstadosDeDocumentosDeVehiculoAsync(
            Guid vehiculoId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_desglose_reutiliza_las_cifras_de_la_calculadora_solo_para_la_pagina_visible(bool pedirDesglose)
    {
        var empresa = new Empresa("Montajes");
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(empresa);
        var visible = Vehiculo.DeEmpresa(empresa.Id, "A", "Daily", "1234ABC");
        var fueraDePagina = Vehiculo.DeEmpresa(empresa.Id, "B", "Daily", "5678ABC");
        var fueraDeCartera = Vehiculo.DeEmpresa(empresa.Id, "C", "Daily", "9012ABC");
        var calculo = new CalculoFalso();
        var handler = new ObtenerVehiculosQueryHandler(empresas, new VehiculosFalso(visible, fueraDePagina, fueraDeCartera),
            new AlcanceDatosServiceFalso(vehiculoIdsVisibles: [visible.Id, fueraDePagina.Id]),
            new DocumentosQueryContextFalso(), new ConfiguracionQueryContextFalso(), calculo);

        var resultado = await handler.Handle(new(null, TamanoPagina: 1, ConDesgloseDocumental: pedirDesglose), CancellationToken.None);

        resultado.TotalElementos.Should().Be(2);
        var fila = resultado.Elementos.Should().ContainSingle().Subject;
        fila.Id.Should().Be(visible.Id);
        fila.DocumentosRegistrados.Should().Be(pedirDesglose ? 7 : 0);
        fila.DocumentosVigentes.Should().Be(pedirDesglose ? 5 : 0);
        calculo.Pedidos.Should().Equal(pedirDesglose ? [visible.Id] : Array.Empty<Guid>());
    }
}
