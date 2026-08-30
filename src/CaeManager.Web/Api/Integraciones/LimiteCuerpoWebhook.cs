namespace CaeManager.Web.Api.Integraciones;

/// <summary>
/// Lectura acotada del cuerpo crudo de un webhook anónimo (WhatsApp,
/// Microsoft 365). <c>Content-Length</c> es una cabecera que el remitente
/// declara — no una garantía; un remitente hostil puede omitirla o mentir y
/// seguir mandando bytes indefinidamente, así que el límite real se aplica
/// mientras se lee el stream, no solo comprobando la cabecera antes de leer.
/// </summary>
internal static class LimiteCuerpoWebhook
{
    /// <summary>Con margen de sobra sobre cualquier notificación real de Meta o Graph, que solo llevan metadatos y, como mucho, un cuerpo de texto corto.</summary>
    public const int MaximoBytes = 1 * 1024 * 1024;

    /// <summary>Devuelve null si el cuerpo supera <see cref="MaximoBytes"/> — el llamador debe responder 413 sin seguir leyendo.</summary>
    public static async Task<byte[]?> LeerAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaximoBytes)
            return null;

        using var destino = new MemoryStream();
        var lote = new byte[8192];
        int leidos;
        while ((leidos = await request.Body.ReadAsync(lote, cancellationToken)) > 0)
        {
            if (destino.Length + leidos > MaximoBytes)
                return null;

            destino.Write(lote, 0, leidos);
        }

        return destino.ToArray();
    }
}
