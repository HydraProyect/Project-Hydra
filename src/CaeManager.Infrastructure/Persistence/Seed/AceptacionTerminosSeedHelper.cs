using CaeManager.Domain.Cumplimiento;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Usuarios sembrados (admin inicial fuera de producción, cartera de
/// DatosPrueba, segundo tenant de verificación E2E) representan cuentas ya
/// "onboarded" a efectos de este mecanismo, no altas reales — sin esto, el
/// modal bloqueante de AceptacionTerminosGate intercepta cada login de los
/// tests E2E y de las demos, que no interactúan con él a propósito.
/// </summary>
internal static class AceptacionTerminosSeedHelper
{
    public static async Task AceptarParaUsuarioDeSemillaAsync(
        CaeManagerDbContext dbContext, Guid usuarioId, CancellationToken cancellationToken)
    {
        dbContext.AceptacionesTerminos.Add(new AceptacionTerminos(usuarioId, VersionTerminos.Actual, DateTime.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
