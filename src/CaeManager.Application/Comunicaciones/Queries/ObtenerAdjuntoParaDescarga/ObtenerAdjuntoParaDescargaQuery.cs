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
/// por visible. Lo que lo acota es el rol: solo los roles de gestión CAE de
/// <c>RolesComunicaciones.Gestion</c>, los mismos que entran en la pantalla.
/// El endpoint solo exige sesión, así que sin esta comprobación cualquier
/// usuario del Tenant propietario que tuviera el Guid se llevaba el adjunto
/// (SEG-9, ASVS 4.0.3 V4.1.3 y V4.2.1). Lista cerrada: un rol nuevo, o el rol
/// nulo de una Sesión Privilegiada, queda fuera hasta que alguien lo decida.
/// </para>
///
/// <para>
/// Cada entrega queda en la auditoría antes de devolver la referencia, y si no
/// se puede registrar no se entrega (ASVS 4.0.3 V7.1.3 y V7.2.2). Va por
/// <see cref="IRegistroAccesoDatoSensibleService"/> y no por el registro de
/// Documentos sensibles porque un adjunto no tiene <c>TipoDocumentoId</c> del
/// que sacar una categoría: la fila dice qué adjunto se leyó y quién lo leyó.
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
    /// <summary>
    /// Copia de <c>RolesComunicaciones.Gestion</c>, que vive en Web y aquí no se
    /// alcanza. <c>RolesComunicacionesEnApplicationTests</c> vigila que no diverjan.
    /// </summary>
    public static readonly IReadOnlySet<string> RolesConAccesoATriage = new HashSet<string>(StringComparer.Ordinal)
    {
        "Administrador", "DireccionCae", "CoordinadorCae", "GestorCae", "Consulta"
    };

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

        var esTriage = fila.ClienteId is null && fila.EmpresaId is null;
        if (esTriage && !EsRolConAccesoATriage(await currentUserService.ObtenerRolEfectivoAsync())) return null;

        await registroAcceso.RegistrarAsync(nameof(AdjuntoMensaje), fila.Id, cancellationToken);

        return new AdjuntoParaDescargaDto(fila.NombreArchivo, fila.TipoContenido, fila.ArchivoUrl);
    }

    private static bool EsRolConAccesoATriage(string? rol) => rol is not null && RolesConAccesoATriage.Contains(rol);
}
