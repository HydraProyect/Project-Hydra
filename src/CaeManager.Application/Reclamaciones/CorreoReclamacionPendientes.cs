using System.Text;

namespace CaeManager.Application.Reclamaciones;

/// <summary>
/// La parte del correo de reclamación que pide lo que no vence: documentos que FALTAN o cuya vigencia está sin confirmar.
/// Es el mismo correo que el de vencimiento (misma apertura, misma tabla, mismo cierre) diciendo que el documento falta en
/// lugar de que vence; los dos constructores de cuerpo (Cliente empresarial y Empresa) la comparten para que no puedan
/// divergir. Sin pendientes no añade nada: el correo de siempre sale byte a byte igual.
/// </summary>
internal static class CorreoReclamacionPendientes
{
    public const string SituacionFalta = "Falta";

    public const string SituacionSinConfirmar = "Falta confirmar su vigencia";

    /// <summary>
    /// Añade al cuerpo el párrafo y la tabla de lo pendiente. <paramref name="conTrabajador"/> es la columna «Trabajador»: se
    /// oculta en un documento de Empresa, donde el propietario es la propia destinataria.
    /// </summary>
    public static void Anexar(StringBuilder builder, IReadOnlyList<DocumentoPendienteDto> pendientes, bool conTrabajador)
    {
        if (pendientes.Count == 0)
            return;

        var haySinConfirmar = pendientes.Any(p => p.Motivo == MotivoPendienteDeReclamacion.SinConfirmar);
        builder.Append(haySinConfirmar
            ? "<p>Los siguientes documentos de coordinación de actividades empresariales faltan o no tienen confirmada su vigencia. Por favor, facilítalos lo antes posible:</p>"
            : "<p>Los siguientes documentos de coordinación de actividades empresariales faltan. Por favor, facilítalos lo antes posible:</p>");

        builder.Append("<table style=\"border-collapse:collapse;width:100%\"><thead><tr>");
        if (conTrabajador)
            builder.Append("<th style=\"text-align:left;border-bottom:1px solid #ccc;padding:4px\">Trabajador</th>");
        builder.Append("<th style=\"text-align:left;border-bottom:1px solid #ccc;padding:4px\">Documento</th>")
            .Append("<th style=\"text-align:left;border-bottom:1px solid #ccc;padding:4px\">Situación</th>")
            .Append("</tr></thead><tbody>");

        foreach (var p in pendientes
                     .OrderBy(p => p.TrabajadorNombre, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(p => p.TipoDocumentoNombre, StringComparer.CurrentCultureIgnoreCase))
        {
            builder.Append("<tr>");
            if (conTrabajador)
                builder.Append("<td style=\"padding:4px\">").Append(System.Net.WebUtility.HtmlEncode(p.TrabajadorNombre ?? string.Empty)).Append("</td>");
            builder.Append("<td style=\"padding:4px\">").Append(System.Net.WebUtility.HtmlEncode(p.TipoDocumentoNombre)).Append("</td>")
                .Append("<td style=\"padding:4px\">")
                .Append(p.Motivo == MotivoPendienteDeReclamacion.SinConfirmar ? SituacionSinConfirmar : SituacionFalta)
                .Append("</td></tr>");
        }

        builder.Append("</tbody></table>");
    }
}
