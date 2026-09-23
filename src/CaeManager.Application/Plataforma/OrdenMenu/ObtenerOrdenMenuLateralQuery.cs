using CaeManager.Domain.Plataforma;
using MediatR;

namespace CaeManager.Application.Plataforma.OrdenMenu;

/// <summary>
/// El orden global guardado del menú lateral, o <c>null</c> si nadie lo ha cambiado nunca (el
/// menú usa entonces el orden por defecto del catálogo). Lo leen todos los usuarios de todos los
/// Tenants: el orden no es autoridad, cada uno sigue viendo solo lo que su rol le permite.
/// </summary>
public record ObtenerOrdenMenuLateralQuery : IRequest<OrdenMenuLateralDto?>;

public class ObtenerOrdenMenuLateralQueryHandler(IOrdenMenuLateralRepository repositorio, CacheOrdenMenuLateral cache)
    : IRequestHandler<ObtenerOrdenMenuLateralQuery, OrdenMenuLateralDto?>
{
    public async Task<OrdenMenuLateralDto?> Handle(ObtenerOrdenMenuLateralQuery request, CancellationToken cancellationToken)
    {
        if (cache.IntentarObtener(out var enCache, out var generacion))
            return enCache;

        var orden = await repositorio.ObtenerSinSeguimientoAsync(cancellationToken);
        var dto = orden is null
            ? null
            : new OrdenMenuLateralDto(
                [.. orden.OrdenGrupos], [.. orden.OrdenEnlaces], orden.Version,
                orden.ActualizadoPorUsuarioId, orden.ActualizadoEnUtc);

        cache.Guardar(dto, generacion);
        return dto;
    }
}
