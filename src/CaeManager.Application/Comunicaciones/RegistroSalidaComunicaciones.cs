using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Comunicaciones;

/// <summary>Desde qué camino del producto salió un correo. Solo etiqueta la fila de auditoría: no autoriza nada.</summary>
public enum OrigenSalidaComunicacion
{
    Redaccion,
    Respuesta,
    RespuestaWhatsApp,
    Informe,
    PaqueteAcreditacion,
    Reclamacion,
    PrioridadValidacion,
    MigracionACorreo,
}

/// <summary>
/// Rastro de salida de un mensaje por un buzón o una línea conectados (C-11, decisión D-5 opción a): una fila
/// por envío, no por adjunto ni por destinatario, sobre <see cref="IRegistroExportacionService"/>,
/// que ya separa Actor real y Usuario simulado. Así un informe enviado por correo no elude el
/// rastro que deja su descarga.
/// <para>
/// Se llama <b>antes</b> de pedir el token de Graph y de enviar: el correo a un tercero no se
/// deshace, así que si la fila no se puede guardar la excepción sube y no sale nada (fallo
/// cerrado). Antes del token por una razón concreta: al refrescarlo la credencial queda
/// modificada en el contexto, y el registro se niega a guardar con cambios pendientes ajenos.
/// La fila atestigua la orden de salida autorizada, no la entrega: un fallo posterior de Graph
/// deja la fila sin correo, nunca correo sin fila. Lo mismo la guarda de envío C-04 del correo y un reintento:
/// cada intento autorizado es una fila.
/// </para>
/// <para>
/// El registro falla cerrado también ante cambios pendientes en el contexto del circuito. Por eso
/// quien tolera un guardado fallido tras enviar los descarta (<see cref="IDescarteCambiosPendientes"/>):
/// si no, el siguiente envío de esa pantalla no saldría.
/// </para>
/// <para>
/// La fila nunca lleva el asunto, el cuerpo, el nombre de los adjuntos ni una dirección completa.
/// El dominio se conserva porque responde a qué organización salió el dato.
/// </para>
/// </summary>
public static class RegistroSalidaComunicaciones
{
    private const string Mascara = "***";

    public static Task RegistrarSalidaAsync(
        this IRegistroExportacionService registro, OrigenSalidaComunicacion origen, Guid conexionId,
        IReadOnlyList<string> destinatarios, IReadOnlyList<string>? copia, IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos,
        CancellationToken cancellationToken = default)
    {
        var criterios = new Dictionary<string, string>
        {
            ["origen"] = origen.ToString(),
            ["conexionId"] = conexionId.ToString(),
            ["destinatarios"] = string.Join(", ", destinatarios.Select(Enmascarar)),
            ["adjuntosBytes"] = (adjuntos?.Sum(a => a.Contenido.LongLength) ?? 0).ToString(),
        };
        if (copia is { Count: > 0 })
            criterios["copia"] = string.Join(", ", copia.Select(Enmascarar));

        return registro.RegistrarAsync(nameof(Mensaje), adjuntos?.Count ?? 0, criterios, cancellationToken);
    }

    /// <summary><c>contacto@cliente.com</c> → <c>c***@cliente.com</c>. Sin arroba válida, como un teléfono, no se conserva nada.</summary>
    public static string Enmascarar(string correo)
    {
        var limpio = correo.Trim();
        var arroba = limpio.LastIndexOf('@');
        if (arroba <= 0 || arroba == limpio.Length - 1)
            return Mascara;

        return $"{limpio[0]}{Mascara}{limpio[arroba..]}";
    }
}
