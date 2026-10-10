using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;

namespace CaeManager.IntegrationTests;

/// <summary>
/// La <see cref="PoliticaNotaInterna"/> real para un rol de negocio dado, sin Sesión Privilegiada. Las pruebas de
/// integración que no miden la nota usan el rol de equipo, que reproduce lo que veían antes del corte.
/// </summary>
public static class PoliticaNotaInternaPruebas
{
    public static IPoliticaNotaInterna Con(string? rol) =>
        new PoliticaNotaInterna(new CurrentUserServiceFalso(rol: rol), new SinSesion(), new TenantFijo(Guid.NewGuid()));

    private sealed class SinSesion : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult<SesionPrivilegiadaActiva?>(null);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class TenantFijo(Guid tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }
}
