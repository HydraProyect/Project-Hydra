using System.Data.Common;
using System.Runtime.CompilerServices;
using CaeManager.Application.EstadoMaterializado.Frescura;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <summary>
/// Saca los avisos del estado materializado después del <c>COMMIT</c> (diseño § 4.3 y § 5.3): lo que
/// <see cref="IRegistroCambiosEstado"/> anota dentro de una transacción espera en un <see cref="BuzonAvisosEstado"/> por
/// contexto y solo se publica en <see cref="INotificadorEstadoTenant"/> si la transacción se confirma. Si se deshace, falla o
/// vuelve a un punto de guardado anterior a la anotación, se descarta: un circuito nunca oye hablar de una versión que no
/// llegó a la base.
///
/// <para>
/// Va en la lista de interceptores de <c>ConfiguracionDeContexto</c>. Es un interceptor de transacciones, no de guardado, así
/// que no compite en orden con el detector de cambios. Publicar nunca lanza: el aviso es una mejora y la acción del usuario
/// ya está confirmada.
/// </para>
/// </summary>
public sealed class AvisoEstadoTrasConfirmarInterceptor(
    INotificadorEstadoTenant notificador, ILogger<AvisoEstadoTrasConfirmarInterceptor>? logger = null) : DbTransactionInterceptor
{
    private readonly ConditionalWeakTable<DbContext, BuzonAvisosEstado> _porContexto = new();

    /// <summary>El interceptor de ese contexto, si lo lleva (un contexto montado a mano en un test no lo lleva).</summary>
    public static AvisoEstadoTrasConfirmarInterceptor? De(DbContext contexto) =>
        contexto.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors?
            .OfType<AvisoEstadoTrasConfirmarInterceptor>().FirstOrDefault();

    /// <summary>Lo pendiente de confirmar en ese contexto (diagnóstico y tests).</summary>
    public int PendientesDe(DbContext contexto) => _porContexto.TryGetValue(contexto, out var buzon) ? buzon.Pendientes : 0;

    /// <summary>
    /// Anota el aviso de una subida de versión hecha con <paramref name="contexto"/>: espera al <c>COMMIT</c> de su transacción,
    /// o sale en el acto si no hay transacción (la sentencia ya se confirmó sola).
    /// </summary>
    internal void Anotar(DbContext contexto, AvisoCambioEstado aviso)
    {
        if (contexto.Database.CurrentTransaction is null)
        {
            Publicar([aviso]);
            return;
        }

        _porContexto.GetOrCreateValue(contexto).Anotar(aviso);
    }

    // Una transacción nueva no hereda nada: lo de una anterior liberada sin COMMIT ni ROLLBACK explícitos no salió.
    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Descartar(eventData.Context);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        Descartar(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        PublicarPendientes(eventData.Context);

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        PublicarPendientes(eventData.Context);
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        Descartar(eventData.Context);

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Descartar(eventData.Context);
        return Task.CompletedTask;
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        Descartar(eventData.Context);

    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Descartar(eventData.Context);
        return Task.CompletedTask;
    }

    public override void CreatedSavepoint(DbTransaction transaction, TransactionEventData eventData) =>
        Buzon(eventData.Context)?.AbrirPunto();

    public override Task CreatedSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
    {
        Buzon(eventData.Context)?.AbrirPunto();
        return Task.CompletedTask;
    }

    public override void RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData) =>
        Buzon(eventData.Context)?.VolverAPunto();

    public override Task RolledBackToSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
    {
        Buzon(eventData.Context)?.VolverAPunto();
        return Task.CompletedTask;
    }

    public override void ReleasedSavepoint(DbTransaction transaction, TransactionEventData eventData) =>
        Buzon(eventData.Context)?.LiberarPunto();

    public override Task ReleasedSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
    {
        Buzon(eventData.Context)?.LiberarPunto();
        return Task.CompletedTask;
    }

    // Los puntos de guardado se cuentan siempre, haya o no anotaciones todavía: una anotación posterior tiene que saber
    // dentro de cuántos puntos abiertos cayó.
    private BuzonAvisosEstado? Buzon(DbContext? contexto) => contexto is null ? null : _porContexto.GetOrCreateValue(contexto);

    private void Descartar(DbContext? contexto)
    {
        if (contexto is not null && _porContexto.TryGetValue(contexto, out var buzon))
            buzon.Descartar();
    }

    private void PublicarPendientes(DbContext? contexto)
    {
        if (contexto is not null && _porContexto.TryGetValue(contexto, out var buzon))
            Publicar(buzon.Tomar());
    }

    private void Publicar(IReadOnlyList<AvisoCambioEstado> avisos)
    {
        foreach (var aviso in avisos)
        {
            try
            {
                notificador.Publicar(aviso);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Estado materializado: no se pudo avisar a los circuitos del Tenant {Tenant} (versión {Version}).",
                    aviso.TenantId, aviso.Version);
            }
        }
    }
}
