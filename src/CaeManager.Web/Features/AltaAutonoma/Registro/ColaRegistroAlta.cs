using System.Threading.Channels;
using CaeManager.Application.AltaAutonoma;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.AltaAutonoma;
using CaeManager.Web.Services;

namespace CaeManager.Web.Features.AltaAutonoma.Registro;

/// <summary>
/// Cola en segundo plano del registro del alta autónoma. La página encola y responde en el acto, con la misma pantalla
/// y el mismo tiempo exista o no una cuenta con ese correo: la búsqueda de la cuenta, la Solicitud de alta y el envío
/// los hace <see cref="ServicioRegistroAlta"/> aquí, fuera de la petición. Mismo diseño que
/// <see cref="RecuperacionContrasena"/>: cola acotada, sin esperar si está llena, y tres envíos por correo y hora
/// (§ 4.1 del plan) con <see cref="LimiteSolicitudesPorCuenta"/>: cupo propio por correo, sin colisiones prácticas,
/// y un estado acotado.
/// </summary>
public sealed class ColaRegistroAlta(
    IServiceScopeFactory scopes, TimeProvider reloj, ILogger<ColaRegistroAlta> logger) : BackgroundService
{
    private static readonly TimeSpan IntervaloAvisoColaLlena = TimeSpan.FromMinutes(1);

    private sealed record Solicitud(DatosRegistroAlta Datos, string CorreoNormalizado, Uri BasePublica);

    private readonly LimiteSolicitudesPorCuenta _limite = new(reloj);
    private readonly Channel<Solicitud> _cola = Channel.CreateBounded<Solicitud>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });
    private readonly object _cerrojo = new();
    private DateTimeOffset _ultimoAvisoColaLlena = DateTimeOffset.MinValue;
    private int _descartadasDesdeElAviso;

    /// <summary>Tiempo máximo de cada registro: un SMTP colgado no bloquea la cola.</summary>
    public TimeSpan TiempoMaximoPorRegistro { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Solo para pruebas: sustituye el tratamiento real de cada registro.</summary>
    public Func<DatosRegistroAlta, Uri, CancellationToken, Task>? ProcesadorDePrueba { get; init; }

    /// <summary>
    /// Encola un registro ya validado. No dice si se encoló: sin cupo para ese correo o con la cola llena se descarta,
    /// y quien registra ve lo mismo que en cualquier otro caso.
    /// </summary>
    public void Encolar(DatosRegistroAlta datos, string correoNormalizado, Uri basePublica)
    {
        lock (_cerrojo)
        {
            if (!_limite.HayCupo(correoNormalizado)) return;
            if (!_cola.Writer.TryWrite(new Solicitud(datos, correoNormalizado, basePublica)))
            {
                AvisarColaLlena();
                return;
            }

            _limite.Consumir(correoNormalizado);
        }
    }

    private void AvisarColaLlena()
    {
        _descartadasDesdeElAviso++;
        var ahora = reloj.GetUtcNow();
        if (ahora - _ultimoAvisoColaLlena < IntervaloAvisoColaLlena) return;

        logger.LogWarning("Cola del registro del alta autónoma llena: {Descartadas} registros descartados desde el último aviso.",
            _descartadasDesdeElAviso);
        _ultimoAvisoColaLlena = ahora;
        _descartadasDesdeElAviso = 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Quien actúa es la plataforma, no la persona anónima que se registra.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        await foreach (var solicitud in _cola.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var limiteDeTiempo = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                limiteDeTiempo.CancelAfter(TiempoMaximoPorRegistro);
                await (ProcesadorDePrueba ?? ProcesarAsync)(solicitud.Datos, solicitud.BasePublica, limiteDeTiempo.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Se agotó el tiempo máximo para procesar un registro del alta autónoma; se pasa al siguiente.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "No se pudo procesar un registro del alta autónoma.");
            }
        }
    }

    private async Task ProcesarAsync(DatosRegistroAlta datos, Uri basePublica, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ServicioRegistroAlta>().IniciarAsync(datos, basePublica, cancellationToken);
    }
}

public static class RegistroAltaServiceCollectionExtensions
{
    /// <summary>
    /// Cola del registro y remitente de los correos del alta. En <c>Development</c> el remitente escribe a fichero
    /// (<see cref="RemitenteCorreoAltaDesarrollo"/>) y nunca usa SMTP; en el resto, el canal de correo con su guarda.
    /// </summary>
    public static IServiceCollection AddRegistroAlta(this IServiceCollection services, IHostEnvironment entorno)
    {
        services.AddSingleton<ColaRegistroAlta>();
        services.AddSingleton<IHostedService, ColaRegistroAlta>(sp => sp.GetRequiredService<ColaRegistroAlta>());

        if (entorno.IsDevelopment())
            services.AddScoped<IRemitenteCorreoAlta, RemitenteCorreoAltaDesarrollo>();
        else
            services.AddScoped<IRemitenteCorreoAlta, RemitenteCorreoAltaPorCanal>();

        return services;
    }
}
