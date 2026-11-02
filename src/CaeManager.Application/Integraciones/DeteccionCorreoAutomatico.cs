namespace CaeManager.Application.Integraciones;

/// <summary>Clasificación determinista de respuestas automáticas; no descarta el mensaje.</summary>
public static class DeteccionCorreoAutomatico
{
    /// <summary>Asuntos con los que llegan los avisos que no traen ninguna cabecera de respuesta automática.</summary>
    private static readonly string[] AsuntosDeRespuestaAutomatica =
    [
        "Automatic reply:", "Respuesta automática:", "Out of office", "Fuera de la oficina", "Undeliverable:", "No se puede entregar:",
    ];

    public static bool EsAutomatico(MensajeGraphDto mensaje)
    {
        var direccion = mensaje.Remitente.Trim();
        var arroba = direccion.LastIndexOf('@');
        var nombre = (arroba >= 0 ? direccion[..arroba] : direccion).Replace("-", "").Replace("_", "").Replace(".", "");
        if (nombre.StartsWith("noreply", StringComparison.OrdinalIgnoreCase)
            || nombre.StartsWith("donotreply", StringComparison.OrdinalIgnoreCase)
            || nombre.Equals("mailerdaemon", StringComparison.OrdinalIgnoreCase)
            || nombre.Equals("postmaster", StringComparison.OrdinalIgnoreCase))
            return true;

        // El Return-Path vacío de RFC 5321 es el de los avisos de no entrega.
        return (mensaje.Cabeceras ?? []).Any(c =>
            (c.Nombre.Equals("Auto-Submitted", StringComparison.OrdinalIgnoreCase)
                && !c.Valor.Split(';')[0].Trim().Equals("no", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(c.Valor))
            || ((c.Nombre.Equals("X-Autoreply", StringComparison.OrdinalIgnoreCase)
                    || c.Nombre.Equals("X-Autorespond", StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(c.Valor)
                && !c.Valor.Trim().Equals("no", StringComparison.OrdinalIgnoreCase))
            || (c.Nombre.Equals("Precedence", StringComparison.OrdinalIgnoreCase)
                && new[] { "bulk", "list", "junk", "auto_reply" }.Contains(c.Valor.Trim(), StringComparer.OrdinalIgnoreCase))
            || ((c.Nombre.Equals("List-Id", StringComparison.OrdinalIgnoreCase)
                    || c.Nombre.Equals("List-Unsubscribe", StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(c.Valor))
            || (c.Nombre.Equals("Return-Path", StringComparison.OrdinalIgnoreCase) && c.Valor.Trim() == "<>"));
    }

    /// <summary>
    /// Más estricta que <see cref="EsAutomatico"/>, que decide si un correo reabre un hilo: equivocarse aquí solo deja
    /// a alguien sin acuse, y equivocarse al contrario manda correo a quien no lo admite. Por eso cuenta también lo
    /// que no prueba que el correo sea automático: el remitente que pide no recibir respuestas automáticas, el asunto
    /// típico de un aviso sin cabeceras y el correo que no supera DMARC, cuyo remitente puede ser una víctima ajena.
    /// </summary>
    public static bool NoAdmiteAcuse(MensajeGraphDto mensaje) =>
        EsAutomatico(mensaje)
        || AsuntosDeRespuestaAutomatica.Any(a => mensaje.Asunto.TrimStart().StartsWith(a, StringComparison.OrdinalIgnoreCase))
        || (mensaje.Cabeceras ?? []).Any(c =>
            (c.Nombre.Equals("X-Auto-Response-Suppress", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Valor))
            || (c.Nombre.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase)
                && c.Valor.Contains("dmarc=fail", StringComparison.OrdinalIgnoreCase)));
}
