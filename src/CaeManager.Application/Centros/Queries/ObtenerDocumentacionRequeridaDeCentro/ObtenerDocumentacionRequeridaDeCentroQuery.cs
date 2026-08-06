using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;

/// <summary>
/// Alimenta la pestaña "Requisitos del Centro" del Context Workspace
/// (PLAN-EJECUCION-UX.md § 0.4) — catálogo completo de TipoDocumento
/// (Trabajador + Empresa) del tenant, con la posición de ESTE Centro sobre
/// cada uno: si aplica hoy (<see cref="ResolucionTipoDocumentoCentro"/>), si
/// hay una fila explícita (y su Incluido/PeriodicidadEspecial/BloqueaAcceso/
/// adjunto) o si sigue el criterio global de <c>TipoDocumento.EsObligatorio</c>.
/// Sustituye a ObtenerRequisitosDocumentalesDeCentroQuery (retirada junto con
/// RequisitoDocumental).
/// </summary>
public record ObtenerDocumentacionRequeridaDeCentroQuery(Guid CentroId) : IRequest<IReadOnlyList<DocumentacionRequeridaCentroDto>?>;

public record DocumentacionRequeridaCentroDto(
    Guid TipoDocumentoId,
    string Nombre,
    AmbitoAplicacion AmbitoAplicacion,
    bool EsObligatorioGlobal,
    bool Aplica,
    bool? Incluido,
    int? PeriodicidadEspecialMeses,
    bool BloqueaAcceso,
    string? ArchivoUrl,
    string? NombreArchivoOriginal);

public class ObtenerDocumentacionRequeridaDeCentroQueryHandler(
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDocumentacionRequeridaDeCentroQuery, IReadOnlyList<DocumentacionRequeridaCentroDto>?>
{
    public async Task<IReadOnlyList<DocumentacionRequeridaCentroDto>?> Handle(
        ObtenerDocumentacionRequeridaDeCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return null;

        if (!await centrosContext.Centros.AnyAsync(c => c.Id == request.CentroId, cancellationToken))
            return null;

        var tipos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador || t.AmbitoAplicacion == AmbitoAplicacion.Empresa)
            .OrderBy(t => t.AmbitoAplicacion).ThenBy(t => t.Orden)
            .Select(t => new { t.Id, t.Nombre, t.AmbitoAplicacion, t.EsObligatorio })
            .ToListAsync(cancellationToken);

        var filas = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tc.CentroId == request.CentroId)
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => tc.TipoDocumentoId);

        return tipos
            .Select(t =>
            {
                filas.TryGetValue(t.Id, out var fila);
                return new DocumentacionRequeridaCentroDto(
                    t.Id, t.Nombre, t.AmbitoAplicacion, t.EsObligatorio,
                    Aplica: fila is not null ? fila.Incluido : t.EsObligatorio,
                    Incluido: fila?.Incluido,
                    PeriodicidadEspecialMeses: fila?.PeriodicidadEspecialMeses,
                    BloqueaAcceso: fila?.BloqueaAcceso ?? false,
                    ArchivoUrl: fila?.ArchivoUrl,
                    NombreArchivoOriginal: fila?.NombreArchivoOriginal);
            })
            .ToList();
    }
}
