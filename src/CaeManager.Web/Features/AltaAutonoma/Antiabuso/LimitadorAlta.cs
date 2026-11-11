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
public sealed class PoliticaLimitadorAlta(IOptions<OpcionesAntiabusoAlta> opciones, CupoDiarioAlta cupoDiario) : IRateLimiterPolicy<string>
{
    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = Rechazar;

    public RateLimitPartition<string> GetPartition(HttpContext httpContext) =>
        LimitadorAlta.Particion(httpContext, opciones.Value, cupoDiario);

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
/// La IP es la del proxy de borde ya resuelta. La ventana por hora se cuenta por IPv4 o por prefijo IPv6 /64
/// (<see cref="LimitadorApiPublica"/>) y la diaria por IPv4 o por prefijo /48 (<see cref="PrefijoDiario"/>), en un
/// cupo que comparten todos los /64 de ese /48 (<see cref="CupoDiarioAlta"/>): con una partición diaria por cada /64,
/// quien dispone de un /48 tendría 65.536 cupos de día. Los contadores viven en memoria por instancia y se reinician
/// con cada despliegue; con dos ranuras sirviendo a la vez, cada una lleva su cupo. El 429 sale sin cuerpo ni
/// cabeceras que digan qué ventana se agotó.
/// </para>
/// </summary>
public static class LimitadorAlta
{
    public const string Politica = "alta";

    public static RateLimitPartition<string> Particion(HttpContext contexto, OpcionesAntiabusoAlta opciones, CupoDiarioAlta cupoDiario) =>
        Particion(contexto, opciones, cupoDiario, TimeSpan.FromHours(1));

    /// <summary>
    /// La misma partición con la ventana corta a medida. Solo la usan las pruebas, para observar con ventanas de
    /// milisegundos que el ciclo del limitador particionado la repone; la diaria la fija <see cref="CupoDiarioAlta"/>.
    /// </summary>
    public static RateLimitPartition<string> Particion(
        HttpContext contexto, OpcionesAntiabusoAlta opciones, CupoDiarioAlta cupoDiario, TimeSpan ventanaCorta)
    {
        if (!HttpMethods.IsPost(contexto.Request.Method))
            return RateLimitPartition.GetNoLimiter("alta-sin-limite");

        var porHora = opciones.IniciosPorHoraPorIp;
        var prefijoDiario = PrefijoDiario(contexto);

        return RateLimitPartition.Get(
            $"alta:{porHora}:{LimitadorApiPublica.IpDelCliente(contexto)}",
            _ => new LimitadorEncadenado(
                VentanaFija(porHora, ventanaCorta),
                new VentanaCompartida(cupoDiario.Limitador, prefijoDiario)));
    }

