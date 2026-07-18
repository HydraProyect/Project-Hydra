using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.TiposDocumento.Queries.ObtenerConfiguracionIaPorCliente;

public record ConfiguracionIaTipoDocumentoDto(Guid TipoDocumentoId, string Nombre, bool GlobalActiva, bool? OverrideActiva)
{
    /// <summary>Estado real aplicado: el nivel 1 (global) manda si está desactivado; si no, manda el override del nivel 2 (o el default activo si no hay override).</summary>
    public bool EfectivaActiva => GlobalActiva && (OverrideActiva ?? true);
}

public record ObtenerConfiguracionIaPorClienteQuery(Guid ClienteId) : IRequest<IReadOnlyList<ConfiguracionIaTipoDocumentoDto>>;

public class ObtenerConfiguracionIaPorClienteQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerConfiguracionIaPorClienteQuery, IReadOnlyList<ConfiguracionIaTipoDocumentoDto>>
{
    public async Task<IReadOnlyList<ConfiguracionIaTipoDocumentoDto>> Handle(
        ObtenerConfiguracionIaPorClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteId, cancellationToken)) return [];

        var tipos = await dbContext.TiposDocumento
            .OrderBy(t => t.Orden)
            .Select(t => new { t.Id, t.Nombre, t.LecturaIaActiva })
            .ToListAsync(cancellationToken);

        var overrides = await dbContext.ConfiguracionesIaDocumentoCliente
            .Where(c => c.ClienteId == request.ClienteId)
            .ToDictionaryAsync(c => c.TipoDocumentoId, c => c.Activa, cancellationToken);

        return tipos
            .Select(t => new ConfiguracionIaTipoDocumentoDto(
                t.Id, t.Nombre, t.LecturaIaActiva, overrides.TryGetValue(t.Id, out var activa) ? activa : null))
            .ToList();
    }
}
