using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;

namespace CaeManager.Application.Seguridad.Incidentes.Aviso;

/// <summary>
/// Plantilla del aviso de violación de seguridad al Tenant beneficiario afectado: el borrador que la página del
/// incidente propone (lo que TALVEG sabe por la señal, art. 33.3 RGPD) y el correo que sale, primer aviso o
/// notificación completa. El Actor de Plataforma TALVEG puede editar cada apartado antes de confirmar.
///
/// <para>
/// Sin datos personales del sujeto del incidente: la evidencia solo trae identificadores y recuentos, y aquí
/// solo se usan los recuentos. El único dato personal del correo es el nombre del destinatario en el saludo.
/// </para>
/// </summary>
public static class PlantillaAvisoIncidente
{
    private static readonly CultureInfo Espanol = CultureInfo.GetCultureInfo("es-ES");

    private const string EnEvaluacion = "En evaluación; se concretarán en la notificación completa.";

    /// <summary>Referencia legible del incidente para el asunto y la página (p. ej. <c>INC-2026-3F9A2C</c>).</summary>
    public static string Referencia(Guid incidenteId, DateTime detectadoEnUtc) =>
        $"INC-{detectadoEnUtc.EnHoraPeninsular():yyyy}-{incidenteId.ToString("N")[^6..].ToUpperInvariant()}";

    /// <summary>Punto de contacto por defecto: el responsable de seguridad de TALVEG y, si está configurado, su correo.</summary>
    public static string ContactoPorDefecto(string? correoContactoTalveg) =>
        string.IsNullOrWhiteSpace(correoContactoTalveg)
            ? "Responsable de seguridad de TALVEG."
            : $"Responsable de seguridad de TALVEG: {correoContactoTalveg.Trim()} (también respondiendo a este correo).";

    /// <summary>Borrador del primer aviso, con lo que la señal permite afirmar.</summary>
    public static ContenidoAvisoIncidente BorradorPrimerAviso(SenalIncidenteSeguridad senal, string? correoContactoTalveg) =>
        new(Naturaleza(senal), EnEvaluacion, EnEvaluacion, Medidas(senal), ContactoPorDefecto(correoContactoTalveg));

    /// <summary>
    /// Borrador de la notificación completa: arranca de lo que se sabe y deja vacíos los apartados que el
    /// primer aviso dejó en evaluación, para que la página los marque como pendientes.
    /// </summary>
    public static ContenidoAvisoIncidente BorradorNotificacionCompleta(SenalIncidenteSeguridad senal, string? correoContactoTalveg) =>
        new(Naturaleza(senal), "", "", Medidas(senal), ContactoPorDefecto(correoContactoTalveg));

    public static string Naturaleza(SenalIncidenteSeguridad senal) => senal switch
    {
        SenalIncidenteSeguridad.InicioSesionNoReconocido =>
            "Acceso no autorizado sospechado a una cuenta de usuario de la organización.",
        SenalIncidenteSeguridad.ExtraccionMasiva =>
            "Extracción masiva sospechada de datos de la organización desde una cuenta con acceso.",
        SenalIncidenteSeguridad.AccesoRepetidoAOtroTenant =>
            "Intentos repetidos, rechazados por TALVEG, de una cuenta de la organización de acceder a datos de otra organización; posible cuenta comprometida.",
        _ => "Violación de la seguridad de los datos personales en evaluación.",
    };

    /// <summary>Lo que TALVEG ya hizo de forma automática al detectar la señal, sin atribuirse lo que no hizo.</summary>
    public static string Medidas(SenalIncidenteSeguridad senal) => senal switch
    {
        SenalIncidenteSeguridad.InicioSesionNoReconocido =>
            "Sesiones de la cuenta cerradas y credenciales revocadas; la persona titular debe crear una contraseña nueva.",
        SenalIncidenteSeguridad.AccesoRepetidoAOtroTenant =>
            "Todos los intentos fueron rechazados; ningún dato de otra organización llegó a la cuenta.",
        _ => EnEvaluacion,
    };

    /// <summary>Qué disparó la señal, en una frase, para la página (no va al correo).</summary>
    public static string DescripcionSenal(SenalIncidenteSeguridad senal, string evidenciaJson)
    {
        var evidencia = LeerEvidencia(evidenciaJson);
        return senal switch
        {
            SenalIncidenteSeguridad.InicioSesionNoReconocido =>
                "La persona titular de una cuenta marcó un inicio de sesión como «No he sido yo»"
                + (Instante(evidencia, "confirmado_en_utc") is { } cuando ? $" a las {cuando.EnHoraPeninsular():HH:mm} del {cuando.EnHoraPeninsular():dd/MM}." : "."),
            SenalIncidenteSeguridad.ExtraccionMasiva =>
                Numero(evidencia, "filas_exportadas_en_ventana") is { } filas
                    ? $"Una cuenta exportó {filas.ToString("N0", Espanol)} filas en {Numero(evidencia, "minutos_ventana") ?? 0} minutos (umbral: {Numero(evidencia, "umbral")?.ToString("N0", Espanol)})."
                    : $"Una cuenta abrió o descargó {Numero(evidencia, "documentos_sensibles_en_ventana")?.ToString("N0", Espanol)} documentos sensibles en {Numero(evidencia, "minutos_ventana") ?? 0} minutos (umbral: {Numero(evidencia, "umbral")?.ToString("N0", Espanol)}).",
            SenalIncidenteSeguridad.AccesoRepetidoAOtroTenant =>
                $"Una cuenta pidió {Numero(evidencia, "rechazos_en_ventana")} veces abrir otra organización en {Numero(evidencia, "minutos_ventana") ?? 0} minutos y TALVEG lo rechazó.",
            _ => "Señal sin descripción.",
        };
    }

