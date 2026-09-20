using System.Net;
using CaeManager.Application.Common;

namespace CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;

/// <summary>
/// Contenido interior del correo de reclamación de documentación. Lo comparten
/// la reclamación por Trabajador y la de Empresa, que solo se distinguen en
/// que la de Empresa no lleva columna «Trabajador»: en un documento de empresa
/// el propietario es la propia destinataria, así que repetir su razón social en
/// cada fila no informaría de nada.
///
/// <para>
/// Es el <c>cuerpoHtml</c> tal cual sale por los dos canales (Conexión de
/// Microsoft 365 y SMTP), así que solo lleva contenido: el envoltorio de marca
/// y el bloque «Qué debes hacer» —que depende de si el correo lleva
/// <c>Reply-To</c>, y eso solo se sabe en el envío por SMTP— los añade quien
/// envía. Sin botón: no hay enlace de subida, y la acción es responder.
/// </para>
/// </summary>
public static class CuerpoDeReclamacion
{
    public static string Construir(
        string razonSocialDestinataria,
        IEnumerable<(string? Trabajador, string Documento, DateOnly Vencimiento)> documentos)
    {
        var filas = documentos.OrderBy(d => d.Vencimiento).ToList();
        var conTrabajador = filas.Any(f => f.Trabajador is not null);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        const string celdaCabecera =
            "padding:8px 8px 8px 0;border-bottom:2px solid #E0E3DE;text-align:left;font-family:Arial,Helvetica,sans-serif;font-size:11px;letter-spacing:.8px;text-transform:uppercase;color:#5A6862";
        const string celda =
            "padding:11px 8px 11px 0;border-bottom:1px solid #E0E3DE;font-family:Arial,Helvetica,sans-serif;font-size:14px;line-height:1.4;color:#2A322E";

        var tabla = new System.Text.StringBuilder();
        tabla.Append("""<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border-collapse:collapse;margin:4px 0 22px"><tr>""");
        if (conTrabajador) tabla.Append($"""<th class="suave" style="{celdaCabecera}">Trabajador</th>""");
        tabla.Append($"""<th class="suave" style="{celdaCabecera}">Documento</th>""");
        tabla.Append($"""<th class="suave" style="{celdaCabecera}">Vencimiento</th></tr>""");

        foreach (var (trabajador, documento, vencimiento) in filas)
        {
            // Vencido o próximo: palabra + símbolo + color, nunca solo color.
            var estado = CorreoHtml.Estado(vencimiento < hoy ? CorreoHtml.EstadoVisual.Vencido : CorreoHtml.EstadoVisual.Proximo);
            tabla.Append("<tr>");
            if (conTrabajador) tabla.Append($"""<td class="txt" style="{celda}">{CorreoHtml.Codificar(trabajador ?? string.Empty)}</td>""");
            tabla.Append($"""<td class="txt" style="{celda}">{CorreoHtml.Codificar(documento)}</td>""");
            tabla.Append($"""<td class="txt" style="{celda}">{estado}<br>{vencimiento:dd/MM/yyyy}</td></tr>""");
        }

        tabla.Append("</table>");

        return
            CorreoHtml.Titulo("Hay documentos que renovar") +
            CorreoHtml.Parrafo($"Estimado/a {CorreoHtml.Codificar(razonSocialDestinataria)}, los siguientes documentos de coordinación de actividades empresariales están próximos a vencer o ya han vencido. Por favor, gestiona su renovación lo antes posible:") +
            tabla +
            CorreoHtml.Parrafo("Gracias por tu colaboración.");
    }

    /// <summary>
    /// «Qué debes hacer», solo para el envío por SMTP. Con <c>Reply-To</c> la
    /// acción es responder (caja de acento: informa); sin él el correo no
    /// tiene a quién contestar y se dice sin prometer nada (caja ámbar:
    /// corrige). Sigue sin enlace de subida: eso es otra decisión.
    /// </summary>
    public static string QueDebesHacer(bool hayReplyTo) =>
        hayReplyTo
            ? CorreoHtml.CajaAcento("<b class=\"tit\" style=\"color:#122A21\">Qué debes hacer</b><br>Gestiona la renovación y <b>responde a este correo</b>: tu respuesta llega directamente a quien te lo reclama.")
            : CorreoHtml.CajaAviso("<b>Qué debes hacer</b><br>Gestiona la renovación de estos documentos y hazlos llegar a tu Gestor CAE.");
}
