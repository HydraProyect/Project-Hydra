using CaeManager.Application.Common;
using CaeManager.Domain.VigilanciaNormativa;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.VigilanciaNormativa.Queries.ObtenerAvisosRevisionNormativa;

public record AvisoRevisionNormativaDto(
    Guid Id,
    string IdentificadorBoe,
    DateOnly FechaPublicacion,
    string Titulo,
    string UrlHtml,
    string NormaVigilada,
    bool Revisado,
    DateTime? RevisadoEnUtc);

/// <summary>
/// DEC-8 (plan de sesiones nocturnas 2026-09-02): superficie de lectura
/// difundida, no un ítem de cola. Cualquier usuario autenticado la ve —
/// tenant beneficiario o Actor de Plataforma TALVEG por igual, sin
/// distinción de rol — porque el aviso es informativo para toda la
/// jerarquía, no una tarea con dueño. La distinción de audiencia real está
/// en la ESCRITURA (<c>MarcarAvisoRevisionNormativaRevisadoCommand</c>,
/// exclusiva del Actor de Plataforma), no en esta lectura.
/// </summary>
public record ObtenerAvisosRevisionNormativaQuery : IRequest<IReadOnlyList<AvisoRevisionNormativaDto>>;

public class ObtenerAvisosRevisionNormativaQueryHandler(
    IVigilanciaNormativaQueryContext queryContext, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerAvisosRevisionNormativaQuery, IReadOnlyList<AvisoRevisionNormativaDto>>
{
    /// <summary>
    /// Cota de lectura, no de dominio: el BOE se sondea a diario y la lista
    /// crece sin fin. Cinco normas vigiladas (<see cref="NormasVigiladas"/>)
    /// producen pocas coincidencias reales, así que 60 cubre con margen más
    /// de un año sin paginar — ampliar esto a paginación real es trabajo de
    /// seguimiento si el catálogo de normas crece.
    /// </summary>
    public const int MaximoAvisos = 60;

    public async Task<IReadOnlyList<AvisoRevisionNormativaDto>> Handle(
        ObtenerAvisosRevisionNormativaQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return [];

        return await queryContext.AvisosRevisionNormativa
            .OrderByDescending(a => a.FechaPublicacion)
            .ThenByDescending(a => a.DetectadoEnUtc)
            .Take(MaximoAvisos)
            .Select(a => new AvisoRevisionNormativaDto(
                a.Id, a.IdentificadorBoe, a.FechaPublicacion, a.Titulo, a.UrlHtml, a.NormaVigilada,
                a.Revisado, a.RevisadoEnUtc))
            .ToListAsync(cancellationToken);
    }
}
