using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Proyectos;
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
    IDocumentosQueryContext documentosContext,
    IProyectosQueryContext proyectosContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerTotalDocumentosGeneradosConAvisosQuery, int>
{
    public async Task<int> Handle(ObtenerTotalDocumentosGeneradosConAvisosQuery request, CancellationToken cancellationToken)
    {
        var consulta = plantillasContext.DocumentosGenerados
            .Where(d => d.Estado == EstadoDocumentoGenerado.GeneradoConAvisos);

        // Con acceso total no hay nada que recortar: se cuenta en la base, como antes.
        if (await alcanceDatos.TieneAccesoTotalAsync(cancellationToken))
            return await consulta.CountAsync(cancellationToken);

        // Sin acceso total, el contador no es una consulta propia (V4.2.1, API4:2023): cuenta la misma función que pinta
        // la lista, AlcanceDeDocumentosGenerados.GeneradosVisiblesAsync, sobre el universo de la lista sin filtros y con
        // la misma puerta, así que no puede contar lo que la tabla no enseña. El estado se filtra antes en la base: la
        // puerta decide cada fila por sus coordenadas, sin mirar las demás, y filtrar el estado antes o después da el
        // mismo número; antes solo materializa menos. Su coste queda acotado por el de la lista (sin paginación de
        // servidor, igual que en main).
        var generados = await alcanceDatos.GeneradosVisiblesAsync(
            consulta, plantillasContext, documentosContext, proyectosContext, cancellationToken);
        return generados.Count;
    }
}
