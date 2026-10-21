using CaeManager.Application.Documentos;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.Proyectos;

/// <summary>Cálculo documental de mentira para las lecturas del Proyecto: devuelve lo que el test le fija por propietario.</summary>
internal sealed class CalculoDocumentalFalso : ICalculoEstadoDocumentalService
{
    public Dictionary<Guid, (EstadoDocumento Estado, DesgloseDocumentalDto Desglose)> PorPropietario { get; } = [];

    public Task<IReadOnlyList<EstadoDocumento>> CalcularEstadosDeDocumentosDeVehiculoAsync(Guid vehiculoId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EstadoDocumento>>([]);

    public Task<IReadOnlyDictionary<Guid, EstadoDocumento>> CalcularPeorEstadoAsync(
        AmbitoAplicacion ambito, IReadOnlyList<Guid> propietarioIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, EstadoDocumento>>(
            PorPropietario.Where(p => propietarioIds.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value.Estado));

    public Task<IReadOnlyDictionary<Guid, DesgloseDocumentalDto>> CalcularDesgloseAsync(
        AmbitoAplicacion ambito, IReadOnlyList<Guid> propietarioIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, DesgloseDocumentalDto>>(
            PorPropietario.Where(p => propietarioIds.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value.Desglose));
}
