using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Catalogo;
using CaeManager.Domain.Configuracion;
using MediatR;

namespace CaeManager.Application.Dashboard.Queries;

public record ObtenerPreferenciaDashboardQuery : IRequest<IReadOnlyList<string>>;

/// <summary>
/// Devuelve los códigos de KPI que el usuario actual ha elegido ver. Sin
/// preferencia guardada, devuelve el conjunto por defecto de su rol efectivo
/// (<see cref="CatalogoKpis.KpisPorDefectoPorRol"/>) — el de dentro del Delegated
/// Workspace, no el del claim de origen. Filtra defensivamente los códigos
/// guardados contra el catálogo vigente: si un código ya no existe (el
/// catálogo cambió), se descarta en vez de romper el render.
/// </summary>
public class ObtenerPreferenciaDashboardQueryHandler(
    ICurrentUserService currentUserService, IPreferenciaDashboardUsuarioRepository repositorio)
    : IRequestHandler<ObtenerPreferenciaDashboardQuery, IReadOnlyList<string>>
{
    public async Task<IReadOnlyList<string>> Handle(ObtenerPreferenciaDashboardQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var porDefecto = CatalogoKpis.KpisPorDefectoPorRol(await currentUserService.ObtenerRolActualAsync());

        if (usuarioId is null) return porDefecto;

        var preferencia = await repositorio.ObtenerPorUsuarioIdAsync(usuarioId.Value, cancellationToken);
        if (preferencia is null) return porDefecto;

        var codigosConocidos = CatalogoKpis.Todos.Select(k => k.Codigo).ToHashSet();
        var seleccionVigente = preferencia.CodigosKpiSeleccionados.Where(codigosConocidos.Contains).ToList();

        return seleccionVigente.Count == 0 ? porDefecto : seleccionVigente;
    }
}
