using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerEvidenciaVerificacionParaDescarga;

/// <summary>
/// Resuelve la evidencia de una verificación externa para servirla por el
/// endpoint de descarga — mismo criterio que
/// <c>ObtenerAdjuntoParaDescargaQuery</c> (Issue #18): nunca servir un
/// archivo por clave sin verificar que la fila es visible para quien lo pide.
/// </summary>
public record ObtenerEvidenciaVerificacionParaDescargaQuery(Guid VerificacionId) : IRequest<EvidenciaParaDescargaDto?>;

public record EvidenciaParaDescargaDto(string NombreArchivo, string ArchivoRuta);

public class ObtenerEvidenciaVerificacionParaDescargaQueryHandler(
    ISubcontratasQueryContext subcontratasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerEvidenciaVerificacionParaDescargaQuery, EvidenciaParaDescargaDto?>
{
    public async Task<EvidenciaParaDescargaDto?> Handle(
        ObtenerEvidenciaVerificacionParaDescargaQuery request, CancellationToken cancellationToken)
    {
        var fila = await subcontratasContext.VerificacionesExternaSubcontrata
            .Where(v => v.Id == request.VerificacionId && v.EvidenciaArchivoRuta != null)
            .Select(v => new { v.SubcontrataId, v.EvidenciaArchivoRuta, v.EvidenciaNombreArchivo })
            .FirstOrDefaultAsync(cancellationToken);

        if (fila is null) return null;
        if (!await alcanceDatos.SubcontrataVisibleAsync(fila.SubcontrataId, cancellationToken)) return null;

        return new EvidenciaParaDescargaDto(fila.EvidenciaNombreArchivo ?? "evidencia", fila.EvidenciaArchivoRuta!);
    }
}
