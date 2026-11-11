using CaeManager.Application.Integraciones;
using CaeManager.Infrastructure.Coordinacion;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// <see cref="ICerrojoVerificacionBuzon"/> sobre <see cref="IEleccionLiderService"/>: un advisory lock de sesión de
/// PostgreSQL por conexión (<c>pg_try_advisory_lock</c> con la clave <c>verificacion-identidad-buzon:{id}</c>). Vale
/// entre réplicas y entre las dos ranuras del despliegue azul/verde porque comparten la base de datos; no necesita
/// columna ni caducidad, porque PostgreSQL lo suelta en cuanto se cierra la conexión que lo tiene, también si el
/// proceso cae a mitad de la verificación.
/// </summary>
public sealed class CerrojoVerificacionBuzonPostgres(IEleccionLiderService eleccionLider) : ICerrojoVerificacionBuzon
{
    internal static string Clave(Guid conexionId) => $"verificacion-identidad-buzon:{conexionId:D}";

    public Task<bool> IntentarEjecutarAsync(Guid conexionId, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken) =>
        eleccionLider.IntentarEjecutarComoLiderAsync(Clave(conexionId), trabajo, cancellationToken);
}