using System.Net;
using System.Text;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.RequisitosDocumentales;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerFormatosRequeridosCentro;

/// <summary>
/// Genera el texto (HTML, listo para pegar en la respuesta de un correo) con
/// la documentación que un Centro exige — cierra "que se pueda compartir los
/// formatos automáticamente cuando sea un formulario de centro necesario"
/// del pedido del usuario. Hydra no guarda plantillas de archivo en blanco
/// por Centro (solo descripciones de texto — TipoDocumento/RequisitoDocumental,
/// ver DATABASE.md), así que "formato" aquí es este resumen generado, no un
/// archivo preexistente. Null cuando el Centro no es visible para el usuario
/// actual o no tiene ningún requisito configurado que compartir.
/// </summary>
public record ObtenerFormatosRequeridosCentroQuery(Guid CentroId) : IRequest<string?>;

public class ObtenerFormatosRequeridosCentroQueryHandler(
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IRequisitosDocumentalesQueryContext requisitosContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerFormatosRequeridosCentroQuery, string?>
{
    public async Task<string?> Handle(ObtenerFormatosRequeridosCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return null;

        var centro = await centrosContext.Centros
            .Where(c => c.Id == request.CentroId)
            .Select(c => new { c.Nombre })
            .FirstOrDefaultAsync(cancellationToken);

        if (centro is null) return null;

        var tiposAplicables = await ObtenerTiposObligatoriosAplicablesAsync(request.CentroId, cancellationToken);

        var requisitos = await requisitosContext.RequisitosDocumentales
            .Where(r => r.CentroId == request.CentroId)
            .OrderBy(r => r.Descripcion)
            .Select(r => new { r.Descripcion, r.PeriodicidadEspecial, r.BloqueaAcceso })
            .ToListAsync(cancellationToken);

        if (tiposAplicables.Count == 0 && requisitos.Count == 0)
            return null;

        var cuerpo = new StringBuilder();
        cuerpo.Append($"<p>Documentación requerida para <strong>{WebUtility.HtmlEncode(centro.Nombre)}</strong>:</p>");

        if (tiposAplicables.Count > 0)
        {
            cuerpo.Append("<ul>");
            foreach (var tipo in tiposAplicables)
            {
                var linea = WebUtility.HtmlEncode(tipo.Nombre);
                if (!string.IsNullOrWhiteSpace(tipo.Descripcion))
                    linea += $" — {WebUtility.HtmlEncode(tipo.Descripcion)}";
                cuerpo.Append($"<li>{linea}</li>");
            }
            cuerpo.Append("</ul>");
        }

        if (requisitos.Count > 0)
        {
            cuerpo.Append("<p>Requisitos adicionales del centro:</p><ul>");
            foreach (var requisito in requisitos)
            {
                var linea = WebUtility.HtmlEncode(requisito.Descripcion);
                if (!string.IsNullOrWhiteSpace(requisito.PeriodicidadEspecial))
                    linea += $" ({WebUtility.HtmlEncode(requisito.PeriodicidadEspecial)})";
                if (requisito.BloqueaAcceso)
                    linea += " — imprescindible para el acceso";
                cuerpo.Append($"<li>{linea}</li>");
            }
            cuerpo.Append("</ul>");
        }

        return cuerpo.ToString();
    }

    /// <summary>Un TipoDocumento obligatorio (Ámbito Empresa o Trabajador) aplica a este Centro si lo restringe explícitamente (TipoDocumentoCentro) o si no restringe a ningún Centro (comportamiento global — ver TipoDocumentoCentro).</summary>
    private async Task<List<(string Nombre, string? Descripcion)>> ObtenerTiposObligatoriosAplicablesAsync(
        Guid centroId, CancellationToken cancellationToken)
    {
        var restricciones = await tiposDocumentoContext.TiposDocumentoCentros
            .Select(tc => new { tc.TipoDocumentoId, tc.CentroId })
            .ToListAsync(cancellationToken);

        var tiposConAlgunaRestriccion = restricciones.Select(r => r.TipoDocumentoId).ToHashSet();
        var tiposDeEsteCentro = restricciones.Where(r => r.CentroId == centroId).Select(r => r.TipoDocumentoId).ToHashSet();

        var tiposObligatorios = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.EsObligatorio && (t.AmbitoAplicacion == AmbitoAplicacion.Empresa || t.AmbitoAplicacion == AmbitoAplicacion.Trabajador))
            .OrderBy(t => t.Orden)
            .Select(t => new { t.Id, t.Nombre, t.Descripcion })
            .ToListAsync(cancellationToken);

        return tiposObligatorios
            .Where(t => tiposDeEsteCentro.Contains(t.Id) || !tiposConAlgunaRestriccion.Contains(t.Id))
            .Select(t => (t.Nombre, t.Descripcion))
            .ToList();
    }
}
