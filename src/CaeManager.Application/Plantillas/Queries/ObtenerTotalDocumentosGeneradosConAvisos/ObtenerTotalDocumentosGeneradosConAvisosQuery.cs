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

        // Sin acceso total, la misma puerta que la lista (V4.2.1): el contador no puede contar lo que la tabla no enseña.
        // Trabajador, Empresa, Centro y plantilla se recortan en la base; solo se materializan las seis coordenadas de
        // lo que queda, que son los generados con avisos que la tabla del panel ya enseña enteros a este usuario. El
        // Documento del que cuelga cada uno vive en otro contexto de persistencia y se decide en memoria sobre ese resto.
        var acotada = await alcanceDatos.AcotarALaCarteraAsync(consulta, plantillasContext, cancellationToken);
        var generados = await acotada
            .Select(d => new CoordenadasDocumentoGenerado(
                d.Id, d.DocumentoId, d.PlantillaDocumentoVersionId, d.TrabajadorId, d.EmpresaId, d.CentroId))
            .ToListAsync(cancellationToken);

        var visibles = await alcanceDatos.DocumentosGeneradosVisiblesAsync(
            generados, plantillasContext, documentosContext, proyectosContext, cancellationToken);
        return visibles.Count;
    }
}
