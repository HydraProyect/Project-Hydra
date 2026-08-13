using CaeManager.Application.Common;
using System.Net;
using System.Text;
using CaeManager.Application.Centros;
using CaeManager.Application.Documentos;
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
/// por Centro (salvo el adjunto opcional de <c>TipoDocumentoCentro</c>, no
/// enlazado aquí — ver DATABASE.md), así que "formato" aquí es este resumen
/// generado, no un archivo preexistente. Null cuando el Centro no es visible
/// para el usuario actual o no tiene ningún requisito configurado que
/// compartir.
/// </summary>
public record ObtenerFormatosRequeridosCentroQuery(Guid CentroId) : IRequest<string?>;

public class ObtenerFormatosRequeridosCentroQueryHandler(
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerFormatosRequeridosCentroQuery, string?>
{
    private record TipoAplicableDto(string Nombre, string? Descripcion, int? PeriodicidadEspecialMeses, bool BloqueaAcceso);

    public async Task<string?> Handle(ObtenerFormatosRequeridosCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return null;

        var centro = await centrosContext.Centros
            .Where(c => c.Id == request.CentroId)
            .Select(c => new { c.Nombre })
            .FirstOrDefaultAsync(cancellationToken);

        if (centro is null) return null;

        var tiposAplicables = await ObtenerTiposAplicablesAsync(request.CentroId, cancellationToken);

        if (tiposAplicables.Count == 0)
            return null;

        var cuerpo = new StringBuilder();
        cuerpo.Append($"<p>Documentación requerida para <strong>{WebUtility.HtmlEncode(centro.Nombre)}</strong>:</p>");
        cuerpo.Append("<ul>");
        foreach (var tipo in tiposAplicables)
        {
            var linea = WebUtility.HtmlEncode(tipo.Nombre);
            if (!string.IsNullOrWhiteSpace(tipo.Descripcion))
                linea += $" — {WebUtility.HtmlEncode(tipo.Descripcion)}";
            if (tipo.PeriodicidadEspecialMeses is { } meses)
                linea += $" (renovación cada {meses} mes(es) en este centro)";
            if (tipo.BloqueaAcceso)
                linea += " — imprescindible para el acceso";
            cuerpo.Append($"<li>{linea}</li>");
        }
        cuerpo.Append("</ul>");

        return cuerpo.ToString();
    }

    /// <summary>Ver ResolucionTipoDocumentoCentro — universo completo (Empresa+Trabajador), fila explícita manda, sin fila sigue EsObligatorio.</summary>
    private async Task<List<TipoAplicableDto>> ObtenerTiposAplicablesAsync(Guid centroId, CancellationToken cancellationToken)
    {
        var tipos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Empresa || t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .OrderBy(t => t.Orden)
            .Select(t => new { t.Id, t.Nombre, t.Descripcion, t.EsObligatorio })
            .ToListAsync(cancellationToken);

        var tipoIds = tipos.Select(t => t.Id).ToHashSet();
        var filasDelCentro = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIds.Contains(tc.TipoDocumentoId) && tc.CentroId == centroId)
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        return tipos
            .Where(t => ResolucionTipoDocumentoCentro.Aplica(filasDelCentro, t.Id, centroId, t.EsObligatorio))
            .Select(t =>
            {
                filasDelCentro.TryGetValue((t.Id, centroId), out var fila);
                return new TipoAplicableDto(t.Nombre, t.Descripcion, fila?.PeriodicidadEspecialMeses, fila?.BloqueaAcceso ?? false);
            })
            .ToList();
    }
}
