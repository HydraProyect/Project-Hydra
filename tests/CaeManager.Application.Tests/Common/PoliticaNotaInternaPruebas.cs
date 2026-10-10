using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Monta la <see cref="PoliticaNotaInterna"/> <b>real</b> sobre un usuario (rol de negocio, o <c>null</c> sin rol), una
/// sesión privilegiada opcional y un Tenant actual. Los tests de las fichas usan esto y no un resultado fijo: así miden
/// la regla que de verdad decide.
/// </summary>
public static class PoliticaNotaInternaPruebas
{
    public static readonly Guid TenantPorDefecto = Guid.Parse("7c1f0a52-3e8b-4d7a-9b61-2f4e8a9c0d11");

    public static IPoliticaNotaInterna Con(string? rol, SesionPrivilegiadaActiva? sesion = null, Guid? tenantActual = null) =>
        new PoliticaNotaInterna(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol),
            new SesionFija(sesion),
            new TenantFijo(tenantActual ?? TenantPorDefecto));

    /// <summary>Sesión privilegiada vigente sobre <paramref name="tenant"/> con la capacidad dada.</summary>
    public static SesionPrivilegiadaActiva Sesion(CapacidadPrivilegio capacidad, Guid? tenant = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), tenant ?? TenantPorDefecto, capacidad, null);

    private sealed class SesionFija(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
    }

    private sealed class TenantFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }
}
