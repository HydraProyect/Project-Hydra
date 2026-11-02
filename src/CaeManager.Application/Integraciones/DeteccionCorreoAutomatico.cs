namespace CaeManager.Application.Integraciones;

/// <summary>Clasificación determinista de respuestas automáticas; no descarta el mensaje.</summary>
public static class DeteccionCorreoAutomatico
{
    /// <summary>Asuntos de avisos que no traen ninguna cabecera de respuesta automática, de calendario y de confirmación de lectura.</summary>
    private static readonly string[] AsuntosSinAcuse =
    [
        "Automatic reply:", "Respuesta automática:", "Resposta automàtica:", "Resposta automática:",
        "Out of office", "Fuera de la oficina", "Undeliverable:", "No se puede entregar:",
        "Accepted:", "Aceptado:", "Aceptada:", "Declined:", "Rechazado:", "Rechazada:", "Tentative:", "Provisional:",
        "Canceled:", "Cancelado:", "Cancelada:", "Read:", "Leído:",
    ];

    /// <summary>Remitentes que no leen respuestas, más allá de los que <see cref="EsAutomatico"/> ya clasifica.</summary>
    private static readonly string[] RemitentesSinAcuse = ["noresponder", "noresponda", "nocontestar", "bounce", "notifications"];

    /// <summary>
    /// Cabeceras que identifican una lista de correo (RFC 2369, RFC 2919 y RFC 8058, más las de los gestores de listas).
    /// No prueban que el autor sea un sistema: una persona que escribe por una lista también las lleva. Por eso excluyen
    /// el acuse y no clasifican el mensaje como automático.
    /// </summary>
    private static readonly string[] CabecerasDeLista =
    [
        "List-Id", "List-Unsubscribe", "List-Unsubscribe-Post", "List-Subscribe", "List-Post", "List-Help", "List-Owner",
        "List-Archive", "Mailing-List", "X-Mailing-List", "X-Mailman-Version", "X-BeenThere",
    ];

    private static string NombreDeRemitente(MensajeGraphDto mensaje)
    {
        var direccion = mensaje.Remitente.Trim();
        var arroba = direccion.LastIndexOf('@');
        return (arroba >= 0 ? direccion[..arroba] : direccion).Replace("-", "").Replace("_", "").Replace(".", "");
    }

    public static bool EsAutomatico(MensajeGraphDto mensaje)
    {
        var nombre = NombreDeRemitente(mensaje);
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
            || (c.Nombre.Equals("Return-Path", StringComparison.OrdinalIgnoreCase) && c.Valor.Trim() == "<>"));
    }

    /// <summary>
    /// Más estricta que <see cref="EsAutomatico"/>, que decide si un correo reabre un hilo: equivocarse aquí solo deja
    /// a alguien sin acuse, y equivocarse al contrario manda correo a quien no lo admite. Por eso cuenta también lo
    /// que no prueba que el correo sea automático: el remitente que pide no recibir respuestas automáticas, el asunto
    /// típico de un aviso sin cabeceras, el correo de una lista y la respuesta a un correo anterior. Esta última porque
    /// un hilo desconocido no es siempre nuevo: la ingesta no ve los Elementos enviados, y quien contesta a algo escrito
    /// desde Outlook ya está hablando con una persona.
    /// </summary>
    public static bool NoAdmiteAcuse(MensajeGraphDto mensaje) =>
        EsAutomatico(mensaje)
        || RemitentesSinAcuse.Any(r => NombreDeRemitente(mensaje).StartsWith(r, StringComparison.OrdinalIgnoreCase))
        || AsuntosSinAcuse.Any(a => mensaje.Asunto.TrimStart().StartsWith(a, StringComparison.OrdinalIgnoreCase))
        || (mensaje.Cabeceras ?? []).Any(c =>
            (c.Nombre.Equals("X-Auto-Response-Suppress", StringComparison.OrdinalIgnoreCase)
                || c.Nombre.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase)
                || c.Nombre.Equals("References", StringComparison.OrdinalIgnoreCase)
                || CabecerasDeLista.Contains(c.Nombre, StringComparer.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(c.Valor));
}