    public static string Asunto(PlantillaAvisoIncidenteSeguridad plantilla, string referencia, string organizacion) => plantilla switch
    {
        PlantillaAvisoIncidenteSeguridad.PrimerAviso =>
            Recortar($"Primer aviso de violación de seguridad de datos personales ({referencia}) — {organizacion}", 300),
        _ => Recortar($"Notificación completa de violación de seguridad de datos personales ({referencia}) — {organizacion}", 300),
    };

    /// <summary>Contenido interior del correo (el envoltorio de marca lo pone <c>IEmailService</c>).</summary>
    public static string Cuerpo(
        PlantillaAvisoIncidenteSeguridad plantilla, ContenidoAvisoIncidente contenido, string referencia,
        string organizacion, string nombreDestinatario, bool sinContactoValidado, DateTime confirmadoEnUtc)
    {
        static string H(string texto) => WebUtility.HtmlEncode(texto.Trim());
        var confirmado = confirmadoEnUtc.EnHoraPeninsular();
        var venceCompleta = (confirmadoEnUtc + PlazosAvisoIncidenteSeguridad.NotificacionCompleta).EnHoraPeninsular();

        var html = new StringBuilder();
        html.Append($"<p>Hola, {H(nombreDestinatario)}:</p>");
        html.Append(plantilla == PlantillaAvisoIncidenteSeguridad.PrimerAviso
            ? $"<p>TALVEG, como encargado del tratamiento, os comunica una violación de la seguridad de los datos personales que afecta a <strong>{H(organizacion)}</strong>, confirmada el {confirmado.ToString("dd/MM/yyyy 'a las' HH:mm", Espanol)} (hora peninsular). Este es el primer aviso, con lo que sabemos ahora (art. 33.4 RGPD).</p>"
            : $"<p>TALVEG, como encargado del tratamiento, os envía la notificación completa de la violación de la seguridad de los datos personales que afecta a <strong>{H(organizacion)}</strong>, confirmada el {confirmado.ToString("dd/MM/yyyy 'a las' HH:mm", Espanol)} (hora peninsular), con la información del art. 33.3 RGPD.</p>");
        if (sinContactoValidado)
            html.Append($"<p>Os escribimos como Administradores de {H(organizacion)} porque la organización no tiene un contacto de seguridad validado en TALVEG. Podéis designarlo desde la configuración de la organización.</p>");

        html.Append("<ul>");
        html.Append($"<li><strong>Naturaleza de la violación:</strong> {H(contenido.Naturaleza)}</li>");
        html.Append($"<li><strong>Categorías y número aproximado de interesados y de registros:</strong> {H(contenido.DatosEInteresados)}</li>");
        if (plantilla == PlantillaAvisoIncidenteSeguridad.NotificacionCompleta)
            html.Append($"<li><strong>Consecuencias probables:</strong> {H(contenido.Consecuencias)}</li>");
        html.Append($"<li><strong>Medidas adoptadas o propuestas:</strong> {H(contenido.Medidas)}</li>");
        html.Append($"<li><strong>Punto de contacto:</strong> {H(contenido.Contacto)}</li>");
        html.Append("</ul>");

        if (plantilla == PlantillaAvisoIncidenteSeguridad.PrimerAviso)
            html.Append($"<p>Os enviaremos la notificación completa antes del {venceCompleta.ToString("dd/MM/yyyy 'a las' HH:mm", Espanol)}.</p>");
        html.Append($"<p>Referencia del incidente: <strong>{H(referencia)}</strong>.</p>");
        return html.ToString();
    }

    private static string Recortar(string texto, int maximo) => texto.Length <= maximo ? texto : texto[..maximo];

    private static JsonElement? LeerEvidencia(string json)
    {
        try
        {
            using var documento = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return documento.RootElement.ValueKind == JsonValueKind.Object ? documento.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? Numero(JsonElement? evidencia, string clave) =>
        evidencia is { } e && e.TryGetProperty(clave, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n : null;

    private static DateTime? Instante(JsonElement? evidencia, string clave) =>
        evidencia is { } e && e.TryGetProperty(clave, out var v) && v.ValueKind == JsonValueKind.String
        && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
}
