using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.VistaUnificada;

/// <summary>
/// Quien mira la vista unificada de Comunicaciones (D-6 b, § 4.1): el usuario, su Tenant de origen y el perfil de origen
/// que decide qué Tenants beneficiarios alcanza. Sale siempre de la sesión (<see cref="ResolverAsync"/>), nunca de la
/// petición: no tiene constructor accesible.
/// </summary>
public sealed class IdentidadVistaUnificada
{
    private IdentidadVistaUnificada(Guid usuarioId, Guid tenantOrigenId, string? rolDeOrigen)
    {
        UsuarioId = usuarioId;
        TenantOrigenId = tenantOrigenId;
        RolDeOrigen = rolDeOrigen;
    }

    public Guid UsuarioId { get; }

    public Guid TenantOrigenId { get; }

    /// <summary><see cref="ICurrentUserService.ObtenerPerfilQueDecideAlcanceAsync"/>, el mismo perfil que el selector.</summary>
    public string? RolDeOrigen { get; }

    /// <summary>La identidad de la sesión, o <c>null</c> sin usuario o sin Tenant de origen.</summary>
    public static async Task<IdentidadVistaUnificada?> ResolverAsync(ICurrentUserService currentUserService)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (usuarioId is null || tenantOrigenId is null) return null;
        return new(usuarioId.Value, tenantOrigenId.Value, await currentUserService.ObtenerPerfilQueDecideAlcanceAsync());
    }
}

/// <summary>
/// El predicado único de la vista unificada de Comunicaciones (D-6 b, § 4.2): qué Tenants entran en ella.
/// <list type="bullet">
/// <item>El Tenant de origen, solo si el usuario opera en él:
/// <see cref="TenantsBeneficiariosAutorizados.OrigenGestionadoAsync"/> (la regla con la que el selector decide si el
/// origen cuenta como Tenant de la cartera). Pertenecer a él no basta.</item>
/// <item>Los Tenants beneficiarios de la vía de Operación Outbound:
/// <see cref="TenantsBeneficiariosAutorizados.OperacionesQueAutorizan"/>, que solo cuenta operaciones de
/// <see cref="TenantsBeneficiariosAutorizados.ServicioDelContexto"/> (DN-1 (a): sin Inbound), vigentes por reloj, no
/// suspendidas y, para el Gestor CAE, con Asignación de Cartera vigente por reloj.</item>
/// </list>
/// <b>Sin ninguna vía heredada</b> (DN-2 (a)): no llama a <c>ObtenerClientesAutorizadosQuery</c> ni a
/// <see cref="TenantsBeneficiariosAutorizados.EstaAutorizadoAsync"/>, que la suman, y se lee siempre de la base, sin la
/// memoización del selector (§ 4.5). Cada evaluación usa un único <c>ahora</c> (§ 5.3).
/// </summary>
public static class PredicadoVistaUnificada
{
    /// <summary>El conjunto de la vista, con un único <paramref name="ahora"/>.</summary>
    public static async Task<IReadOnlyList<Guid>> ConjuntoAsync(
        IdentidadVistaUnificada identidad, IOperacionesQueryContext operaciones, ITenantsQueryContext tenants,
        DateTime ahora, CancellationToken cancellationToken)
    {
        var externos = await TenantsBeneficiariosAutorizados
            .OperacionesQueAutorizan(
                operaciones, tenants, identidad.UsuarioId, identidad.TenantOrigenId, identidad.RolDeOrigen, ahora)
            .Select(o => o.PropietarioTenantId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var conjunto = externos.Where(t => t != identidad.TenantOrigenId).ToList();
        if (await TenantsBeneficiariosAutorizados.OrigenGestionadoAsync(
                operaciones, identidad.UsuarioId, identidad.TenantOrigenId, ahora, cancellationToken))
            conjunto.Insert(0, identidad.TenantOrigenId);
        return conjunto;
    }

    /// <summary>
    /// <paramref name="tenantId"/> está en el conjunto de la vista en <paramref name="ahora"/>. Es la re-comprobación por
    /// Tenant (§ 4.3), con el mismo predicado que <see cref="ConjuntoAsync"/>, y la única puerta de
    /// <see cref="AmbitoTenantAutorizado"/>.
    /// </summary>
    public static async Task<bool> AutorizaAsync(
        IdentidadVistaUnificada identidad, IOperacionesQueryContext operaciones, ITenantsQueryContext tenants,
        Guid tenantId, DateTime ahora, CancellationToken cancellationToken) =>
        tenantId == identidad.TenantOrigenId
            ? await TenantsBeneficiariosAutorizados.OrigenGestionadoAsync(
                operaciones, identidad.UsuarioId, identidad.TenantOrigenId, ahora, cancellationToken)
            : await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
                operaciones, tenants, identidad.UsuarioId, identidad.TenantOrigenId, tenantId, identidad.RolDeOrigen,
                ahora, cancellationToken) is not null;
}

