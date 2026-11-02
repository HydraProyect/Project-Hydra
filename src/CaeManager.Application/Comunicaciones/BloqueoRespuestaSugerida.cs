using System.Net;
using System.Text;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones;

/// <summary>
/// Bloqueo en servidor de la respuesta sugerida: nunca se envía sin que una persona la revise. Lo llaman, antes de tocar
/// el canal, todos los comandos de Comunicaciones que envían texto de una persona. La interfaz bloquea además Enviar y
/// Ctrl+Intro, pero la garantía es esta. Escribir a mano sin insertar la sugerencia no bloquea.
/// </summary>
public static class BloqueoRespuestaSugerida
{
    public static readonly Error SugerenciaSinRevisar = Error.Crear(
        "Respuesta.SugerenciaSinRevisar", "Revisa la respuesta sugerida antes de enviar: confírmala, edítala o descártala.");

    /// <summary>
    /// Falla si la conversación tiene una sugerencia en <see cref="EstadoRespuestaSugerida.SinRevisar"/>, o si
    /// <paramref name="cuerpo"/> coincide con una en <see cref="EstadoRespuestaSugerida.Propuesta"/>: esa es la vía de
    /// enviarla sin pasar por Insertar.
    /// </summary>
    public static async Task<Result> ComprobarEnvioAsync(
        this IComunicacionesQueryContext comunicaciones, Guid conversacionId, string cuerpo, CancellationToken cancellationToken)
    {
        var activas = await comunicaciones.RespuestasSugeridas
            .Where(r => r.ConversacionId == conversacionId &&
                        (r.Estado == EstadoRespuestaSugerida.Propuesta || r.Estado == EstadoRespuestaSugerida.SinRevisar))
            .Select(r => new { r.Estado, r.Texto })
            .ToListAsync(cancellationToken);

        return activas.Any(r => r.Estado == EstadoRespuestaSugerida.SinRevisar) || Coincide(cuerpo, activas.Select(r => r.Texto))
            ? Result.Fallo(SugerenciaSinRevisar)
            : Result.Exito();
    }

    /// <summary>
    /// Para un mensaje nuevo: el proveedor decide después de enviarlo si cae en un hilo ya existente, y solo puede ser un
    /// hilo de este buzón. Antes de enviar no se sabe cuál, así que se compara con las sugerencias activas de todo el buzón.
    /// </summary>
    public static async Task<Result> ComprobarEnvioPorBuzonAsync(
        this IComunicacionesQueryContext comunicaciones, Guid conexionIntegracionId, string cuerpo, CancellationToken cancellationToken)
    {
        var textos = await (
            from respuesta in comunicaciones.RespuestasSugeridas
            where respuesta.Estado == EstadoRespuestaSugerida.Propuesta || respuesta.Estado == EstadoRespuestaSugerida.SinRevisar
            join conversacion in comunicaciones.Conversaciones on respuesta.ConversacionId equals conversacion.Id
            where conversacion.ConexionIntegracionId == conexionIntegracionId
            select respuesta.Texto)
            .ToListAsync(cancellationToken);

        return Coincide(cuerpo, textos) ? Result.Fallo(SugerenciaSinRevisar) : Result.Exito();
    }

    private static bool Coincide(string cuerpo, IEnumerable<string> textos)
    {
        var normalizado = Normalizar(cuerpo);
        return textos.Any(t => Normalizar(t) == normalizado);
    }

    /// <summary>Sin HTML, con las entidades decodificadas y los espacios colapsados.</summary>
    internal static string Normalizar(string texto)
    {
        var sinEtiquetas = new StringBuilder(texto.Length);
        var dentroDeEtiqueta = false;
        foreach (var caracter in texto)
        {
            if (caracter == '<')
            {
                dentroDeEtiqueta = true;
                sinEtiquetas.Append(' ');
            }
            else if (caracter == '>' && dentroDeEtiqueta)
            {
                dentroDeEtiqueta = false;
            }
            else if (!dentroDeEtiqueta)
            {
                sinEtiquetas.Append(caracter);
            }
        }

        var decodificado = WebUtility.HtmlDecode(sinEtiquetas.ToString());
        return string.Join(' ', decodificado.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
