using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerEvidenciaVerificacionParaDescarga;

/// <summary>
/// Resuelve la evidencia de una verificación externa para servirla por el
/// endpoint de descarga — mismo criterio que
/// <c>ObtenerAdjuntoParaDescargaQuery</c> (Issue #18): nunca servir un
/// archivo por clave sin verificar que la fila es visible para quien lo pide.
/// Visible = la Subcontrata Y el Centro de la verificación, las dos cosas.
/// </summary>
public record ObtenerEvidenciaVerificacionParaDescargaQuery(Guid VerificacionId) : IRequest<EvidenciaParaDescargaDto?>;

/// <summary>
/// <paramref name="TipoDocumentoId"/> viaja aquí para que el endpoint pueda
/// registrar el acceso (DEC-36, REC-099): a diferencia de un adjunto de
/// correo, la evidencia de una VerificacionExternaSubcontrata SÍ tiene una
/// clasificación real del catálogo — no es una plantilla en blanco, es una
/// captura/justificante adjuntado (ver el comentario de la entidad). Codex lo
/// detectó antes de abrir la PR: la exclusión original de este endpoint
/// asumía "sin TipoDocumentoId no hay categoría", que era falso aquí.
/// </summary>
public record EvidenciaParaDescargaDto(string NombreArchivo, string ArchivoRuta, Guid TipoDocumentoId);

public class ObtenerEvidenciaVerificacionParaDescargaQueryHandler(
    ISubcontratasQueryContext subcontratasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerEvidenciaVerificacionParaDescargaQuery, EvidenciaParaDescargaDto?>
{
    public async Task<EvidenciaParaDescargaDto?> Handle(
        ObtenerEvidenciaVerificacionParaDescargaQuery request, CancellationToken cancellationToken)
    {
        var fila = await subcontratasContext.VerificacionesExternaSubcontrata
            .Where(v => v.Id == request.VerificacionId && v.EvidenciaArchivoRuta != null)
            .Select(v => new { v.SubcontrataId, v.CentroId, v.EvidenciaArchivoRuta, v.EvidenciaNombreArchivo, v.TipoDocumentoId })
            .FirstOrDefaultAsync(cancellationToken);

        if (fila is null) return null;
        if (!await alcanceDatos.SubcontrataVisibleAsync(fila.SubcontrataId, cancellationToken)) return null;

        // La verificación es de la Subcontrata EN un Centro: la Subcontrata
        // visible no basta. Mismo cruce que ObtenerSupervisionSubcontrataQuery,
        // que es quien enseña el identificador con el que se llega aquí.
        if (!await alcanceDatos.CentroVisibleAsync(fila.CentroId, cancellationToken)) return null;

        return new EvidenciaParaDescargaDto(fila.EvidenciaNombreArchivo ?? "evidencia", fila.EvidenciaArchivoRuta!, fila.TipoDocumentoId);
    }
}
