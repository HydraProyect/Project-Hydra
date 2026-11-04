using System.Threading.RateLimiting;
using CaeManager.Web.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.AltaAutonoma.Antiabuso;

/// <summary>
/// Política <c>alta</c> tal como se registra en <c>AddRateLimiter</c>: reparte con <see cref="LimitadorAlta.Particion"/>
/// y tiene su propio rechazo. El 429 tiene que salir vacío, y <c>UseStatusCodePagesWithReExecute("/not-found")</c>, que
/// va antes del limitador, lo reejecutaría hacia la página de «no encontrado»: un POST de formulario acabaría en HTML de
/// esa página, o en 400 si el reenvío choca con la antifalsificación. El rechazo apaga las páginas de estado para esta
/// respuesta, como hace el presupuesto de memoria con su 503.
/// </summary>
public sealed class PoliticaLimitadorAlta(IOptions<OpcionesAntiabusoAlta> opciones) : IRateLimiterPolicy<string>
{
    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = Rechazar;

    public RateLimitPartition<string> GetPartition(HttpContext httpContext) =>
        LimitadorAlta.Particion(httpContext, opciones.Value);

    private static ValueTask Rechazar(OnRejectedContext contexto, CancellationToken _)
    {
        if (contexto.HttpContext.Features.Get<IStatusCodePagesFeature>() is { } paginasEstado)
            paginasEstado.Enabled = false;

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Política de limitación <c>alta</c> (ASVS 4.0.3 V2.2.1 y V11.1.4): reparte por IP los inicios del alta autónoma con
/// dos ventanas fijas encadenadas, una por hora y otra por día. Solo cuenta los POST, igual que el límite de
/// <c>/cuenta</c>: cargar la página no es iniciar el alta, y castigarlo deja sin formulario a quien comparte IP.
///
/// <para>
/// La política se aplica con <c>RequireRateLimiting(LimitadorAlta.Politica)</c> sobre un endpoint HTTP. Un envío que
/// viaje por el circuito de Blazor interactivo no pasa por este middleware, así que la página del alta tiene que
/// enviar por HTTP (formulario SSR estático o endpoint mínimo) para que el límite actúe.
/// </para>
///
/// <para>
/// La IP es la del proxy de borde ya resuelta, agrupando IPv6 por /64 (<see cref="LimitadorApiPublica"/>). Los
/// contadores viven en memoria por instancia y se reinician con cada despliegue; con dos ranuras sirviendo a la vez,
/// cada una lleva su cupo. El 429 sale sin cuerpo ni cabeceras que digan qué ventana se agotó.
/// </para>
/// </summary>
public static class LimitadorAlta
{
    public const string Politica = "alta";

    public static RateLimitPartition<string> Particion(HttpContext contexto, OpcionesAntiabusoAlta opciones) =>
        Particion(contexto, opciones, TimeSpan.FromHours(1), TimeSpan.FromDays(1));

    /// <summary>
    /// La misma partición con ventanas a medida. Solo la usan las pruebas, para observar con ventanas de milisegundos
    /// que el ciclo del limitador particionado repone las dos (el proyecto no abre sus internos a las pruebas).
    /// </summary>
    public static RateLimitPartition<string> Particion(
        HttpContext contexto, OpcionesAntiabusoAlta opciones, TimeSpan ventanaCorta, TimeSpan ventanaLarga)
    {
        if (!HttpMethods.IsPost(contexto.Request.Method))
            return RateLimitPartition.GetNoLimiter("alta-sin-limite");

        var porHora = opciones.IniciosPorHoraPorIp;
        var porDia = opciones.IniciosPorDiaPorIp;

        return RateLimitPartition.Get(
            $"alta:{porHora}:{porDia}:{LimitadorApiPublica.IpDelCliente(contexto)}",
            _ => new LimitadorEncadenado(
                VentanaFija(porHora, ventanaCorta),
                VentanaFija(porDia, ventanaLarga)));
    }

    // Sin reposición automática: cada FixedWindowRateLimiter automático arma su propio temporizador, uno por IP y por
    // ventana durante hasta un día. Así los repone el temporizador único del limitador particionado (TryReplenish).
    private static FixedWindowRateLimiter VentanaFija(int permisos, TimeSpan ventana) => new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = permisos,
        Window = ventana,
        QueueLimit = 0,
        AutoReplenishment = false,
    });
}

/// <summary>
/// Dos limitadores en serie: concede solo si conceden los dos. Pregunta primero al de la ventana corta, de modo que una
/// petición rechazada por la hora no gasta cupo del día; la que pasa la hora y choca con el día sí gasta su permiso de
/// la hora, que de todos modos ya no sirve hasta que el día se reponga. Sin cola: decide en el acto.
///
/// <para>
/// Es un <see cref="ReplenishingRateLimiter"/> sin reposición automática: el limitador particionado llama a
/// <see cref="TryReplenish"/> en su propio ciclo y aquí se reenvía a los dos. Una partición solo es candidata a
/// retirarse cuando las dos ventanas están enteras (<see cref="IdleDuration"/>); antes, retirarla devolvería el cupo.
/// </para>
/// </summary>
internal sealed class LimitadorEncadenado(ReplenishingRateLimiter primero, ReplenishingRateLimiter segundo) : ReplenishingRateLimiter
{
    public override bool IsAutoReplenishing => false;

    public override TimeSpan ReplenishmentPeriod =>
        primero.ReplenishmentPeriod < segundo.ReplenishmentPeriod ? primero.ReplenishmentPeriod : segundo.ReplenishmentPeriod;

    public override bool TryReplenish()
    {
        var repuestoPrimero = primero.TryReplenish();
        var repuestoSegundo = segundo.TryReplenish();
        return repuestoPrimero || repuestoSegundo;
    }

    // El limitador particionado retira una partición cuando lleva inactiva un rato; aquí solo lo está si lo están las dos.
    public override TimeSpan? IdleDuration =>
        primero.IdleDuration is { } a && segundo.IdleDuration is { } b ? (a < b ? a : b) : null;

    public override RateLimiterStatistics? GetStatistics()
    {
        var p = primero.GetStatistics();
        var s = segundo.GetStatistics();
        if (p is null || s is null)
            return null;

        return new RateLimiterStatistics
        {
            CurrentAvailablePermits = Math.Min(p.CurrentAvailablePermits, s.CurrentAvailablePermits),
            CurrentQueuedCount = 0,
            TotalFailedLeases = p.TotalFailedLeases + s.TotalFailedLeases,
            TotalSuccessfulLeases = s.TotalSuccessfulLeases,
        };
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        var concesionPrimero = primero.AttemptAcquire(permitCount);
        if (!concesionPrimero.IsAcquired)
            return concesionPrimero;

        var concesionSegundo = segundo.AttemptAcquire(permitCount);
        if (!concesionSegundo.IsAcquired)
        {
            concesionPrimero.Dispose();
            return concesionSegundo;
        }

        return new ConcesionDoble(concesionPrimero, concesionSegundo);
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AttemptAcquireCore(permitCount));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            primero.Dispose();
            segundo.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        await primero.DisposeAsync().ConfigureAwait(false);
        await segundo.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class ConcesionDoble(RateLimitLease primera, RateLimitLease segunda) : RateLimitLease
    {
        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames => primera.MetadataNames.Union(segunda.MetadataNames);

        public override bool TryGetMetadata(string metadataName, out object? metadata) =>
            primera.TryGetMetadata(metadataName, out metadata) || segunda.TryGetMetadata(metadataName, out metadata);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                primera.Dispose();
                segunda.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
