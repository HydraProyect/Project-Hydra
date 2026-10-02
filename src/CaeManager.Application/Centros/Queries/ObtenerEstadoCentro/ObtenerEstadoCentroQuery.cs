using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.Centros.Queries.ObtenerEstadoCentro;

/// <summary>Respalda el badge de cumplimiento y su desglose en el Context Workspace de Centro (ver CalculoEstadoCentroService).</summary>
public record ObtenerEstadoCentroQuery(Guid CentroId) : IRequest<EstadoCentroDto?>;

/// <param name="CumplimientoPorcentaje">
/// Mismo cálculo que <c>CentroListaDto.CumplimientoPorcentaje</c> (D-17): <c>null</c> cuando no hay ningún
/// Trabajador×TipoDocumento obligatorio aplicable, es decir, no se ha medido nada. La UI lo usa para rotular
/// «Sin datos» en vez de «Vigente» (<c>EstadoCentroUi.Texto(estado, cumplimiento)</c>).
/// </param>
public record EstadoCentroDto(EstadoCentro Estado, IReadOnlyList<CausaEstadoCentroDto> Causas, int? CumplimientoPorcentaje);

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

        var cumplimiento = await calculoEstadoCentro.CalcularCumplimientoAsync([request.CentroId], cancellationToken);

        return new EstadoCentroDto(
            resultado.Estado,
            resultado.Causas.Select(c => new CausaEstadoCentroDto(c.Descripcion, c.Estado, c.Bloqueante)).ToList(),
            cumplimiento.TryGetValue(request.CentroId, out var fraccion) ? fraccion.Porcentaje : null);
    }
}
