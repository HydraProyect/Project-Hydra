using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones;

/// <summary>
/// Origen del texto de lo que envía una persona en una conversación (issue #2007, D-5 del plan de cierre de issues
/// legales). Lo resuelve el servidor, no el cliente: el comando de envío no recibe ningún identificador de sugerencia, así
/// que una interfaz que se lo saltara no podría hacer pasar un texto de Vega por uno escrito a mano.
///
/// <para>
/// Regla: el envío parte de la sugerencia <see cref="EstadoRespuestaSugerida.Revisada"/> más reciente de la conversación
/// que contesta a su último entrante y que todavía no es el origen de ningún <see cref="Mensaje"/>. Sin ella, el origen es
/// <see cref="OrigenContenidoMensaje.Persona"/>. Se apoya en <see cref="BloqueoRespuestaSugerida"/>: una sugerencia
/// insertada en el redactor bloquea todo envío de la conversación hasta que una persona la confirma (o la edita lo
/// suficiente, y la interfaz la confirma) o la descarta, así que lo que sale tras una sugerencia no descartada sale después
/// de una revisada. La regla no compara textos a propósito: un texto de Vega muy editado ya no se parece a la propuesta y
/// sigue partiendo de ella. El error que admite es el conservador, marcar como de IA un texto que la persona rehízo entero
/// tras confirmar la propuesta, nunca dejar sin marca uno que salió de Vega.
/// </para>
///
/// <para>
/// Un mensaje nuevo (<c>EnviarMensajeNuevoCommand</c>) abre un hilo que todavía no tiene sugerencias: su origen es
/// siempre <see cref="OrigenContenidoMensaje.Persona"/>.
/// </para>
/// </summary>
public static class OrigenContenidoEnvio
{
    /// <summary>
    /// Cabecera del correo saliente cuyo texto redactó Vega y revisó una persona: legible por máquina y sin texto visible
    /// añadido, porque Vega ya firma (D-5). Graph solo admite cabeceras propias que empiecen por «x-».
    /// </summary>
    public const string NombreCabecera = "X-TALVEG-Origen-Contenido";

    public const string ValorCabeceraIaRevisada = "ia-revisada";

    /// <summary>
    /// La sugerencia revisada de la que parte el envío que una persona hace ahora en <paramref name="conversacionId"/>, o
    /// null si lo escribió a mano. Se llama después de <see cref="BloqueoRespuestaSugerida.ComprobarEnvioAsync"/> y antes
    /// de enviar, para que la marca vaya ya en el correo.
    /// </summary>
    public static async Task<RespuestaSugerida?> SugerenciaDelEnvioAsync(
        this IComunicacionesQueryContext comunicaciones, Guid conversacionId, CancellationToken cancellationToken)
    {
        var ultimoEntrante = await comunicaciones.Mensajes
            .Where(m => m.ConversacionId == conversacionId && m.Direccion == DireccionMensaje.Entrante && !m.EsAutomatico)
            .OrderByDescending(m => m.FechaUtc)
            .Select(m => (Guid?)m.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (ultimoEntrante is null)
            return null;

        return await comunicaciones.RespuestasSugeridas
            .Where(r => r.ConversacionId == conversacionId &&
                        r.Estado == EstadoRespuestaSugerida.Revisada &&
                        r.MensajeOrigenId == ultimoEntrante &&
                        !comunicaciones.Mensajes.Any(m => m.RespuestaSugeridaId == r.Id))
            .OrderByDescending(r => r.RevisadaEnUtc)
            .ThenByDescending(r => r.GeneradaEnUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Fija en <paramref name="mensaje"/> el origen resuelto por <see cref="SugerenciaDelEnvioAsync"/>.</summary>
    public static void RegistrarEn(Mensaje mensaje, RespuestaSugerida? sugerencia)
    {
        if (sugerencia is null)
            mensaje.RegistrarOrigenPersona();
        else
            mensaje.RegistrarOrigenDesde(sugerencia);
    }
}
