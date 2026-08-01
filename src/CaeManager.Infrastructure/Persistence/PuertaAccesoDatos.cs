using CaeManager.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence;

/// <summary>
/// Da a cada operación (un despacho de MediatR, una carga directa de
/// UserManager) su propio <c>CaeManagerDbContext</c>, creado bajo demanda
/// vía <see cref="IDbContextFactory{TContext}"/> y desechado al terminar
/// (P1-11 de docs/business/MATURITY_REVIEW.md).
///
/// <b>Sustituye al semáforo que serializaba "todo acceso a datos dentro de
/// cada circuito Blazor"</b> (`SemaphoreSlim(1,1)`, la versión anterior de
/// esta clase). Ese semáforo existía porque varios componentes de Blazor
/// (layout y página) se inicializaban en paralelo compartiendo un único
/// <c>CaeManagerDbContext</c> scoped para todo el circuito, y un DbContext no
/// admite dos operaciones en vuelo — sobre SQLite la carrera nunca afloró
/// (sus operaciones "async" completan de forma síncrona); Npgsql hace I/O
/// asíncrona real y la destapa. La solución canónica para Blazor Server +
/// EF Core no es serializar el acceso a UN contexto compartido, es no
/// compartirlo: cada operación recibe el suyo, así que dos operaciones en
/// vuelo ya no compiten por nada.
///
/// Reentrante por flujo asíncrono (<see cref="AsyncLocal{T}"/>, igual que
/// <c>AmbitoTenantExplicito</c>): quien ya tiene un contexto activo en este
/// flujo lo reutiliza en vez de crear otro — necesario para que un handler
/// que despacha otro request de MediatR dentro de sí (<c>ObtenerKpisGlobalesQuery</c>,
/// que además cambia de tenant en vuelo con <c>AmbitoTenantExplicito</c>
/// entre iteraciones) siga viendo consistencia transaccional, y para que una
/// página que envuelve su carga entera en un único <c>EjecutarAsync</c> y
/// despacha varias Queries dentro comparta un solo contexto entre ellas.
///
/// La fábrica se registra <c>Scoped</c> (no <c>Singleton</c>, el valor por
/// defecto de <c>AddDbContextFactory</c>): así construye sus
/// <c>DbContextOptions</c> —y, con ellas, resuelve
/// <c>AuditoriaInterceptor</c>/<c>TenantSelladoInterceptor</c>, ambos
/// dependientes de servicios scoped como <c>ITenantActual</c>— contra el
/// scope del circuito o de la petición HTTP actual, nunca contra el
/// proveedor raíz. Resolverla como Singleton habría sido la fuga de tenant
/// más grave posible: los interceptores habrían quedado fijados al primer
/// tenant que creara un contexto, para siempre, hasta reiniciar el proceso
/// — justo la frontera de seguridad que docs/MULTITENANCY.md marca como más
/// sensible. Ver <c>InfrastructureServiceCollectionExtensions</c> para el
/// registro completo (fábrica + <c>CaeManagerDbContext</c> con reserva para
/// el puñado de accesos directos que no pasan por aquí, como
/// <c>RevalidacionClienteActivoMiddleware</c>).
/// </summary>
public sealed class PuertaAccesoDatos(IDbContextFactory<CaeManagerDbContext> fabrica) : IPuertaAccesoDatos
{
    private readonly AsyncLocal<CaeManagerDbContext?> _contextoActual = new();

    /// <summary>
    /// El contexto de la operación en curso en este flujo asíncrono, o
    /// <c>null</c> si no hay ninguno. Público por necesidad de empaquetado
    /// (lo lee el registro de DI de <c>CaeManagerDbContext</c> en
    /// <c>InfrastructureServiceCollectionExtensions</c> y los tests de
    /// integración que verifican el aislamiento entre "circuitos"), pero
    /// nada fuera de esos dos casos debería depender de esta propiedad.
    /// </summary>
    public CaeManagerDbContext? ContextoActual => _contextoActual.Value;

    public async Task<T> EjecutarAsync<T>(Func<Task<T>> operacion, CancellationToken cancellationToken = default)
    {
        if (_contextoActual.Value is not null)
            return await operacion();

        await using var contexto = await fabrica.CreateDbContextAsync(cancellationToken);
        _contextoActual.Value = contexto;
        try
        {
            return await operacion();
        }
        finally
        {
            _contextoActual.Value = null;
        }
    }

    public async Task EjecutarAsync(Func<Task> operacion, CancellationToken cancellationToken = default)
    {
        if (_contextoActual.Value is not null)
        {
            await operacion();
            return;
        }

        await using var contexto = await fabrica.CreateDbContextAsync(cancellationToken);
        _contextoActual.Value = contexto;
        try
        {
            await operacion();
        }
        finally
        {
            _contextoActual.Value = null;
        }
    }
}
