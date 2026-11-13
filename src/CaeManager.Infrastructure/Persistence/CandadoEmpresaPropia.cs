using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence;

/// <inheritdoc cref="ICandadoEmpresaPropia" />
/// <remarks>
/// Misma convención que <see cref="SerializacionAltasAcreditacionEmpresa"/>: candado de asesoramiento de transacción con
/// espacio propio, sobre la conexión y la transacción del <see cref="CaeManagerDbContext"/> del ámbito, la misma que
/// cuenta las Empresas propias y la que guarda. Dura hasta el COMMIT o el ROLLBACK; no hay que soltarlo.
/// </remarks>
public class CandadoEmpresaPropia(CaeManagerDbContext dbContext, ITransaccionDeComando transaccion, ITenantActual tenantActual)
    : ICandadoEmpresaPropia
{
    /// <summary>Espacio de clave del candado: no lo comparte con ningún otro <c>pg_advisory_xact_lock</c> del repositorio.</summary>
    public const string Espacio = "app.limite_empresa_propia:";

    public Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default)
    {
        // Un Command que ya abrió su transacción (la importación con el candado de altas, un plan de Vega): el candado
        // dura lo que ella, y confirmar o deshacer es de quien la abrió.
        if (dbContext.Database.CurrentTransaction is not null)
            return BloquearYEjecutarAsync(operacion, cancellationToken);

        // Sin reintento: los escritores dejan rastreado lo que guardan (la Empresa añadida, la restaurada, las filas de
        // una importación) y un intento fallido lo descarta todo. Repetir la operación guardaría sin ello.
        var intentos = 0;
        return transaccion.EjecutarAsync(ct =>
        {
            if (++intentos > 1)
                throw new InvalidOperationException(
                    "El guardado bajo el límite de Empresa propia no se reintenta: el intento anterior descartó lo rastreado.");
            return BloquearYEjecutarAsync(operacion, ct);
        }, cancellationToken);
    }

    private async Task<Result> BloquearYEjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken)
    {
        // El Tenant de la sesión, el mismo que fija la RLS de la conexión y el que cuenta el límite.
        var tenantId = tenantActual.TenantId
                       ?? throw new InvalidOperationException("El límite de Empresa propia exige un Tenant en curso.");

        var clave = Espacio + tenantId;
        await dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({clave}, 0))", cancellationToken);

        return await operacion(cancellationToken);
    }
}
