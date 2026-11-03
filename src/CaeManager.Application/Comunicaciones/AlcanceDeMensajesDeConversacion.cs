using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones;

/// <summary>
/// Alcance de lo que cuelga de un <c>Mensaje</c> —las sugerencias de Visita y los ítems de Gestión que la IA detecta
/// en él—: es el de la Conversación dueña del mensaje, y nada más. Es el mismo criterio con el que
/// <c>ObtenerConversacionPorIdQuery</c> entrega esas sugerencias, con su Id, a la Bandeja de Comunicaciones: quien no
/// puede abrir el hilo no ve el ítem, así que tampoco puede resolverlo conociendo su Guid.
/// </summary>
public static class AlcanceDeMensajesDeConversacion
{
    /// <summary>
    /// ¿Alcanza quien mira la Conversación del mensaje <paramref name="mensajeId"/>? Falla cerrado: un mensaje que no
    /// existe, o cuya Conversación no se encuentra, responde false. El criterio de la Conversación es
    /// <see cref="AlcanceDatosServiceExtensions.ConversacionVisibleAsync"/> (cartera del ancla y buzón personal).
    /// </summary>
    public static async Task<bool> MensajeVisibleAsync(
        this IAlcanceDatosService alcance, IComunicacionesQueryContext comunicaciones, Guid mensajeId,
        CancellationToken cancellationToken = default)
    {
        var conversacion = await (
            from mensaje in comunicaciones.Mensajes
            where mensaje.Id == mensajeId
            join c in comunicaciones.Conversaciones on mensaje.ConversacionId equals c.Id
            select c)
            .FirstOrDefaultAsync(cancellationToken);

        return conversacion is not null && await alcance.ConversacionVisibleAsync(conversacion, cancellationToken);
    }

    /// <summary><see cref="AlcanceDatosServiceExtensions.ConversacionVisibleAsync"/> sobre una conversación ya cargada.</summary>
    public static Task<bool> ConversacionVisibleAsync(
        this IAlcanceDatosService alcance, Conversacion conversacion, CancellationToken cancellationToken = default) =>
        alcance.ConversacionVisibleAsync(
            conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken);

    /// <summary>Sin Cliente empresarial ni Empresa, el hilo está en triage y ninguna cartera lo atiende todavía.</summary>
    public static bool TieneAncla(this Conversacion conversacion) =>
        conversacion.ClienteId is not null || conversacion.EmpresaId is not null;
}
