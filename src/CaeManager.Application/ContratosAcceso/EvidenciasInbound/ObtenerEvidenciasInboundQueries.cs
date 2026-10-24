using CaeManager.Application.Common;
using CaeManager.Application.ContratosAcceso.LecturaInbound;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.EvidenciasInbound;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.ContratosAcceso.EvidenciasInbound;

// Lecturas de las evidencias del Tenant del Cliente empresarial: no tocan el otro Tenant (son copias propias) y siguen
// después de una revocación. La autoridad es la de LecturaInboundAutoridad sobre el contrato; null = no encontrado, sin
// distinguir contrato invisible, rol que no valida o evidencia inexistente. La referencia del archivo copiado no sale.

public record ObtenerEvidenciasInboundQuery(Guid ContratoAccesoId) : IRequest<IReadOnlyList<EvidenciaInboundDto>?>;

public record ObtenerEvidenciaInboundPorIdQuery(Guid EvidenciaId) : IRequest<EvidenciaInboundDto?>;

public class ObtenerEvidenciasInboundQueryHandler(
    IEvidenciasInboundQueryContext evidenciasContext, ITiposDocumentoQueryContext tiposContext,
    IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos, ICurrentUserService currentUser)
    : IRequestHandler<ObtenerEvidenciasInboundQuery, IReadOnlyList<EvidenciaInboundDto>?>
{
    public async Task<IReadOnlyList<EvidenciaInboundDto>?> Handle(ObtenerEvidenciasInboundQuery request, CancellationToken cancellationToken)
    {
        if (!await LecturaInboundAutoridad.PuedeLeerAsync(request.ContratoAccesoId, contratosContext, alcanceDatos, currentUser, cancellationToken))
            return null;

        var evidencias = await evidenciasContext.EvidenciasInbound
            .Where(e => e.ContratoAccesoId == request.ContratoAccesoId)
            .OrderByDescending(e => e.ValidadaEnUtc).ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);
        return await EvidenciasInboundProyeccion.ADtosAsync(evidencias, tiposContext, cancellationToken);
    }
}

public class ObtenerEvidenciaInboundPorIdQueryHandler(
    IEvidenciasInboundQueryContext evidenciasContext, ITiposDocumentoQueryContext tiposContext,
    IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos, ICurrentUserService currentUser)
    : IRequestHandler<ObtenerEvidenciaInboundPorIdQuery, EvidenciaInboundDto?>
{
    public async Task<EvidenciaInboundDto?> Handle(ObtenerEvidenciaInboundPorIdQuery request, CancellationToken cancellationToken)
    {
        var evidencia = await evidenciasContext.EvidenciasInbound.FirstOrDefaultAsync(e => e.Id == request.EvidenciaId, cancellationToken);
        if (evidencia is null
            || !await LecturaInboundAutoridad.PuedeLeerAsync(evidencia.ContratoAccesoId, contratosContext, alcanceDatos, currentUser, cancellationToken))
            return null;

        return (await EvidenciasInboundProyeccion.ADtosAsync([evidencia], tiposContext, cancellationToken)).Single();
    }
}

internal static class EvidenciasInboundProyeccion
{
    public static async Task<IReadOnlyList<EvidenciaInboundDto>> ADtosAsync(
        IReadOnlyList<EvidenciaInbound> evidencias, ITiposDocumentoQueryContext tiposContext, CancellationToken cancellationToken)
    {
        var tipoIds = evidencias.Select(e => e.TipoDocumentoId).Distinct().ToList();
        var nombres = await tiposContext.TiposDocumento
            .Where(t => tipoIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        return evidencias.Select(e => new EvidenciaInboundDto(
                e.Id, e.ContratoAccesoId, e.TenantOrigenId, e.DocumentoOrigenId, e.AutorizacionId, e.TipoDocumentoId,
                nombres.GetValueOrDefault(e.TipoDocumentoId, string.Empty), e.TrabajadorOrigenId, e.SujetoNombre,
                e.SujetoDocumentoIdentidad, e.FechaEmision, e.FechaVencimiento, e.SoloConclusionAptitud, e.ConclusionAptitud,
                e.ArchivoUrl is not null, e.HuellaSha256, e.Estado, e.CausaRechazo, e.MotivoRechazo, e.ValidadaPorUsuarioId,
                e.ValidadaEnUtc))
            .ToList();
    }
}
