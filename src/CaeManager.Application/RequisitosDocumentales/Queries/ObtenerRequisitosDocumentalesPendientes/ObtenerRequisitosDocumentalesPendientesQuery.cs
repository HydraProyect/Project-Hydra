using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.RequisitosDocumentales.Queries.ObtenerRequisitosDocumentalesPendientes;

/// <summary>
/// Fase C (Bandeja del gestor): a diferencia de
/// <see cref="CaeManager.Application.Alertas.Queries.ObtenerAlertas.ObtenerAlertasQuery"/>,
/// un <c>RequisitoDocumental</c> sin cumplir **no** aparece hoy en
/// <c>/alertas</c> — su propio comentario de dominio lo deja explícito (la
/// detección de "documento faltante" se apoya en
/// <c>TipoDocumento.EsObligatorio</c>, no en este agregado). Esta Query
/// replica, como consulta propia y reutilizable, el mismo filtro que ya usa
/// <c>CalculoEstadoCentroService.AgregarCausasDeRequisitosBloqueantesAsync</c>
/// para calcular <c>EstadoCentro.Bloqueado</c> — solo los que además
/// <c>BloqueaAcceso</c>, porque son los únicos con urgencia real para una
/// cola priorizada (un requisito informativo sin bloqueo no compite por
/// atención con un documento vencido).
/// </summary>
public record ObtenerRequisitosDocumentalesPendientesQuery : IRequest<IReadOnlyList<RequisitoDocumentalPendienteDto>>;

public record RequisitoDocumentalPendienteDto(Guid Id, Guid CentroId, string CentroNombre, string Descripcion);

public class ObtenerRequisitosDocumentalesPendientesQueryHandler(
    IRequisitosDocumentalesQueryContext requisitosContext, ICentrosQueryContext centrosContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerRequisitosDocumentalesPendientesQuery, IReadOnlyList<RequisitoDocumentalPendienteDto>>
{
    public async Task<IReadOnlyList<RequisitoDocumentalPendienteDto>> Handle(
        ObtenerRequisitosDocumentalesPendientesQuery request, CancellationToken cancellationToken)
    {
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);

        var consulta =
            from requisito in requisitosContext.RequisitosDocumentales
            where requisito.BloqueaAcceso && !requisito.Cumplido
            join centro in centrosContext.Centros on requisito.CentroId equals centro.Id
            select new { RequisitoId = requisito.Id, CentroId = centro.Id, centro.Nombre, requisito.Descripcion };

        if (centroIdsVisibles is not null)
            consulta = consulta.Where(x => centroIdsVisibles.Contains(x.CentroId));

        return await consulta
            .OrderBy(x => x.Nombre)
            .Select(x => new RequisitoDocumentalPendienteDto(x.RequisitoId, x.CentroId, x.Nombre, x.Descripcion))
            .ToListAsync(cancellationToken);
    }
}
