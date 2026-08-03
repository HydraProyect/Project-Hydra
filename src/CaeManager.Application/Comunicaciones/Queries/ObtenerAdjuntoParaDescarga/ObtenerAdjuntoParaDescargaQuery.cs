using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerAdjuntoParaDescarga;

/// <summary>
/// Resuelve un adjunto para servirlo por el endpoint de descarga —
/// comprueba el alcance sobre el Cliente de la conversación dueña antes de
/// devolver el <c>ArchivoUrl</c>, mismo criterio que
/// <c>ObtenerDocumentoPorIdQuery</c> para <c>/documentos/{id}/archivo</c>:
/// nunca servir un archivo por clave sin verificar que la fila es visible
/// para quien lo pide (Issue #18).
/// </summary>
public record ObtenerAdjuntoParaDescargaQuery(Guid Id) : IRequest<AdjuntoParaDescargaDto?>;

public record AdjuntoParaDescargaDto(string NombreArchivo, string TipoContenido, string ArchivoUrl);

public class ObtenerAdjuntoParaDescargaQueryHandler(IComunicacionesQueryContext comunicacionesContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerAdjuntoParaDescargaQuery, AdjuntoParaDescargaDto?>
{
    public async Task<AdjuntoParaDescargaDto?> Handle(ObtenerAdjuntoParaDescargaQuery request, CancellationToken cancellationToken)
    {
        var fila = await (
            from adjunto in comunicacionesContext.AdjuntosMensajeCorreo
            join mensaje in comunicacionesContext.MensajesCorreo on adjunto.MensajeCorreoId equals mensaje.Id
            join conversacion in comunicacionesContext.ConversacionesCorreo on mensaje.ConversacionCorreoId equals conversacion.Id
            where adjunto.Id == request.Id
            select new { adjunto.NombreArchivo, adjunto.TipoContenido, adjunto.ArchivoUrl, conversacion.ClienteId })
            .FirstOrDefaultAsync(cancellationToken);

        if (fila is null) return null;
        if (!await alcanceDatos.ClienteOpcionalVisibleAsync(fila.ClienteId, cancellationToken)) return null;

        return new AdjuntoParaDescargaDto(fila.NombreArchivo, fila.TipoContenido, fila.ArchivoUrl);
    }
}
