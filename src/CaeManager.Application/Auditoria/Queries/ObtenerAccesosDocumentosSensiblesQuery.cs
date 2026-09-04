using CaeManager.Application.Common;
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

public record AccesoDocumentoSensibleDto(
    Guid Id,
    Guid DocumentoId,
    SensibilidadDocumental Sensibilidad,
    TipoAccesoDocumentoSensible TipoAcceso,
    Guid? UsuarioId,
    DateTime OcurridoEnUtc,
    TipoViaAccesoAuditoria ViaAcceso,
    bool EsPrivilegiado);

public class ObtenerAccesosDocumentosSensiblesQueryHandler(IAuditoriaQueryContext dbContext)
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

        return new ResultadoPaginado<AccesoDocumentoSensibleDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
