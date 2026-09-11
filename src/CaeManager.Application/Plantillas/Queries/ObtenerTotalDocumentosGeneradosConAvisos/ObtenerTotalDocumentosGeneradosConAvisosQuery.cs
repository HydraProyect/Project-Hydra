using CaeManager.Application.Common;
using CaeManager.Domain.Plantillas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plantillas.Queries.ObtenerTotalDocumentosGeneradosConAvisos;

/// <summary>
/// Cuántos <see cref="DocumentoGenerado"/> quedaron en
/// <see cref="EstadoDocumentoGenerado.GeneradoConAvisos"/> — el número que
/// pinta el badge de la pestaña "Generados" de Plantillas (Plantillas
/// TALVEG.dc.html: badge en tono aviso, con el tooltip "N documentos
/// generados con avisos, pendientes de revisar"). No es el total de
/// documentos generados: es un indicador de "pendiente de revisar", así que
/// a propósito no acepta <c>PlantillaDocumentoId</c>/<c>TrabajadorId</c> —
/// los filtros de <see cref="ObtenerDocumentosGenerados.ObtenerDocumentosGeneradosQuery"/>
/// acotan qué se ve en la tabla del panel, pero no deben poder hacer bajar
/// (ni desaparecer) este contador, que es del ámbito completo visible del
/// usuario.
/// </summary>
public record ObtenerTotalDocumentosGeneradosConAvisosQuery : IRequest<int>;

public class ObtenerTotalDocumentosGeneradosConAvisosQueryHandler(
    IPlantillasQueryContext plantillasContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerTotalDocumentosGeneradosConAvisosQuery, int>
{
    public async Task<int> Handle(ObtenerTotalDocumentosGeneradosConAvisosQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);

        var consulta = plantillasContext.DocumentosGenerados
            .Where(d => d.Estado == EstadoDocumentoGenerado.GeneradoConAvisos);

        if (trabajadorIdsVisibles is not null)
            consulta = consulta.Where(d => d.TrabajadorId == null || trabajadorIdsVisibles.Contains(d.TrabajadorId!.Value));
        if (empresaIdsVisibles is not null)
            consulta = consulta.Where(d => d.EmpresaId == null || empresaIdsVisibles.Contains(d.EmpresaId!.Value));

        return await consulta.CountAsync(cancellationToken);
    }
}
