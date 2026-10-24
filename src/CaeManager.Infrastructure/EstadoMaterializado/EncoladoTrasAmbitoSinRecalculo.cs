using CaeManager.Application.Common;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <summary>
/// Al cerrar un <see cref="AmbitoSinRecalculoEstado"/> (siembras de arranque y de la CLI): encola el recálculo del Tenant
/// entero de cada Tenant tocado dentro (diseño § 4.3 y § 13.8). El detector ya encola en cada guardado que recalcula, pero
/// una siembra también escribe con <c>ExecuteUpdate</c> o en transacciones explícitas que no pasan por la envoltura del
/// recálculo, y eso el detector no lo ve: esta entrada final lo cubre. No mira la versión del Tenant: si aún no la tiene, el
/// relleno de la cola borra la entrada al rellenarlo.
///
/// <para>
/// Un Tenant propietario cada vez, con su <see cref="AmbitoTenantExplicito"/> y su <c>CreateScope()</c>: la conexión es la
/// de tráfico y la RLS de <c>RecalculosPendientes</c> solo deja escribir la entrada de ese Tenant.
/// </para>
/// </summary>
public static class EncoladoTrasAmbitoSinRecalculo
{
    /// <summary>
    /// Cierra el ámbito (si sigue abierto) y encola sus Tenants. Un fallo se registra y no tumba la siembra.
    ///
    /// <para>
    /// No es <c>async</c> a propósito: el ámbito va por <see cref="AsyncLocal{T}"/>, y cerrarlo dentro de un método
    /// <c>async</c> solo lo cerraría en el contexto de ese método; el del llamador (el arranque, del que heredan los servicios
    /// de fondo) seguiría con el ámbito abierto. Se cierra aquí, de forma síncrona, y después se encola.
    /// </para>
    /// </summary>
    public static Task CerrarYEncolarAsync(AmbitoSinRecalculoEstado.Abierto ambito, IServiceScopeFactory ambitoFactory, ILogger logger)
    {
        ambito.Dispose();
        return EncolarAsync(ambito.TenantsTocados, ambito.Motivo, ambitoFactory, logger);
    }

    private static async Task EncolarAsync(
        IReadOnlyCollection<Guid> tenants, string motivo, IServiceScopeFactory ambitoFactory, ILogger logger)
    {
        foreach (var tenantId in tenants)
        {
            try
            {
                using var _ = AmbitoTenantExplicito.Establecer(tenantId);
                using var alcance = ambitoFactory.CreateScope();
                var dbContext = alcance.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
                await ColaEstadoMaterializado.EncolarTenantEnteroAsync(dbContext, tenantId, "Ámbito sin recálculo: " + motivo);
            }
            catch (Exception ex)
            {
                // Lo repone el cotejo (diseño § 7.2); la siembra ya está confirmada.
                logger.LogError(ex, "Estado materializado: no se pudo encolar el Tenant {Tenant} al cerrar el ámbito sin recálculo.", tenantId);
            }
        }
    }
}
