using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;

namespace CaeManager.Web.Tests;

/// <summary>
/// Doble de <see cref="IAsignacionAutomaticaDePrincipal"/> que no asigna nada: para los arneses que
/// construyen a mano un comando de delegaciones o de cuentas y no tratan del principal de cartera.
/// </summary>
public sealed class AsignacionAutomaticaInerte : IAsignacionAutomaticaDePrincipal
{
    public Task<bool> AlAbrirOperacionAsync(AsignacionOperacion operacion, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<bool> AlPrimerElegibleAsync(Guid usuarioId, Guid operadorTenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}

/// <summary>Doble de <see cref="ITransaccionDeComando"/> para arneses sin base de datos: ejecuta la operación tal cual.</summary>
public sealed class TransaccionSinBaseDeDatos : ITransaccionDeComando
{
    public Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default) =>
        operacion(cancellationToken);
}