/// <summary>
/// El ámbito de un Tenant de la vista unificada de Comunicaciones (D-6 b, § 4.6), ya autorizado por la re-comprobación
/// del predicado de la vista (<see cref="PredicadoVistaUnificada.AutorizaAsync"/>, con un <c>ahora</c> nuevo). Abre
/// <see cref="AmbitoTenantExplicito"/> con la marca <see cref="AmbitoSinViaHeredada"/>, así que la lectura de dentro corre
/// con el contexto RLS firmado de ese Tenant, con <c>cae_app_runtime</c> y la política <c>aislamiento_tenant</c> de
/// siempre: ninguna política ni identidad nueva (§ 3, alternativa (a)).
///
/// <para>
/// Contrato (M3):
/// <list type="bullet">
/// <item><c>sealed class</c>, sin constructor accesible desde fuera del tipo y sin miembros <c>set</c> ni <c>init</c>.
/// Su única fábrica es <see cref="EjecutarAsync{T}"/>, que re-comprueba el predicado antes de abrirlo.</item>
/// <item>De un solo uso: <see cref="EjecutarAsync{T}"/> lo abre, entrega la instancia a la lectura y lo cierra al
/// terminar, también si la lectura lanza. Una instancia cerrada no se reabre y <see cref="ExigirAbierto"/> lanza con
/// ella: cada página, detalle, adjunto o contador crea la suya.</item>
/// <item>Nunca se serializa ni se guarda en el estado de un componente: no es <c>[Serializable]</c> ni tiene
/// constructor sin parámetros.</item>
/// </list>
/// La forma de un callback no es estética: un <see cref="AsyncLocal{T}"/> fijado dentro de un método <c>async</c> no
/// vuelve a quien lo llamó, así que el ámbito tiene que abrirse y cerrarse en el mismo marco que la lectura.
/// </para>
/// </summary>
public sealed class AmbitoTenantAutorizado : IDisposable
{
    private IDisposable? _tenant;
    private IDisposable? _marca;

    private AmbitoTenantAutorizado(Guid tenantId)
    {
        TenantId = tenantId;
        _marca = AmbitoSinViaHeredada.Marcar();
        _tenant = AmbitoTenantExplicito.Establecer(tenantId);
    }

    /// <summary>El Tenant propietario cuyos datos lee este ámbito.</summary>
    public Guid TenantId { get; }

    /// <summary>
    /// Re-comprueba <paramref name="tenantId"/> con el predicado de la vista y un <c>ahora</c> nuevo; si sigue dentro, abre
    /// el ámbito, ejecuta <paramref name="lectura"/> en él y lo cierra. Si ya no está, no abre nada ni ejecuta la lectura:
    /// <c>Autorizado</c> es falso.
    /// </summary>
    public static async Task<(bool Autorizado, T? Valor)> EjecutarAsync<T>(
        IdentidadVistaUnificada identidad, IOperacionesQueryContext operaciones, ITenantsQueryContext tenants,
        Guid tenantId, Func<AmbitoTenantAutorizado, Task<T>> lectura, CancellationToken cancellationToken)
    {
        if (!await PredicadoVistaUnificada.AutorizaAsync(
                identidad, operaciones, tenants, tenantId, DateTime.UtcNow, cancellationToken))
            return (false, default);

        using var ambito = new AmbitoTenantAutorizado(tenantId);
        return (true, await lectura(ambito));
    }

    /// <summary>
    /// Lanza si el ámbito ya se cerró o si la lectura no corre dentro de él (otro ámbito explícito encima, o la marca
    /// perdida). Cada lectura que lo recibe lo llama antes de tocar la base.
    /// </summary>
    public void ExigirAbierto()
    {
        ObjectDisposedException.ThrowIf(_tenant is null, this);
        if (AmbitoTenantExplicito.TenantIdActual != TenantId || !AmbitoSinViaHeredada.Activo)
            throw new InvalidOperationException("La lectura de la vista unificada no corre dentro de su ámbito autorizado.");
    }

    public void Dispose()
    {
        if (_tenant is null) return;
        _tenant.Dispose();
        _marca!.Dispose();
        _tenant = null;
        _marca = null;
    }
}
