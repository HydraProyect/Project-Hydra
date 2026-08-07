using CaeManager.Application.Integraciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Integraciones.Queries.ObtenerProveedoresPlataformaCae;

/// <summary>
/// Catálogo completo de <c>ProveedorPlataformaCae</c> con sus dominios — para
/// el selector de proveedor de un canal de gestión (Parte 2 § (a),
/// PLAN-EJECUCION-UX.md) y para la futura pantalla de administración del
/// catálogo. <paramref name="SoloActivos"/> filtra los sembrados sin foco CAE
/// real (Arch, Opground) fuera de los selectores de alta.
/// </summary>
public record ObtenerProveedoresPlataformaCaeQuery(bool SoloActivos = true) : IRequest<IReadOnlyList<ProveedorPlataformaCaeListaDto>>;

public record ProveedorPlataformaCaeListaDto(
    Guid Id, string Codigo, string Nombre, string? Grupo, bool Activo, IReadOnlyList<string> Dominios);

public class ObtenerProveedoresPlataformaCaeQueryHandler(IProveedoresPlataformaCaeQueryContext queryContext)
    : IRequestHandler<ObtenerProveedoresPlataformaCaeQuery, IReadOnlyList<ProveedorPlataformaCaeListaDto>>
{
    public async Task<IReadOnlyList<ProveedorPlataformaCaeListaDto>> Handle(
        ObtenerProveedoresPlataformaCaeQuery request, CancellationToken cancellationToken)
    {
        var proveedores = queryContext.ProveedoresPlataformaCae.AsQueryable();

        if (request.SoloActivos)
            proveedores = proveedores.Where(p => p.Activo);

        var lista = await proveedores
            .OrderBy(p => p.Nombre)
            .Select(p => new { p.Id, p.Codigo, p.Nombre, p.Grupo, p.Activo })
            .ToListAsync(cancellationToken);

        var dominiosPorProveedor = await queryContext.DominiosProveedorPlataformaCae
            .Where(d => lista.Select(p => p.Id).Contains(d.ProveedorPlataformaCaeId))
            .Select(d => new { d.ProveedorPlataformaCaeId, d.Dominio })
            .ToListAsync(cancellationToken);

        var dominiosPorId = dominiosPorProveedor
            .GroupBy(d => d.ProveedorPlataformaCaeId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(d => d.Dominio).OrderBy(d => d).ToList());

        return lista
            .Select(p => new ProveedorPlataformaCaeListaDto(
                p.Id, p.Codigo, p.Nombre, p.Grupo, p.Activo,
                dominiosPorId.GetValueOrDefault(p.Id, [])))
            .ToList();
    }
}
