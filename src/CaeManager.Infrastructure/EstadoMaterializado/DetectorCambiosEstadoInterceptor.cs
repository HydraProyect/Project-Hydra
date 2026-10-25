using System.Runtime.CompilerServices;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <summary>
/// Detector de cambios del estado materializado (diseño § 4.3). Va en todo <see cref="CaeManagerDbContext"/> como
/// interceptor, así que ningún guardado por el <c>ChangeTracker</c> se lo salta, venga de un comando, de un trabajo de fondo
/// o de un servicio. Recoge las raíces en <c>SavingChanges</c>, después del sellado de Tenant (va el último de los
/// interceptores), con <see cref="InventarioDisparadoresEstado"/>; las da por buenas en <c>SavedChanges</c> (un guardado que
/// falla las descarta) y recalcula:
/// <list type="bullet">
/// <item>Dentro de una transacción explícita (<c>ITransaccionDeComando</c>, <c>ITransaccionDocumentoBloqueado</c>): se
/// acumulan y la envoltura llama a <see cref="ProcesarAntesDeConfirmarAsync"/> antes del <c>COMMIT</c>, dentro de un
/// <c>SAVEPOINT</c>.</item>
/// <item>Sin transacción (el guardado suelto, con su transacción implícita ya confirmada): en el propio
/// <c>SavedChangesAsync</c>, en una transacción nueva. Nunca lanza: un fallo va a la cola y al registro de errores.</item>
/// </list>
///
/// <para>
/// Un guardado síncrono (<c>SaveChanges</c>) también recoge, pero no recalcula: lo acumulado se recalcula en el siguiente
/// guardado asíncrono o confirmación de ese contexto (el recálculo es idempotente desde lo que hay en la base).
/// </para>
/// </summary>
public sealed class DetectorCambiosEstadoInterceptor(IServiceProvider servicios) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, Estado> _porContexto = new();

    private sealed class Estado
    {
        public CambiosEstadoDetectados Acumulados { get; } = new();
        public CambiosEstadoDetectados? DelGuardado { get; set; }
        public bool Procesando { get; set; }
    }

    /// <summary>El detector de ese contexto, si lo lleva (un contexto montado a mano en un test no lo lleva).</summary>
    public static DetectorCambiosEstadoInterceptor? De(DbContext contexto) =>
        contexto.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors?
            .OfType<DetectorCambiosEstadoInterceptor>().FirstOrDefault();

    /// <summary>
    /// Recalcula lo acumulado en la transacción explícita abierta del contexto, antes de confirmarla. Lo llaman las
    /// envolturas de transacción; nunca lanza por un fallo del recálculo (solo si la conexión ya no sirve, y entonces el
    /// <c>COMMIT</c> fallaría igual).
    /// </summary>
    public Task ProcesarAntesDeConfirmarAsync(DbContext contexto)
    {
        var estado = _porContexto.GetOrCreateValue(contexto);
        if (estado.Acumulados.EstaVacio || estado.Procesando)
            return Task.CompletedTask;
        return ConEstadoAsync(contexto, estado, r => r.ProcesarEnTransaccionAsync(estado.Acumulados.Tomar()));
    }

    /// <summary>La transacción explícita se deshizo: lo acumulado no llegó a la base.</summary>
    public void Descartar(DbContext contexto)
    {
        if (_porContexto.TryGetValue(contexto, out var estado))
        {
            estado.Acumulados.Descartar();
            estado.DelGuardado = null;
        }
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Recoger(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Recoger(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Consolidar(eventData.Context);
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (Consolidar(eventData.Context) is { } estado
            && eventData.Context is { } contexto
            && contexto.Database.CurrentTransaction is null
            && System.Transactions.Transaction.Current is null
            && !estado.Procesando
            && !estado.Acumulados.EstaVacio)
            await ConEstadoAsync(contexto, estado, r => r.ProcesarFueraDeTransaccionAsync(estado.Acumulados.Tomar()));

        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => DescartarGuardado(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        DescartarGuardado(eventData.Context);
        return Task.CompletedTask;
    }

    private void Recoger(DbContext? contexto)
    {
        if (contexto is not CaeManagerDbContext)
            return;

        var estado = _porContexto.GetOrCreateValue(contexto);
        var delGuardado = new CambiosEstadoDetectados();
        try
        {
            foreach (var entrada in contexto.ChangeTracker.Entries())
                InventarioDisparadoresEstado.Recoger(entrada, delGuardado);
        }
        catch (Exception ex)
        {
            // Un defecto del inventario no puede tumbar el guardado: el Tenant de cada entrada del cálculo va entero a la cola.
            delGuardado = new CambiosEstadoDetectados();
            foreach (var entrada in contexto.ChangeTracker.Entries())
                if (entrada.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                    && entrada.Entity is Domain.Common.EntidadConTenant e
                    && InventarioDisparadoresEstado.Entradas.ContainsKey(entrada.Metadata.ClrType))
                    delGuardado.De(e.TenantId).TenantEntero = "Fallo del detector: " + ex.GetType().Name;
        }

        if (AmbitoSinRecalculoEstado.Activo)
        {
            // Siembra, retirada u operación masiva: nada en línea; el Tenant entero a la cola, o nada si se retira.
            var motivo = "Ámbito sin recálculo: " + AmbitoSinRecalculoEstado.Motivo;
            var encolar = AmbitoSinRecalculoEstado.EncolaAlGuardar;
            var soloTenants = new CambiosEstadoDetectados();
            if (encolar)
                foreach (var tenant in delGuardado.PorTenant.Keys)
                    soloTenants.De(tenant).TenantEntero = motivo;
            // Quien abrió el ámbito los encola también al cerrarlo: este guardado puede no llegar a recalcularse (una
            // transacción explícita que no pasa por la envoltura) y otros cambios del mismo Tenant no pasan por aquí.
            AmbitoSinRecalculoEstado.AnotarTenants(delGuardado.PorTenant.Keys);
            delGuardado = soloTenants;
        }

        estado.DelGuardado = delGuardado;
    }

    private Estado? Consolidar(DbContext? contexto)
    {
        if (contexto is null || !_porContexto.TryGetValue(contexto, out var estado))
            return null;
        if (estado.DelGuardado is { } delGuardado)
            estado.Acumulados.Sumar(delGuardado);
        estado.DelGuardado = null;
        return estado;
    }

    private void DescartarGuardado(DbContext? contexto)
    {
        if (contexto is not null && _porContexto.TryGetValue(contexto, out var estado))
            estado.DelGuardado = null;
    }

    private async Task ConEstadoAsync(DbContext contexto, Estado estado, Func<RecalculoEstadoEnLinea, Task> accion)
    {
        // Sin el servicio registrado (un contenedor de test a medida) no hay recálculo; lo acumulado se queda.
        if (servicios.GetService<RecalculoEstadoEnLinea>() is not { } recalculo)
            return;
        // El servicio de recálculo lee y escribe con el contexto del ámbito de DI. Si el guardado vino de otro contexto (uno
        // creado a mano), su transacción no es la de ese servicio: no se recalcula aquí (lo repone el cotejo, diseño § 7.2).
        if (!ReferenceEquals(recalculo.Contexto, contexto))
        {
            // Se descarta (no se arrastra a cada guardado siguiente de ese contexto): lo repone el cotejo nocturno (CambioDeDiaEstadoMaterializado).
            estado.Acumulados.Descartar();
            recalculo.RegistrarContextoAjeno();
            return;
        }

        estado.Procesando = true;
        try
        {
            await accion(recalculo);
        }
        finally
        {
            estado.Procesando = false;
        }
    }
}
