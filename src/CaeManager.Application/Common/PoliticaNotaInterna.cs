using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;

namespace CaeManager.Application.Common;

/// <summary>
/// Decide quién lee la «Nota interna» de una ficha 360 (Cliente, Subcontrata, Vehículo, Tipo de documento). Es la única
/// regla de lectura: ninguna consulta la repite.
///
/// <para>
/// <b>La regla.</b> La nota la lee quien pertenece al equipo del Tenant propietario, es decir, quien tiene uno de los
/// roles de <see cref="RolesQueVenLaNotaInterna"/>, <b>o</b> quien abre el Tenant con una Sesión Privilegiada vigente
/// cuya capacidad da acceso total a ese Tenant (<see cref="CapacidadesConAccesoTotal"/>). El Actor de Plataforma TALVEG
/// actúa para ayudar al Tenant: ve lo mismo que su equipo, y lo que la nota protege es que no llegue al cliente.
/// </para>
///
/// <para>
/// <b>Lo que no entra.</b> El rol Cliente («Usuario de Cliente») nunca la recibe: no está en la lista y no tiene
/// Sesión Privilegiada. Un rol nulo, vacío o desconocido tampoco, porque la lectura es lista blanca. La sesión se
/// comprueba por su vigencia real (<see cref="ISesionPrivilegiadaActual.ObtenerAsync"/>), no por un parámetro: una
/// coordenada de contexto no es autoridad. Solo <c>SoporteLectura</c>, <c>BreakGlass</c> y <c>Aprovisionamiento</c>
/// dan acceso total; <c>Impersonacion</c> lee como la persona simulada (su rol efectivo), y <c>AdminPlataforma</c>
/// no lee contenido de ningún cliente.
/// </para>
///
/// <para>
/// <b>Escritura.</b> Esta política no concede escribir. Quien guarda la nota pasa por <c>AutorizacionEscrituraBehavior</c>
/// y por el alcance de gestión de cada handler; una Sesión de solo lectura (<c>SoporteLectura</c>) lee y no escribe.
/// </para>
/// </summary>
public interface IPoliticaNotaInterna
{
    /// <summary>
    /// <c>true</c> si el usuario actual puede leer la nota interna de las fichas 360 del Tenant actual.
    /// </summary>
    Task<bool> PuedeLeerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>true</c> si hay una Sesión Privilegiada vigente con acceso total al Tenant actual. Lo usan las puertas de
    /// página que, además de la nota, abren la ficha: quien ve la nota por esta vía ve la ficha igual que el equipo.
    /// </summary>
    Task<bool> TieneAccesoTotalAlTenantAsync(CancellationToken cancellationToken = default);
}

public sealed class PoliticaNotaInterna(
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ITenantActual tenantActual) : IPoliticaNotaInterna
{
    // Literales, no Infrastructure.Identity.Roles: Application no puede referenciar Infrastructure. Lista blanca del
    // equipo de gestión: un rol nuevo no la ve hasta que se añade aquí a propósito.
    public static readonly IReadOnlyList<string> RolesQueVenLaNotaInterna =
        ["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae", "Consulta"];

    // Las tres capacidades que dan acceso total al Tenant objetivo; el mismo criterio que AlcanceDatosService.
    public static readonly IReadOnlySet<CapacidadPrivilegio> CapacidadesConAccesoTotal =
        new HashSet<CapacidadPrivilegio> { CapacidadPrivilegio.SoporteLectura, CapacidadPrivilegio.BreakGlass, CapacidadPrivilegio.Aprovisionamiento };

    public async Task<bool> PuedeLeerAsync(CancellationToken cancellationToken = default)
    {
        if (await TieneAccesoTotalAlTenantAsync(cancellationToken)) return true;

        var rol = await currentUserService.ObtenerRolEfectivoAsync();
        return rol is not null && RolesQueVenLaNotaInterna.Contains(rol);
    }

    public async Task<bool> TieneAccesoTotalAlTenantAsync(CancellationToken cancellationToken = default)
    {
        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not { } sesion) return false;

        // Solo dentro del Tenant que la sesión abrió: TenantObjetivoId es un dato de la concesión, no del contexto.
        return sesion.TenantObjetivoId == tenantActual.TenantId && CapacidadesConAccesoTotal.Contains(sesion.Capacidad);
    }
}
