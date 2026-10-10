using CaeManager.Application.Common;
using CaeManager.Domain.Configuracion;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Configuracion.Queries;

/// <summary>
/// Los filtros con nombre del usuario actual en una pantalla, en el Tenant actual.
/// La fila reservada de la vista recordada comparte tabla y clave pero no es un
/// filtro con nombre: nunca sale en este listado (se lee con
/// <see cref="ObtenerVistaRecordadaQuery"/>).
/// </summary>
public record ObtenerFiltrosGuardadosQuery(string Pantalla) : IRequest<IReadOnlyList<FiltroGuardadoDto>>;

public record FiltroGuardadoDto(Guid Id, string Nombre, string ValoresJson, DateTime CreadoEnUtc);

public class ObtenerFiltrosGuardadosQueryHandler(IConfiguracionQueryContext dbContext, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerFiltrosGuardadosQuery, IReadOnlyList<FiltroGuardadoDto>>
{
    public async Task<IReadOnlyList<FiltroGuardadoDto>> Handle(ObtenerFiltrosGuardadosQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return [];

        return await dbContext.FiltrosGuardados
            .Where(f => f.UsuarioId == usuarioId.Value
                && f.Pantalla == request.Pantalla
                && f.Nombre != FiltroGuardado.NombreVistaRecordada)
            .OrderBy(f => f.Nombre)
            .Select(f => new FiltroGuardadoDto(f.Id, f.Nombre, f.ValoresJson, f.CreadoEnUtc))
            .ToListAsync(cancellationToken);
    }
}
