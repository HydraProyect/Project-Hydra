using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace CaeManager.Web.Services;

/// <summary>
/// Límite de solicitudes de recuperación por cuenta: tres por hora. La clave es un
/// hash fijo del correo normalizado y el estado es un array de tamaño fijo, indexado
/// por ese hash: no crece con el número de correos distintos que un anónimo pueda
/// enviar y nunca rechaza "por lleno". Dos correos pueden compartir posición (1 entre
/// 65.536); el efecto es compartir cupo de tres por hora, no una fuga ni un error.
/// Solo se usa bajo el cerrojo de quien lo posee.
/// </summary>
public sealed class LimiteSolicitudesPorCuenta(TimeProvider reloj)
{
    public const int Posiciones = 1 << 16;
    public const int MaximoPorVentana = 3;
    public static readonly TimeSpan Ventana = TimeSpan.FromHours(1);

    private readonly long[] _inicio = new long[Posiciones];
    private readonly byte[] _solicitudes = new byte[Posiciones];

    public static int Posicion(string correoNormalizado) =>
        BitConverter.ToUInt16(SHA256.HashData(Encoding.UTF8.GetBytes(correoNormalizado)), 0);

    public bool HayCupo(string correoNormalizado)
    {
        var i = Posicion(correoNormalizado);
        return Caducada(i, reloj.GetUtcNow().UtcTicks) || _solicitudes[i] < MaximoPorVentana;
    }

    public void Consumir(string correoNormalizado)
    {
        var i = Posicion(correoNormalizado);
        var ahora = reloj.GetUtcNow().UtcTicks;
        if (Caducada(i, ahora))
        {
            _inicio[i] = ahora;
            _solicitudes[i] = 0;
        }

        if (_solicitudes[i] < MaximoPorVentana) _solicitudes[i]++;
    }

    private bool Caducada(int i, long ahoraTicks) =>
        _solicitudes[i] == 0 || ahoraTicks - _inicio[i] >= Ventana.Ticks;
}

public sealed class RecuperacionContrasena(
    IServiceScopeFactory scopes, TimeProvider reloj, ILogger<RecuperacionContrasena> logger) : BackgroundService
{
    /// <summary>Longitud máxima de una dirección de correo (RFC 5321); mayor, se descarta sin procesar.</summary>
    public const int LongitudMaximaCorreo = 254;

    private static readonly TimeSpan IntervaloAvisoColaLlena = TimeSpan.FromMinutes(1);

    private sealed record Solicitud(string CorreoNormalizado, Uri BasePublica);
    private readonly LimiteSolicitudesPorCuenta _limite = new(reloj);
    private readonly Channel<Solicitud> _cola = Channel.CreateBounded<Solicitud>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });
    private readonly object _cerrojo = new();
    private DateTimeOffset _ultimoAvisoColaLlena = DateTimeOffset.MinValue;
    private int _descartadasDesdeElAviso;

    /// <summary>Tiempo máximo de cada envío: un SMTP colgado no bloquea la cola.</summary>
    public TimeSpan TiempoMaximoPorEnvio { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Solo para pruebas: sustituye el tratamiento real de cada solicitud.</summary>
    public Func<string, Uri, CancellationToken, Task>? ProcesadorDePrueba { get; init; }

    public void Solicitar(string correoNormalizado, Uri basePublica)
    {
        // Misma respuesta visible que cualquier otra solicitud: sin dirección posible no hay nada que hacer.
        if (string.IsNullOrEmpty(correoNormalizado) || correoNormalizado.Length > LongitudMaximaCorreo) return;

        lock (_cerrojo)
        {
            if (!_limite.HayCupo(correoNormalizado)) return;
            if (!_cola.Writer.TryWrite(new Solicitud(correoNormalizado, basePublica)))
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

        logger.LogWarning("Cola de recuperación de contraseña llena: {Descartadas} solicitudes descartadas desde el último aviso.",
            _descartadasDesdeElAviso);
        _ultimoAvisoColaLlena = ahora;
        _descartadasDesdeElAviso = 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Quien actúa es la plataforma, no la persona que pidió el enlace (que es
        // anónima): se declara en el punto de entrada, como en el resto de servicios de fondo.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        await foreach (var solicitud in _cola.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var limiteDeTiempo = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                limiteDeTiempo.CancelAfter(TiempoMaximoPorEnvio);
                await (ProcesadorDePrueba ?? ProcesarAsync)(solicitud.CorreoNormalizado, solicitud.BasePublica, limiteDeTiempo.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Se agotó el tiempo máximo para procesar una recuperación de contraseña; se pasa a la siguiente.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "No se pudo procesar la recuperación de contraseña.");
            }
        }
    }

    private async Task ProcesarAsync(string correoNormalizado, Uri basePublica, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var usuarios = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var correo = scope.ServiceProvider.GetRequiredService<IEmailService>();
        using var identificacion = AmbitoIdentificacionSinTenant.Abrir();
        var usuario = await usuarios.FindByEmailAsync(correoNormalizado);
        if (usuario is null) return;

        var token = await usuarios.GeneratePasswordResetTokenAsync(usuario);
        var codigo = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var enlace = EnlacesPublicos.Activacion(basePublica, usuario.Id, codigo);
        var cuerpo = $"""
            <p>Recibimos una solicitud para restablecer la contraseña de tu cuenta en {Marca.Nombre}.</p>
            <p><a href="{System.Net.WebUtility.HtmlEncode(enlace)}">Restablecer mi contraseña</a></p>
            <p>Este enlace caduca en 60 minutos. Si no fuiste tú, puedes ignorar este correo — tu contraseña actual sigue siendo válida.</p>
            """;
        var resultado = await correo.EnviarAsync(usuario.Email!, $"Restablece tu contraseña — {Marca.Nombre}", cuerpo,
            tipo: TipoAvisoCorreo.Seguridad, cancellationToken: cancellationToken);
        if (resultado.EsFallido)
            logger.LogWarning("No se pudo enviar el correo de restablecimiento de contraseña a {UsuarioId}.", usuario.Id);
    }
}
