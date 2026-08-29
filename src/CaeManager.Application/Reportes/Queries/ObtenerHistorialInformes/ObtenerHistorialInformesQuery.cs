using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reportes.Queries.ObtenerHistorialInformes;

public record ObtenerHistorialInformesQuery(int Limite = 10) : IRequest<IReadOnlyList<HistorialInformeDto>>;

public record HistorialInformeDto(Guid Id, string TipoInforme, string? ClienteNombre, DateTime GeneradoEnUtc, Guid GeneradoPorUsuarioId);

/// <summary>
/// El panel "Historial" de /reportes congela la razón social del Cliente en
/// cada fila (ver <c>HistorialInforme</c>), así que sin acotar por cartera
/// enseñaba a cualquier Gestor CAE los nombres de los clientes de los demás.
/// Un usuario con cartera restringida ve dos cosas: lo que él mismo generó
/// (incluidos los informes de "toda la cartera", que no llevan ClienteId) y
/// lo que otros generaron sobre Clientes que él sí puede ver.
/// </summary>
public class ObtenerHistorialInformesQueryHandler(
    IReportesQueryContext reportesContext, IAlcanceDatosService alcanceDatos, ICurrentUserService usuarioActual)
    : IRequestHandler<ObtenerHistorialInformesQuery, IReadOnlyList<HistorialInformeDto>>
{
    public async Task<IReadOnlyList<HistorialInformeDto>> Handle(ObtenerHistorialInformesQuery request, CancellationToken cancellationToken)
    {
        var consulta = reportesContext.HistorialInformes;

        if (await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken) is { } clienteIdsVisibles)
        {
            // Guid.Empty cuando no hay usuario resuelto (fuera de un circuito
            // de Blazor): no coincide con ninguna fila real, así que el filtro
            // falla cerrado, igual que RegistrarHistorialInformeCommandHandler.
            var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync() ?? Guid.Empty;
            consulta = consulta.Where(h =>
                h.GeneradoPorUsuarioId == usuarioId ||
                (h.ClienteId != null && clienteIdsVisibles.Contains(h.ClienteId.Value)));
        }

        return await consulta
            .OrderByDescending(h => h.GeneradoEnUtc)
            .Take(request.Limite)
            .Select(h => new HistorialInformeDto(h.Id, h.TipoInforme, h.ClienteNombre, h.GeneradoEnUtc, h.GeneradoPorUsuarioId))
            .ToListAsync(cancellationToken);
    }
}
