using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones;

/// <summary>Lo que hace falta saber de un adjunto antes de repartirlo: su hilo, para el alcance, y su dirección.</summary>
internal sealed record AdjuntoParaReparto(
    Guid ConversacionId, Guid? ClienteId, Guid? EmpresaId, Guid? ConexionIntegracionId, DireccionMensaje Direccion)
{
    public Task<bool> VisibleAsync(IAlcanceDatosService alcanceDatos, CancellationToken cancellationToken) =>
        alcanceDatos.ConversacionVisibleAsync(ClienteId, EmpresaId, ConexionIntegracionId, cancellationToken);
}

internal static class RepartoAdjuntos
{
    public static Task<AdjuntoParaReparto?> ObtenerAsync(
        IComunicacionesQueryContext contexto, Guid adjuntoId, CancellationToken cancellationToken) =>
        (from a in contexto.AdjuntosMensaje
         join m in contexto.Mensajes on a.MensajeId equals m.Id
         join c in contexto.Conversaciones on m.ConversacionId equals c.Id
         where a.Id == adjuntoId
         select new AdjuntoParaReparto(c.Id, c.ClienteId, c.EmpresaId, c.ConexionIntegracionId, m.Direccion))
        .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Si el hilo existe y entra en el alcance de la sesión. Con acceso total el alcance dice sí: la existencia se mira aquí.</summary>
    public static async Task<bool> ConversacionVisibleAsync(
        IComunicacionesQueryContext contexto, IAlcanceDatosService alcanceDatos, Guid conversacionId, CancellationToken cancellationToken)
    {
        var conversacion = await contexto.Conversaciones
            .Where(c => c.Id == conversacionId)
            .Select(c => new { c.ClienteId, c.EmpresaId, c.ConexionIntegracionId })
            .FirstOrDefaultAsync(cancellationToken);

        return conversacion is not null && await alcanceDatos.ConversacionVisibleAsync(
            conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken);
    }
}
