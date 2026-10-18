namespace CaeManager.Application.Integraciones;

/// <summary>Clasificación determinista de respuestas automáticas; no descarta el mensaje.</summary>
public static class DeteccionCorreoAutomatico
{
    public static bool EsAutomatico(MensajeGraphDto mensaje)
    {
        var direccion = mensaje.Remitente.Trim();
        var arroba = direccion.LastIndexOf('@');
        var nombre = (arroba >= 0 ? direccion[..arroba] : direccion).Replace("-", "").Replace("_", "").Replace(".", "");
        if (nombre.Equals("noreply", StringComparison.OrdinalIgnoreCase)
            || nombre.Equals("donotreply", StringComparison.OrdinalIgnoreCase))
            return true;

        return (mensaje.Cabeceras ?? []).Any(c =>
            (c.Nombre.Equals("Auto-Submitted", StringComparison.OrdinalIgnoreCase)
                && !c.Valor.Split(';')[0].Trim().Equals("no", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(c.Valor))
            || (c.Nombre.Equals("X-Autoreply", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(c.Valor)
                && !c.Valor.Trim().Equals("no", StringComparison.OrdinalIgnoreCase))
            || (c.Nombre.Equals("Precedence", StringComparison.OrdinalIgnoreCase)
                && new[] { "bulk", "list", "junk", "auto_reply" }.Contains(c.Valor.Trim(), StringComparer.OrdinalIgnoreCase)));
    }
}
