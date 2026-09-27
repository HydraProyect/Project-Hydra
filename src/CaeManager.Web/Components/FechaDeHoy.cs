namespace CaeManager.Web.Components;

/// <summary>
/// La fecha que un formulario propone como «hoy» (emisión de un documento,
/// solicitud o respuesta del blindaje 42.1…).
///
/// Es el día UTC, el mismo con el que el dominio y los validadores juzgan que una
/// fecha «no puede ser futura» (<c>Documento.Renovar</c>,
/// <c>SolicitudCertificacionTgss</c>, <c>Crear/RenovarDocumentoCommandValidator</c>).
/// Con la hora local del servidor, en una máquina con hora de Madrid el formulario
/// proponía entre las 00:00 y las 02:00 (01:00 en invierno) un día que la regla
/// todavía consideraba futuro, y el guardado fallaba sin que el usuario tocara la
/// fecha. Proponer y juzgar tienen que usar el mismo reloj.
/// </summary>
public static class FechaDeHoy
{
    /// <summary>El día UTC de <paramref name="reloj"/>, en el formato de un <c>input type="date"</c>.</summary>
    public static string ParaCampoFecha(TimeProvider reloj) =>
        DateOnly.FromDateTime(reloj.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd");
}
