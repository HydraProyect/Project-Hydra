using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace CaeManager.Web.Services;

/// <summary>
/// Límite de solicitudes por cuenta: tres por correo y hora. Lo comparten la recuperación de contraseña y el registro
/// del alta autónoma.
///
/// <para>
/// La clave es el SHA-256 completo del correo normalizado: dos correos distintos no comparten cupo en la práctica, así
/// que nadie puede calcular otro correo que gaste el cupo de una víctima (con una clave de 16 bits bastaban unos cientos
/// de intentos). Guardar la huella y no el correo evita retener direcciones en memoria durante la hora.
/// </para>
///
/// <para>
/// El estado guarda los correos con ventana abierta en la última hora, en el orden en que la abrieron: como las ventanas
/// vencen en ese mismo orden, cada llamada retira por delante las vencidas en tiempo amortizado constante, sin barrer
/// el diccionario. Tiene un techo de <see cref="MaximoCuentas"/> entradas (unos 16 MB). Al llegar, un correo nuevo no se
/// niega, porque negarlo dejaría a todo el mundo sin enlace mientras alguien mantenga el techo lleno: se retira la ventana
/// más antigua. El coste lo paga ese correo, que recupera su cupo antes de la hora, y solo si alguien sostiene ese
/// número de correos distintos aceptados en una hora.
/// Solo se usa bajo el cerrojo de quien lo posee.
/// </para>
/// </summary>
public sealed class LimiteSolicitudesPorCuenta(TimeProvider reloj)
{
    public const int MaximoCuentas = 1 << 18;
    public const int MaximoPorVentana = 3;
    public static readonly TimeSpan Ventana = TimeSpan.FromHours(1);

    private readonly record struct Huella(UInt128 Alta, UInt128 Baja);

    private record struct Cupo(long InicioTicks, int Solicitudes);

    private readonly Dictionary<Huella, Cupo> _cupos = [];

    // Ventanas en el orden en que se abrieron. Un elemento cuyo inicio ya no coincide con el de su entrada (porque la
    // ventana se reabrió o se retiró) se descarta al llegar al frente.
    private readonly Queue<(Huella Huella, long InicioTicks)> _orden = new();

    /// <summary>Entradas en memoria; para pruebas.</summary>
    public int CuentasEnMemoria => _cupos.Count;

    public bool HayCupo(string correoNormalizado)
    {
        var ahora = reloj.GetUtcNow().UtcTicks;
        RetirarVencidas(ahora);
        return !_cupos.TryGetValue(HuellaDe(correoNormalizado), out var cupo)
               || Caducado(cupo, ahora)
               || cupo.Solicitudes < MaximoPorVentana;
    }

    public void Consumir(string correoNormalizado)
    {
        var huella = HuellaDe(correoNormalizado);
        var ahora = reloj.GetUtcNow().UtcTicks;
        RetirarVencidas(ahora);

        if (_cupos.TryGetValue(huella, out var cupo) && !Caducado(cupo, ahora))
        {
            if (cupo.Solicitudes < MaximoPorVentana)
                _cupos[huella] = cupo with { Solicitudes = cupo.Solicitudes + 1 };
            return;
        }

        if (!_cupos.ContainsKey(huella))
        {
            while (_cupos.Count >= MaximoCuentas && _orden.TryDequeue(out var masAntigua))
                RetirarSiSigueAbierta(masAntigua);
        }

        _cupos[huella] = new Cupo(ahora, 1);
        _orden.Enqueue((huella, ahora));
    }

    private void RetirarVencidas(long ahoraTicks)
    {
        while (_orden.TryPeek(out var frente) && ahoraTicks - frente.InicioTicks >= Ventana.Ticks)
            RetirarSiSigueAbierta(_orden.Dequeue());
    }

    private void RetirarSiSigueAbierta((Huella Huella, long InicioTicks) ventana)
    {
        if (_cupos.TryGetValue(ventana.Huella, out var cupo) && cupo.InicioTicks == ventana.InicioTicks)
            _cupos.Remove(ventana.Huella);
    }

    private static bool Caducado(Cupo cupo, long ahoraTicks) => ahoraTicks - cupo.InicioTicks >= Ventana.Ticks;

    private static Huella HuellaDe(string correoNormalizado)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(correoNormalizado), hash);
        return new Huella(BinaryPrimitives.ReadUInt128LittleEndian(hash), BinaryPrimitives.ReadUInt128LittleEndian(hash[16..]));
    }
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
                // ASVS 4.0.3 V7.1.1: solo el tipo de la excepción. Su mensaje (o el de su causa: SMTP, Identity) puede
                // citar la dirección de quien pidió el enlace, y el registro no puede decir si esa cuenta existe.
                logger.LogError("No se pudo procesar la recuperación de contraseña ({TipoExcepcion}).", ex.GetType().Name);
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
            {BotonCorreo.Html("Restablecer mi contraseña", enlace)}
            <p>Este enlace caduca en 60 minutos. Si no fuiste tú, puedes ignorar este correo — tu contraseña actual sigue siendo válida.</p>
            """;
        var resultado = await correo.EnviarAsync(usuario.Email!, $"Restablece tu contraseña — {Marca.Nombre}", cuerpo,
            tipo: TipoAvisoCorreo.Seguridad, cancellationToken: cancellationToken);
        if (resultado.EsFallido)
            logger.LogWarning("No se pudo enviar el correo de restablecimiento de contraseña a {UsuarioId}.", usuario.Id);
    }
}
