using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerCumplimientoSubcontrata;

/// <summary>
/// Fracción de cumplimiento de UNA subcontrata, para el anillo de su página 360: la misma cifra que el listado
/// (<c>SubcontrataListaDto.CumplimientoPorcentaje</c>) porque sale del mismo
/// <see cref="ICalculoEstadoSubcontrataService.CalcularCumplimientoAsync"/>, que aquí se pide con un solo
/// identificador. Devuelve la fracción y no solo el porcentaje porque la página enseña también «N de M».
/// Hoy solo cuenta la documentación de sus Trabajadores: la subcontrata no tiene documentos de empresa propios.
/// </summary>
/// <remarks>
/// <c>null</c> cuando la subcontrata queda fuera del alcance de lectura de quien pregunta — igual que «no existe»,
/// para no revelar que el registro está en otra organización. Una subcontrata visible sin nada exigido devuelve
/// <see cref="FraccionCumplimiento.SinRequisitos"/>, cuyo <c>Porcentaje</c> es <c>null</c>.
/// </remarks>
public record ObtenerCumplimientoSubcontrataQuery(Guid SubcontrataId) : IRequest<FraccionCumplimiento?>;

public class ObtenerCumplimientoSubcontrataQueryHandler(
    ICalculoEstadoSubcontrataService calculoEstado, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCumplimientoSubcontrataQuery, FraccionCumplimiento?>
{
    public async Task<FraccionCumplimiento?> Handle(ObtenerCumplimientoSubcontrataQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.SubcontrataVisibleAsync(request.SubcontrataId, cancellationToken))
            return null;

        var fracciones = await calculoEstado.CalcularCumplimientoAsync([request.SubcontrataId], cancellationToken);

        return fracciones.TryGetValue(request.SubcontrataId, out var fraccion) ? fraccion : FraccionCumplimiento.SinRequisitos;
    }
}
