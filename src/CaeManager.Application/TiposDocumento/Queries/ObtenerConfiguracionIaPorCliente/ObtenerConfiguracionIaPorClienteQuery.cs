using CaeManager.Application.Common;
using CaeManager.Application.TiposDocumento;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.TiposDocumento.Queries.ObtenerConfiguracionIaPorCliente;

public record ConfiguracionIaTipoDocumentoDto(
    Guid TipoDocumentoId,
    string Nombre,
    bool GlobalActiva,
    bool? OverrideActiva,
    bool DeteccionTrabajadoresActiva)
{
    /// <summary>Permiso combinado de los niveles 1 y 2: el nivel 1 manda si está desactivado; si no, se aplica el valor del nivel 2 (activo si no hay valor propio). No incluye la instrucción de tratamiento del Nivel 0 ni las condiciones del documento o del servicio de lectura.</summary>
    public bool EfectivaActiva => GlobalActiva && (OverrideActiva ?? true);
}

public record ObtenerConfiguracionIaPorClienteQuery(Guid ClienteId) : IRequest<IReadOnlyList<ConfiguracionIaTipoDocumentoDto>>;

public class ObtenerConfiguracionIaPorClienteQueryHandler(ITiposDocumentoQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerConfiguracionIaPorClienteQuery, IReadOnlyList<ConfiguracionIaTipoDocumentoDto>>
{
    public async Task<IReadOnlyList<ConfiguracionIaTipoDocumentoDto>> Handle(
        ObtenerConfiguracionIaPorClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteId, cancellationToken)) return [];

        var tipos = await dbContext.TiposDocumento
            .OrderBy(t => t.Orden)
            .Select(t => new { t.Id, t.Nombre, t.LecturaIaActiva, t.DeteccionTrabajadoresActiva })
            .ToListAsync(cancellationToken);

        var overrides = await dbContext.ConfiguracionesIaDocumentoCliente
            .Where(c => c.ClienteId == request.ClienteId)
            .ToDictionaryAsync(c => c.TipoDocumentoId, c => c.Activa, cancellationToken);

        return tipos
            .Select(t => new ConfiguracionIaTipoDocumentoDto(
                t.Id, t.Nombre, t.LecturaIaActiva, overrides.TryGetValue(t.Id, out var activa) ? activa : null,
                t.DeteccionTrabajadoresActiva))
            .ToList();
    }
}
