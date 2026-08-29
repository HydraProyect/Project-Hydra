using CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;
using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.BusquedaGlobal.Queries.ObtenerRecientes;

/// <summary>
/// Alimenta el grupo "Recientes" del estado inicial del Command Palette
/// (query vacía). Reutiliza <see cref="ItemBusquedaDto"/> — el mismo DTO que
/// ya usan los resultados de búsqueda — añadiendo el <c>Tipo</c> que
/// necesita el palette para elegir el icono.
/// </summary>
public record ObtenerRecientesQuery : IRequest<IReadOnlyList<ItemRecienteDto>>;

public record ItemRecienteDto(string Tipo, Guid? EntidadId, string Titulo, string? Subtitulo, string UrlDestino);

public class ObtenerRecientesQueryHandler(IBusquedaGlobalQueryContext busquedaGlobalContext, ICurrentUserService usuarioActual)
    : IRequestHandler<ObtenerRecientesQuery, IReadOnlyList<ItemRecienteDto>>
{
    /// <summary>Cuántos eventos recientes se traen de base de datos antes de deduplicar — ver "condición de carrera" del plan.</summary>
    private const int MaximoATraer = 50;

    /// <summary>Cuántos "recientes" distintos se muestran en el palette tras deduplicar.</summary>
    private const int MaximoAMostrar = 6;

    public async Task<IReadOnlyList<ItemRecienteDto>> Handle(ObtenerRecientesQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return [];

        // WHERE usuario + ORDER BY OcurridoEnUtc DESC + LIMIT, nunca SELECT
        // DISTINCT: un DISTINCT sobre las columnas visibles no garantiza
        // quedarse con la fila más reciente de cada UrlDestino repetida. El
        // filtro de TenantId ya lo aplica el HasQueryFilter global de
        // CaeManagerDbContext — no hace falta repetirlo aquí.
        var eventos = await busquedaGlobalContext.EventosRecientesUsuario
            .Where(e => e.UsuarioId == usuarioId.Value)
            .OrderByDescending(e => e.OcurridoEnUtc)
            .Take(MaximoATraer)
            .Select(e => new { e.Tipo, e.EntidadId, e.Titulo, e.Subtitulo, e.UrlDestino, e.OcurridoEnUtc })
            .ToListAsync(cancellationToken);

        // Deduplicar por UrlDestino en memoria, recorriendo en el orden ya
        // descendente que trajo la query: la primera vez que aparece una
        // UrlDestino es, por construcción, su ocurrencia más reciente.
        var urlsVistas = new HashSet<string>();
        var recientes = new List<ItemRecienteDto>();

        foreach (var evento in eventos)
        {
            if (recientes.Count >= MaximoAMostrar) break;
            if (!urlsVistas.Add(evento.UrlDestino)) continue;

            var subtitulo = evento.Tipo == "Accion"
                ? TiempoRelativoTexto.Formatear(evento.OcurridoEnUtc)
                : evento.Subtitulo;

            recientes.Add(new ItemRecienteDto(evento.Tipo, evento.EntidadId, evento.Titulo, subtitulo, evento.UrlDestino));
        }

        return recientes;
    }
}
