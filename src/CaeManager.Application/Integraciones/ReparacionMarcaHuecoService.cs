using CaeManager.Application.Common;
using CaeManager.Domain.Integraciones;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Repara la marca del hueco de una desconexión cuyo último guardado no llegó a la base: registra
/// <see cref="ConexionIntegracion.MarcaHuecoPorRegistrarDesdeUtc"/> en <see cref="ConexionIntegracion.HuecoPendienteDesdeUtc"/>
/// (<see cref="ConexionIntegracion.RegistrarMarcaHuecoPorRegistrar"/>). Todo sale de lo guardado en la conexión, no de lo
/// que quedara en memoria ni de una notificación posterior (Codex, ronda 18). Solo escribe la marca y el valor por
/// registrar: nunca el estado, la reclamación ni la suscripción. Tres llamadores:
/// <list type="bullet">
/// <item>Desconectar repetido sobre la conexión Deshabilitada, ya dentro de <see cref="ICerrojoVerificacionBuzon"/>
/// (<see cref="RepararBajoCerrojoAsync"/>).</item>
/// <item>La acción Reparar de Conexiones (<c>RepararMarcaHuecoCommandHandler</c>), también dentro del cerrojo y solo si
/// la conexión releída sigue Deshabilitada (Codex, ronda 19).</item>
/// <item>El ciclo diario de suscripciones (<c>RenovacionSuscripcionWebhookHostedService</c>), por Tenant propietario
/// activo y con su ámbito explícito, que toma el mismo cerrojo por conexión (<see cref="RepararDelTenantActualAsync"/>).</item>
/// </list>
/// </summary>
public class ReparacionMarcaHuecoService(
    IConexionIntegracionRepository conexionRepositorio,
    IUnitOfWork unitOfWork,
    ICerrojoVerificacionBuzon cerrojo,
    IDescarteCambiosPendientes descarteCambios,
    ILogger<ReparacionMarcaHuecoService> logger)
{
    /// <summary>Intentos de la reparación de una conexión (lectura de la base y guardado), contando el primero.</summary>
    public const int IntentosGuardado = 3;

    /// <summary>
    /// Ciclo diario: cada conexión del Tenant actual con algo por registrar, aislada de las demás. Con el cerrojo ocupado
    /// (Desconectar, Reactivar o la verificación trabajan en ella) se deja para el ciclo siguiente.
    /// </summary>
    public async Task RepararDelTenantActualAsync(CancellationToken cancellationToken)
    {
        var conexiones = await conexionRepositorio.ObtenerIdsConMarcaHuecoPorRegistrarAsync(cancellationToken);
        foreach (var conexionId in conexiones)
        {
            try
            {
                var ejecutado = await cerrojo.IntentarEjecutarAsync(conexionId, async _ =>
                {
                    if (!await RepararBajoCerrojoAsync(conexionId))
                        logger.LogError("La marca de hueco por registrar de {ConexionId} sigue sin registrar; se reintenta en el ciclo siguiente.", conexionId);
                }, cancellationToken);

                if (!ejecutado)
                    logger.LogInformation("La conexión {ConexionId} está ocupada; su marca de hueco se repara en el ciclo siguiente.", conexionId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "No se pudo reparar la marca de hueco de {ConexionId}; se sigue con las demás.", conexionId);
                descarteCambios.DescartarCambiosPendientes();
            }
        }
    }

    /// <summary>
    /// Con el cerrojo de la conexión ya tomado. Cada intento relee la conexión de la base y guarda sin la cancelación del
    /// llamador; un fallo (colisión con la ingesta, base caída) descarta lo rastreado y vuelve a empezar desde la base.
    /// </summary>
    /// <returns>true si no queda nada por registrar en la base (reparada, sin nada que reparar o ya no existe); false si,
    /// agotados los intentos, sigue puesta.</returns>
    public async Task<bool> RepararBajoCerrojoAsync(Guid conexionId)
    {
        for (var intento = 1; intento <= IntentosGuardado; intento++)
        {
            try
            {
                var conexion = await conexionRepositorio.ObtenerActualPorIdAsync(conexionId, CancellationToken.None);
                if (conexion is null || !conexion.RegistrarMarcaHuecoPorRegistrar())
                    return true;

                await unitOfWork.SaveChangesAsync(CancellationToken.None);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo registrar la marca de hueco por registrar de {ConexionId} (intento {Intento}).", conexionId, intento);
                descarteCambios.DescartarCambiosPendientes();
            }
        }

        return false;
    }
}
