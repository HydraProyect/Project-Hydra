using CaeManager.Application.Common;
using CaeManager.Application.Exportacion;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Exportacion;

/// <summary>
/// <see cref="ICandadoExportacionCompleta"/> sobre <c>pg_advisory_xact_lock</c> con el espacio de clave
/// <see cref="Espacio"/> + TenantId, el mismo patrón que <c>CandadoEmpresaPropia</c>. El candado se suelta con el
/// COMMIT o el ROLLBACK de la transacción del Command: dos solicitudes del mismo Tenant a la vez se ejecutan una tras
/// otra, y la segunda ve la fila que confirmó la primera.
/// </summary>
public class CandadoExportacionCompleta(CaeManagerDbContext dbContext, ITransaccionDeComando transaccion, ITenantActual tenantActual)
    : ICandadoExportacionCompleta
{
    public const string Espacio = "app.exportacion_completa:";

    public Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.CurrentTransaction is not null)
            return BloquearYEjecutarAsync(operacion, cancellationToken);

        // Sin reintento: la solicitud y su auditoría quedan rastreadas, y un intento fallido lo descarta todo.
        var intentos = 0;
        return transaccion.EjecutarAsync(ct =>
        {
            if (++intentos > 1)
                throw new InvalidOperationException(
                    "La solicitud de exportación completa no se reintenta: el intento anterior descartó lo rastreado.");
            return BloquearYEjecutarAsync(operacion, ct);
        }, cancellationToken);
    }

    private async Task<Result> BloquearYEjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken)
    {
        // El Tenant de la sesión, el mismo que fija la RLS de la conexión y el que cuenta el límite.
        var tenantId = tenantActual.TenantId
                       ?? throw new InvalidOperationException("La solicitud de exportación completa exige un Tenant en curso.");

        var clave = Espacio + tenantId;
        await dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({clave}, 0))", cancellationToken);

        return await operacion(cancellationToken);
    }
}
