using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Auditoria.Queries;

/// <summary>
/// «Consulta mínima para demostrar que el permiso funciona» (HO-099-01 § 8):
/// listado paginado del rastro de acceso a documentos sensibles del tenant
/// activo, sin filtros ni exportación — eso es otro incremento si hace
/// falta. La autorización real vive en el endpoint/página que la sirve
/// (<c>Policies.ConsultarAccesoDocumentosSensibles</c>, RequireRole +
/// RequireClaim); esta query no repite la comprobación porque Application no
/// conoce Identity — mismo criterio que el resto de queries de este proyecto.
/// </summary>
public record ObtenerAccesosDocumentosSensiblesQuery(
    int Pagina = 1, int TamanoPagina = 30) : IRequest<ResultadoPaginado<AccesoDocumentoSensibleDto>>;

/// <param name="DocumentoTitulo">
/// Nombre del Tipo de documento («Reconocimiento médico de aptitud»), para no
/// enseñar el GUID en crudo (revisión UX pre-piloto 2026-09-28, D.1). Se resuelve
/// con los filtros normales de Documentos —Tenant activo y sin dar de baja—, sin
/// <c>IgnoreQueryFilters()</c>: null cuando el documento ya no se puede leer (dado
/// de baja), y la pantalla cae entonces al identificador. No amplía lo que ve quien
/// consulta: el nombre del tipo de un documento del mismo Tenant propietario.
/// </param>
public record AccesoDocumentoSensibleDto(
    Guid Id,
    Guid DocumentoId,
    SensibilidadDocumental Sensibilidad,
    TipoAccesoDocumentoSensible TipoAcceso,
    Guid? UsuarioId,
    DateTime OcurridoEnUtc,
    TipoViaAccesoAuditoria ViaAcceso,
    bool EsPrivilegiado,
    string? DocumentoTitulo = null);

public class ObtenerAccesosDocumentosSensiblesQueryHandler(
    IAuditoriaQueryContext dbContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext)
    : IRequestHandler<ObtenerAccesosDocumentosSensiblesQuery, ResultadoPaginado<AccesoDocumentoSensibleDto>>
{
    public async Task<ResultadoPaginado<AccesoDocumentoSensibleDto>> Handle(
        ObtenerAccesosDocumentosSensiblesQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.RegistrosAccesoDocumentoSensible.AsQueryable();

        var total = await consulta.CountAsync(cancellationToken);

        var elementos = await consulta
            .OrderByDescending(r => r.OcurridoEnUtc)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(r => new AccesoDocumentoSensibleDto(
                r.Id, r.DocumentoId, r.Sensibilidad, r.TipoAcceso, r.UsuarioId, r.OcurridoEnUtc,
                r.ViaAcceso, r.ViaAcceso == TipoViaAccesoAuditoria.SesionPrivilegiada))
            .ToListAsync(cancellationToken);

        // Un lote por página, nunca una consulta por fila.
        var idsDocumento = elementos.Select(e => e.DocumentoId).Distinct().ToArray();
        if (idsDocumento.Length > 0)
        {
            var titulos = await (
                    from documento in documentosContext.Documentos
                    join tipo in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipo.Id
                    where idsDocumento.Contains(documento.Id)
                    select new { documento.Id, tipo.Nombre })
                .ToDictionaryAsync(d => d.Id, d => d.Nombre, cancellationToken);

            elementos = elementos
                .Select(e => e with { DocumentoTitulo = titulos.GetValueOrDefault(e.DocumentoId) })
                .ToList();
        }

        return new ResultadoPaginado<AccesoDocumentoSensibleDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
