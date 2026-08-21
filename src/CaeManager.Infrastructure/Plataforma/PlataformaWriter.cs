using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Persistence;

namespace CaeManager.Infrastructure.Plataforma;

/// <inheritdoc cref="IPlataformaWriter" />
/// <remarks>
/// Escribe en el mismo <c>DbContext</c> scoped que el comando que lo invoca, así
/// que la sesión entra en el <c>SaveChanges</c> de ese comando y no hace falta
/// transacción explícita — mismo patrón que <c>AsignacionesOperativasWriter</c>.
/// </remarks>
public class PlataformaWriter(CaeManagerDbContext dbContext) : IPlataformaWriter
{
    public void AnadirSesion(SesionPrivilegiada sesion)
    {
        ArgumentNullException.ThrowIfNull(sesion);
        dbContext.SesionesPrivilegiadas.Add(sesion);
    }

    public void AnadirConcesion(ConcesionPrivilegio concesion)
    {
        ArgumentNullException.ThrowIfNull(concesion);
        // Las filas de alcance cuelgan del agregado y viajan con él: la
        // concesión y los tenants que cubre entran en el mismo SaveChanges, así
        // que no puede quedar una concesión sin alcance ni al revés.
        dbContext.ConcesionesPrivilegio.Add(concesion);
    }
}
