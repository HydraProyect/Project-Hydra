using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence;

/// <inheritdoc cref="ISerializacionAltasAcreditacionEmpresa" />
/// <remarks>
/// Misma convención que <see cref="BloqueoEstadoOperativoTenant"/>: candado de asesoramiento de transacción con espacio
/// propio, sobre la conexión y la transacción del <see cref="CaeManagerDbContext"/> del ámbito, la misma que guarda.
/// </remarks>
public class SerializacionAltasAcreditacionEmpresa(
    CaeManagerDbContext dbContext, ITransaccionDeComando transaccion, ITenantActual tenantActual)
    : ISerializacionAltasAcreditacionEmpresa
{
    private const string Espacio = "app.altas_acreditacion_empresa:";

    public Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default)
    {
        // Un Command que ya abrió su transacción y delega aquí el alta: el candado dura lo que ella.
        if (dbContext.Database.CurrentTransaction is not null)
            return BloquearYEjecutarAsync(operacion, cancellationToken);

        // Un llamante que deja cambios pendientes para que los confirme este guardado (el adjunto que pasa a documento,
        // ActualizarDocumentoDesdeAdjuntoCommand): un intento fallido descarta todo lo rastreado, también lo suyo, y un
        // reintento confirmaría el Documento sin ese cambio. Ese reintento no se hace: falla, y el llamante lo trata como
        // cualquier otro fallo del guardado.
        var conCambiosDelLlamante = dbContext.ChangeTracker.HasChanges();
        var intentos = 0;
        return transaccion.EjecutarAsync(ct =>
        {
            if (++intentos > 1 && conCambiosDelLlamante)
                throw new InvalidOperationException(
                    "El alta no se reintenta: el intento anterior descartó cambios pendientes de quien la invocó.");
            return BloquearYEjecutarAsync(operacion, ct);
        }, cancellationToken);
    }

    private async Task<Result> BloquearYEjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken)
    {
        // El Tenant de la sesión, el mismo que fija la RLS de la conexión. Sin Tenant no hay alta que serializar.
        var tenantId = tenantActual.TenantId
                       ?? throw new InvalidOperationException("Las altas con acreditación de Documentos de Empresa exigen un Tenant en curso.");

        var clave = Espacio + tenantId;
        await dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({clave}, 0))", cancellationToken);

        return await operacion(cancellationToken);
    }
}
