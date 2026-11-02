using System.Net.Mail;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Texto del acuse automático por buzón. Es fijo y no lleva ningún dato del correo recibido: no hay nada que un
/// remitente pueda hacer rebotar a otra dirección.
/// </summary>
public static class PlantillaAcuseBuzon
{
    /// <summary>Borrador: Chris aprueba el texto definitivo (plan de Comunicaciones, 2026-11-02). Al aprobarlo, pasa a false.</summary>
    public const bool EsBorrador = true;

    public const string CuerpoHtml =
        "<p>Hemos recibido tu mensaje. Una persona del equipo lo revisará y te responderá lo antes posible.</p>" +
        "<p>Este es un aviso automático: no hace falta que contestes a este correo.</p>";
}

/// <summary>Acuse decidido durante la ingesta, que sale cuando el correo que lo origina ya está confirmado.</summary>
public sealed record AcusePendiente(
    ConexionIntegracion Conexion, Conversacion Conversacion, string MensajeExternoId, string Destinatario);

/// <summary>Qué correos admiten acuse, salvo la cadencia por remitente, que necesita mirar la base.</summary>
public static class ReglaAcuseBuzon
{
    /// <param name="conexion">Buzón por el que entró el correo.</param>
    /// <param name="mensaje">Primer correo de una conversación nueva.</param>
    /// <param name="dominioDePlataformaCae">
    /// El remitente usa un dominio de Plataforma CAE, autenticado o no. No basta con mirar si el mensaje se clasificó como
    /// automático: la ingesta trata como externo a quien suplanta una Plataforma CAE, aunque escriba desde un noreply.
    /// </param>
    public static bool Procede(ConexionIntegracion conexion, MensajeGraphDto mensaje, bool dominioDePlataformaCae) =>
        conexion.AcuseAutomaticoActivo
        && !dominioDePlataformaCae
        && !DeteccionCorreoAutomatico.NoAdmiteAcuse(mensaje)
        && EsDireccionUtil(mensaje.Remitente)
        && !MismaDireccion(mensaje.Remitente, conexion.BuzonEmail)
        && RespondeAlRemitente(mensaje);

    private static bool EsDireccionUtil(string remitente) =>
        MailAddress.TryCreate(remitente.Trim(), out var direccion) && direccion.Address == remitente.Trim();

    /// <summary>
    /// /reply envía al Reply-To cuando existe, así que las exclusiones de arriba y la fila de auditoría, que miran al
    /// remitente, hablarían de otra dirección. Si el correo pide respuesta en otro sitio, no se contesta.
    /// </summary>
    private static bool RespondeAlRemitente(MensajeGraphDto mensaje) =>
        (mensaje.ResponderA ?? []).All(d => MismaDireccion(d, mensaje.Remitente));

    private static bool MismaDireccion(string a, string b) =>
        a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);
}
