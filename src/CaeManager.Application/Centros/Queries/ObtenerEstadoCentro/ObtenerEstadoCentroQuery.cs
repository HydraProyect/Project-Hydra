using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.Centros.Queries.ObtenerEstadoCentro;

/// <summary>Respalda el badge de cumplimiento y su desglose en el Context Workspace de Centro (ver CalculoEstadoCentroService).</summary>
public record ObtenerEstadoCentroQuery(Guid CentroId) : IRequest<EstadoCentroDto?>;

public record EstadoCentroDto(EstadoCentro Estado, IReadOnlyList<CausaEstadoCentroDto> Causas);

public record CausaEstadoCentroDto(string Descripcion, EstadoDocumento? Estado, bool Bloqueante);

public class ObtenerEstadoCentroQueryHandler(ICalculoEstadoCentroService calculoEstadoCentro, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerEstadoCentroQuery, EstadoCentroDto?>
{
    public async Task<EstadoCentroDto?> Handle(ObtenerEstadoCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return null;

        var resultados = await calculoEstadoCentro.CalcularAsync([request.CentroId], cancellationToken);
        if (!resultados.TryGetValue(request.CentroId, out var resultado))
            return null;

        return new EstadoCentroDto(
            resultado.Estado,
            resultado.Causas.Select(c => new CausaEstadoCentroDto(c.Descripcion, c.Estado, c.Bloqueante)).ToList());
    }
}
