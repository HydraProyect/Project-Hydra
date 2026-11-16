using CaeManager.Application.CapacidadesTenant;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.DerechosInteresado;
using CaeManager.Domain.Tenants;

namespace CaeManager.Application.DerechosInteresado.Commands;

/// <summary>
/// El recorrido común de los cinco pasos de tramitación (verificar la identidad, iniciar la tramitación,
/// prorrogar, resolver y denegar): primero la capacidad «Derechos del interesado» en el Tenant actual
/// (<see cref="IAutoridadCapacidadesTenant"/>, decisión D-3 del 2026-11-15), luego la solicitud del Tenant actual,
/// luego el paso de dominio con el actor real y, si el dominio lo rechaza, un error en vez de una excepción.
/// Cada paso queda auditado con su propia acción por el interceptor.
/// </summary>
internal static class PasoSolicitudDerechoInteresado
{
    public static async Task<Result> DarAsync(
        IAutoridadCapacidadesTenant autoridad,
        IActorAuditoria actorAuditoria,
        ISolicitudDerechoInteresadoRepository solicitudes,
        IUnitOfWork unitOfWork,
        TimeProvider reloj,
        Guid solicitudId,
        Action<SolicitudDerechoInteresado, Guid, DateTime> paso,
        CancellationToken cancellationToken)
    {
        if (!await autoridad.TieneCapacidadAsync(CapacidadTenant.DerechosDelInteresado, cancellationToken)
            || (await actorAuditoria.ObtenerAsync()).ActorRealUsuarioId is not { } actorRealId)
            return Result.Fallo(ErroresDerechosInteresado.SinCapacidad);

        var solicitud = await solicitudes.ObtenerPorIdAsync(solicitudId, cancellationToken);
        if (solicitud is null)
            return Result.Fallo(ErroresDerechosInteresado.NoEncontrada);

        try
        {
            paso(solicitud, actorRealId, reloj.GetUtcNow().UtcDateTime);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Fallo(ErroresDerechosInteresado.PasoNoPermitido(ex.Message));
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(ErroresDerechosInteresado.DatosNoValidos(ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
