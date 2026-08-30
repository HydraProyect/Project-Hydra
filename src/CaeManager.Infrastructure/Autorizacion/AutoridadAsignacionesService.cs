using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Autorizacion;

/// <summary>
/// Implementación de <see cref="IAutoridadAsignacionesService"/>. Vive en
/// Infrastructure por el mismo motivo que <see cref="AlcanceDatosService"/>:
/// necesita los nombres de rol de <see cref="Roles"/>, que Application no
/// puede referenciar.
///
/// <para>
/// <b>Dos preguntas, en este orden, y la primera no se deriva de la
/// segunda:</b>
/// </para>
/// <list type="number">
/// <item>
/// ¿Tiene este rol <b>capacidad administrativa</b> sobre asignaciones? Es una
/// propiedad del rol, no del dato. <c>Consulta</c> lo ve todo y no la tiene;
/// <c>Cliente</c> tampoco. Aquí se responde que no <b>antes</b> de mirar
/// ningún ámbito, que es lo que impide que «ve todo» se convierta en «puede
/// todo».
/// </item>
/// <item>
/// ¿Está el centro <b>dentro de su ámbito</b>? Solo para los roles que
/// pasaron la primera. El ámbito se deriva de la misma cartera operativa que
/// alimenta la lectura — una sola fuente de verdad, dos preguntas distintas
/// sobre ella.
/// </item>
/// </list>
///
/// <para>
/// <b>Una sesión privilegiada de plataforma nunca tiene autoridad</b>, y se
/// comprueba antes que el rol por el mismo motivo que en
/// <c>AutorizacionEscrituraBehavior</c>: hoy su rol efectivo es <c>null</c> y
/// caería igual, pero eso es una consecuencia de cómo se resuelve el rol, no
/// una decisión tomada aquí. La inspección de soporte es de solo lectura, y
/// esa regla se escribe donde se aplica.
/// </para>
/// </summary>
public class AutoridadAsignacionesService(
    CaeManagerDbContext dbContext,
    ICurrentUserService currentUserService,
    IAlcanceDatosService alcanceDatos,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual)
    : IAutoridadAsignacionesService
{
    /// <summary>
    /// Los roles con capacidad administrativa sobre asignaciones.
    /// <c>Consulta</c> y <c>Cliente</c> quedan fuera <b>aunque vean el
    /// centro</b>; cualquier otro valor —incluido <c>null</c>— tampoco, que es
    /// la única forma segura de equivocarse (misma lista blanca que
    /// <c>AutorizacionEscrituraBehavior</c>, y por el mismo motivo).
    /// </summary>
    private static readonly string[] RolesConAutoridadSobreAsignaciones =
        [Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae];

    public async Task<bool> PuedeModificarAsignacionesDelCentroAsync(
        Guid centroId, CancellationToken cancellationToken = default)
    {
        var centrosPermitidos = await FiltrarCentrosConAutoridadAsync([centroId], cancellationToken);
        return centrosPermitidos.Count == 1;
    }

    public async Task<IReadOnlyList<Guid>> FiltrarCentrosConAutoridadAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken = default)
    {
        if (centroIds.Count == 0) return [];

        if (!await TieneCapacidadAdministrativaAsync(cancellationToken)) return [];

        // El centro tiene que existir en el tenant además de estar en el
        // ámbito: sin esto, un rol de ámbito universal (Administrador) haría
        // pasar un Id inventado y el comando fallaría más tarde y peor.
        var centrosDelTenant = await dbContext.Centros
            .Where(c => centroIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (centrosDelTenant.Count == 0) return [];

        var centrosEnAmbito = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);

        // null = el rol no tiene restricción de cartera (Administrador,
        // DireccionCae). Llegados aquí ya sabemos que SÍ tiene capacidad
        // administrativa, así que la ausencia de restricción es autoridad
        // real, no visibilidad prestada.
        if (centrosEnAmbito is null) return centrosDelTenant;

        var enAmbito = centrosEnAmbito.ToHashSet();
        return centrosDelTenant.Where(enAmbito.Contains).ToList();
    }

    public async Task<bool> PuedeModificarAsignacionesDelTrabajadorAsync(
        Guid trabajadorId, CancellationToken cancellationToken = default)
    {
        var trabajadoresPermitidos = await FiltrarTrabajadoresConAutoridadAsync([trabajadorId], cancellationToken);
        return trabajadoresPermitidos.Count == 1;
    }

    public async Task<IReadOnlyList<Guid>> FiltrarTrabajadoresConAutoridadAsync(
        IReadOnlyList<Guid> trabajadorIds, CancellationToken cancellationToken = default)
    {
        if (trabajadorIds.Count == 0) return [];

        if (!await TieneCapacidadAdministrativaAsync(cancellationToken)) return [];

        var trabajadoresDelTenant = await dbContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        if (trabajadoresDelTenant.Count == 0) return [];

        var trabajadoresEnAmbito = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);

        if (trabajadoresEnAmbito is null) return trabajadoresDelTenant;

        var enAmbito = trabajadoresEnAmbito.ToHashSet();
        return trabajadoresDelTenant.Where(enAmbito.Contains).ToList();
    }

    private async Task<bool> TieneCapacidadAdministrativaAsync(CancellationToken cancellationToken)
    {
        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not null) return false;

        var rol = await currentUserService.ObtenerRolActualAsync();
        return rol is not null && RolesConAutoridadSobreAsignaciones.Contains(rol);
    }
}
