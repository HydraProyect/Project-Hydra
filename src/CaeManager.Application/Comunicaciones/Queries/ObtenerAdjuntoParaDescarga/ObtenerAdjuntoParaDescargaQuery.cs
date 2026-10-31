using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerAdjuntoParaDescarga;

/// <summary>
/// Resuelve un adjunto para servirlo por el endpoint de descarga —
/// comprueba el alcance sobre el Cliente de la conversación dueña Y, si el
/// hilo cuelga de un buzón personal de un gestor, que sea su propio dueño
/// (<see cref="AlcanceDatosServiceExtensions.ConversacionVisibleAsync"/>),
/// antes de devolver el <c>ArchivoUrl</c> — mismo criterio que
/// <c>ObtenerDocumentoPorIdQuery</c> para <c>/documentos/{id}/archivo</c>:
/// nunca servir un archivo por clave sin verificar que la fila es visible
/// para quien lo pide (Issue #18; auditoría módulo 6: sin el eje de
/// ConexionIntegracionId, cualquier gestor que adivinara el Guid podía
/// descargar un adjunto del buzón personal de otro gestor).
///
/// <para>
/// Un hilo sin ancla (la cola de triage) no tiene cartera que lo acote, y
/// <see cref="AlcanceDatosServiceExtensions.ConversacionVisibleAsync"/> lo da
/// por visible. Lo que lo acota es el rol (<see cref="AccesoATriage"/>). El
/// endpoint solo exige sesión, así que sin esta comprobación cualquier usuario
/// del Tenant propietario que tuviera el Guid se llevaba el adjunto (SEG-9,
/// ASVS 4.0.3 V4.1.3 y V4.2.1).
/// </para>
///
/// <para>
/// Cada entrega queda en la auditoría antes de devolver la referencia, y si no
/// se puede registrar no se entrega (ASVS 4.0.3 V7.1.3 y V7.2.2). Va por
/// <see cref="IRegistroAccesoDatoSensibleService"/> y no por el registro de
/// Documentos sensibles porque un adjunto no tiene <c>TipoDocumentoId</c> del
/// que sacar una categoría: la fila dice qué adjunto se pidió y quién lo pidió.
/// La fila se escribe antes de abrir el archivo, así que un archivo ausente deja
/// fila sin entrega, y cada petición Range de un visor deja la suya.
/// </para>
///
/// <para>
/// Sin rol efectivo no se entrega nada, tampoco en un hilo anclado. Es el caso de
/// Soporte TALVEG en una Sesión Privilegiada: <c>cae_app_soporte</c> no puede
/// escribir en <c>RegistrosAuditoria</c>, y lo que no se puede registrar no se
/// entrega. Mejor un 404 decidido aquí que un 500 por un 42501.
/// </para>
/// </summary>
public record ObtenerAdjuntoParaDescargaQuery(Guid Id) : IRequest<AdjuntoParaDescargaDto?>;

public record AdjuntoParaDescargaDto(string NombreArchivo, string TipoContenido, string ArchivoUrl);

public class ObtenerAdjuntoParaDescargaQueryHandler(
    IComunicacionesQueryContext comunicacionesContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    IRegistroAccesoDatoSensibleService registroAcceso)
    : IRequestHandler<ObtenerAdjuntoParaDescargaQuery, AdjuntoParaDescargaDto?>
{
    public async Task<AdjuntoParaDescargaDto?> Handle(ObtenerAdjuntoParaDescargaQuery request, CancellationToken cancellationToken)
    {
        var fila = await (
            from adjunto in comunicacionesContext.AdjuntosMensaje
            join mensaje in comunicacionesContext.Mensajes on adjunto.MensajeId equals mensaje.Id
            join conversacion in comunicacionesContext.Conversaciones on mensaje.ConversacionId equals conversacion.Id
            where adjunto.Id == request.Id
            select new { adjunto.Id, adjunto.NombreArchivo, adjunto.TipoContenido, adjunto.ArchivoUrl, conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId })
            .FirstOrDefaultAsync(cancellationToken);

        if (fila is null) return null;
        if (!await alcanceDatos.ConversacionVisibleAsync(fila.ClienteId, fila.EmpresaId, fila.ConexionIntegracionId, cancellationToken)) return null;

        var rol = await currentUserService.ObtenerRolEfectivoAsync();
        if (rol is null) return null;
        if (AccesoATriage.EsTriage(fila.ClienteId, fila.EmpresaId) && !AccesoATriage.RolPermitido(rol)) return null;

        await registroAcceso.RegistrarAsync(nameof(AdjuntoMensaje), fila.Id, cancellationToken);

        return new AdjuntoParaDescargaDto(fila.NombreArchivo, fila.TipoContenido, fila.ArchivoUrl);
    }
}
