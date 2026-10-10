namespace CaeManager.Application.Reclamaciones;

/// <summary>
/// La única definición de «esta reclamación sigue sin respuesta» por envío: hay Conversación y ningún mensaje
/// entrante posterior al envío. La comparten la pestaña de reclamaciones enviadas y la segunda línea de cada
/// documento en las fichas 360, para que las dos digan lo mismo del mismo envío.
/// </summary>
public static class RespuestaDeReclamacion
{
    /// <returns>
    /// <c>null</c> si el envío no tiene Conversación (no se puede saber si contestaron); <c>true</c> si ningún
    /// entrante de esa Conversación es posterior al envío.
    /// </returns>
    public static bool? SinRespuesta(Guid? conversacionId, DateTime fechaEnvioUtc, IEnumerable<DateTime> entrantesDeLaConversacionUtc) =>
        conversacionId is null
            ? null
            : !entrantesDeLaConversacionUtc.Any(fecha => fecha > fechaEnvioUtc);
}