    /// <summary>
    /// Techo del limitador global para el POST anónimo de <c>/alta/verificar</c>, o <c>null</c> si la petición no lo es.
    /// Cada confirmación abre conexión con contexto firmado y bloquea una fila (<c>app_alta_verificar_correo</c>): adivinar
    /// el token (256 bits) no es el riesgo, martillear la base sí. Misma IP del cliente que la ventana por hora del alta
    /// (<see cref="LimitadorApiPublica.IpDelCliente"/>): una IPv6 cuenta por su /64 y una IPv4 mapeada como la IPv4, para
    /// que rotar los bits bajos de un /64 no dé un techo nuevo por dirección.
    /// </summary>
    public static RateLimitPartition<string>? ParticionVerificar(HttpContext contexto, int permisosPorMinuto)
    {
        if (!HttpMethods.IsPost(contexto.Request.Method) || !contexto.Request.Path.StartsWithSegments("/alta/verificar"))
            return null;

        return RateLimitPartition.GetFixedWindowLimiter(
            $"alta-verificar:{LimitadorApiPublica.IpDelCliente(contexto)}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permisosPorMinuto,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    }

    /// <summary>
    /// Techo del limitador global para <c>/alta/organizacion</c> (GET y POST), o <c>null</c> si la petición no va ahí.
    /// La página consulta la base con contexto firmado al pintarse y al enviarse (<c>app_alta_consultar_continuacion</c>,
    /// <c>app_alta_completar_datos_organizacion</c>), así que el GET también lleva techo. Va en su propia partición por
    /// IP, y no bajo la política <c>alta</c>: esa cuenta los inicios del alta, y si el paso Empresa gastara del mismo cupo,
    /// quien comparte IP (una oficina tras NAT) se quedaría sin poder iniciar el alta, o perdería lo escrito con un 429.
    /// El antiabuso propio del paso es el cupo por CIF (<see cref="CupoConsultasCifAlta"/>) y el credencial de 256 bits.
    /// </summary>
    public static RateLimitPartition<string>? ParticionOrganizacion(HttpContext contexto, int permisosPorMinuto)
    {
        if (!contexto.Request.Path.StartsWithSegments("/alta/organizacion"))
            return null;

        return RateLimitPartition.GetFixedWindowLimiter(
            $"alta-organizacion:{LimitadorApiPublica.IpDelCliente(contexto)}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permisosPorMinuto,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    }

    /// <summary>
    /// IP del cliente para el cupo diario: una IPv4 tal cual (también la mapeada en IPv6) y una IPv6 por su prefijo /48,
    /// lo que un proveedor suele asignar a un solo cliente.
    /// </summary>
    public static string PrefijoDiario(HttpContext contexto)
    {
        var ip = contexto.Connection.RemoteIpAddress;
        if (ip is null)
            return "desconocida";

        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            return ip.ToString();

        Span<byte> octetos = stackalloc byte[16];
        ip.TryWriteBytes(octetos, out _);
        octetos[6..].Clear();
        return $"{new System.Net.IPAddress(octetos)}/48";
    }

    // Sin reposición automática: cada FixedWindowRateLimiter automático arma su propio temporizador, uno por IP y por
    // ventana durante hasta un día. Así los repone el temporizador único del limitador particionado (TryReplenish).
    internal static FixedWindowRateLimiter VentanaFija(int permisos, TimeSpan ventana) => new(new FixedWindowRateLimiterOptions
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

/// <summary>
/// Cupo diario de la política <c>alta</c>, compartido por todas las particiones por hora: una ventana fija de un día por
/// cada IPv4 o prefijo IPv6 /48 (<see cref="LimitadorAlta.PrefijoDiario"/>). Singleton; su limitador particionado repone
/// las ventanas en su propio ciclo y retira las que llevan tiempo enteras.
/// </summary>
public sealed class CupoDiarioAlta : IDisposable
{
    public CupoDiarioAlta(IOptions<OpcionesAntiabusoAlta> opciones)
        : this(opciones.Value.IniciosPorDiaPorIp, TimeSpan.FromDays(1))
    {
    }

    /// <summary>Con la ventana a medida: solo para las pruebas.</summary>
    public CupoDiarioAlta(int permisosPorDia, TimeSpan ventana) =>
        Limitador = PartitionedRateLimiter.Create<string, string>(clave =>
            RateLimitPartition.Get(clave, _ => LimitadorAlta.VentanaFija(permisosPorDia, ventana)));

    public PartitionedRateLimiter<string> Limitador { get; }

    public void Dispose() => Limitador.Dispose();
}

/// <summary>
/// Ventana de <see cref="CupoDiarioAlta"/> vista desde una partición por hora: pide y consulta el permiso en el cupo
/// compartido con su clave. No guarda estado propio, así que retirar la partición por hora no devuelve cupo de día, y
/// liberarla no libera el cupo compartido.
/// </summary>
internal sealed class VentanaCompartida(PartitionedRateLimiter<string> compartido, string clave) : ReplenishingRateLimiter
{
    // La repone el ciclo del limitador compartido, no el de la partición por hora.
    public override bool IsAutoReplenishing => true;

    public override TimeSpan ReplenishmentPeriod => TimeSpan.MaxValue;

    public override bool TryReplenish() => false;

    // Siempre «inactiva»: la inactividad de la partición por hora la decide solo su propia ventana.
    public override TimeSpan? IdleDuration => TimeSpan.MaxValue;

    public override RateLimiterStatistics? GetStatistics() => compartido.GetStatistics(clave);

    protected override RateLimitLease AttemptAcquireCore(int permitCount) => compartido.AttemptAcquire(clave, permitCount);

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
        compartido.AcquireAsync(clave, permitCount, cancellationToken);
}
