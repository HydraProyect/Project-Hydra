using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;

namespace CaeManager.Web.Features.Bandeja.Components;

/// <summary>
/// Recorte de <c>MiTrabajoTenantDto</c> (Application) ya filtrado por el chip
/// de severidad de <c>MiTrabajo.razor</c> — <see cref="Bloqueos"/>/
/// <see cref="Actuaciones"/> viajan aparte de <see cref="BloqueoActuacion"/>
/// porque el resumen de la cabecera cuenta sobre el filtro activo (mismo
/// criterio que el mockup Gen2: <c>bl</c>/<c>ac</c> se calculan sobre
/// <c>todas</c>, ya filtrada por <c>state.sev</c>), no sobre el total sin
/// filtrar de <c>ResumenMiTrabajoTenantDto</c>.
/// </summary>
public sealed record MiTrabajoTenantVista(
    Guid TenantId,
    string TenantNombre,
    bool EsOrigen,
    BandejaAgrupadaDto BloqueoActuacion,
    IReadOnlyList<ItemBandejaDto> Proximos,
    IReadOnlyList<ItemBandejaDto> Seguimiento,
    int Bloqueos,
    int Actuaciones);
