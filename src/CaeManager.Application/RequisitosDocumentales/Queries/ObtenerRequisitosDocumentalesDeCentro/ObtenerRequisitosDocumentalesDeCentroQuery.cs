using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.RequisitosDocumentales.Queries.ObtenerRequisitosDocumentalesDeCentro;

/// <summary>
/// Respalda la pestaña "Requisitos del Centro" del Context Workspace —
/// RequisitoDocumental ya existía en el modelo (ver DATABASE.md) pero no
/// tenía ninguna Query/Command en Application todavía.
/// </summary>
public record ObtenerRequisitosDocumentalesDeCentroQuery(Guid CentroId)
    : IRequest<IReadOnlyList<RequisitoDocumentalDto>>;

public record RequisitoDocumentalDto(
    Guid Id, string Descripcion, string? PeriodicidadEspecial, bool BloqueaAcceso, string? Notas, Guid Version);

public class ObtenerRequisitosDocumentalesDeCentroQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerRequisitosDocumentalesDeCentroQuery, IReadOnlyList<RequisitoDocumentalDto>>
{
    public async Task<IReadOnlyList<RequisitoDocumentalDto>> Handle(
        ObtenerRequisitosDocumentalesDeCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return [];

        return await dbContext.RequisitosDocumentales
            .Where(r => r.CentroId == request.CentroId)
            .OrderBy(r => r.Descripcion)
            .Select(r => new RequisitoDocumentalDto(r.Id, r.Descripcion, r.PeriodicidadEspecial, r.BloqueaAcceso, r.Notas, r.Version))
            .ToListAsync(cancellationToken);
    }
}
