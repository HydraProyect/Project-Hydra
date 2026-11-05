using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.AltaAutonoma.Antiabuso;

/// <summary>Reto emitido: el token opaco que viaja al navegador y los bits que pide (informativos para el cliente).</summary>
public sealed record RetoPruebaTrabajo(string Token, int DificultadBits);

/// <summary>
/// Resultado de verificar un reto. Distingue los casos para registrarlos y probarlos; la respuesta al navegador no debe
/// distinguirlos (un único rechazo genérico), para no orientar a quien automatiza el envío.
/// </summary>
public enum ResultadoVerificacionReto
{
    Valido,
    Ilegible,
    Caducado,
    DificultadInsuficiente,
    Reutilizado,
}

/// <summary>
/// CAPTCHA propio de prueba de trabajo (DA-4; ASVS 4.0.3 V2.2.1 y V11.1.4), sin terceros ni datos fuera del servidor.
///
/// <para>
/// <b>Emisión</b>: el token es una carga binaria (versión, id aleatorio de 16 bytes, sal de 16 bytes, dificultad en
/// bits, emisión y caducidad) protegida con ASP.NET Data Protection, que la cifra y la autentica con las claves que la
/// aplicación ya tiene; no hay secreto nuevo. El navegador no puede leerla ni cambiarla: la dificultad que cuenta es la
/// de dentro, nunca la que diga el cliente.
/// </para>
///
/// <para>
/// <b>Solución</b>: un nonce decimal tal que SHA-256(UTF-8(token + ":" + nonce)) empiece por al menos tantos bits cero
/// como pida la carga. Lo busca <c>wwwroot/js/reto-prueba-trabajo-worker.js</c>; el mismo cálculo está en
/// <see cref="BitsCeroIniciales"/>.
/// </para>
///
/// <para>
/// <b>Verificación sin estado salvo el uso único</b>: firma, caducidad y hash se comprueban con el propio token. El uso
/// único lo da <see cref="RegistroRetosUsados"/>, en memoria de la instancia y solo hasta la caducidad del reto. Con dos
/// instancias sirviendo a la vez (ranuras azul y verde durante un relevo), un reto resuelto podría aceptarse una vez en
/// cada una: límite conocido, acotado a la ventana del relevo y a la caducidad del reto.
/// </para>
/// </summary>
public sealed class ServicioRetoPruebaTrabajo
{
    internal const string Proposito = "CaeManager.Web.AltaAutonoma.RetoPruebaTrabajo.v1";
    private const byte Version = 1;
    private const int LongitudCarga = 1 + 16 + 16 + 1 + 8 + 8;
    internal const int LongitudMaximaToken = 1024;
    private const int LongitudMaximaNonce = 20;

    private readonly IDataProtector _protector;
    private readonly IOptions<OpcionesAntiabusoAlta> _opciones;
    private readonly TimeProvider _reloj;
    private readonly RegistroRetosUsados _usados;

    public ServicioRetoPruebaTrabajo(
        IDataProtectionProvider proteccion, IOptions<OpcionesAntiabusoAlta> opciones, TimeProvider reloj, RegistroRetosUsados usados)
    {
        _protector = proteccion.CreateProtector(Proposito);
        _opciones = opciones;
        _reloj = reloj;
        _usados = usados;
    }

    public RetoPruebaTrabajo Emitir()
    {
        var opciones = _opciones.Value;
        var ahora = _reloj.GetUtcNow();
        var carga = new byte[LongitudCarga];
        carga[0] = Version;
        RandomNumberGenerator.Fill(carga.AsSpan(1, 32));
        carga[33] = (byte)opciones.DificultadBits;
        BinaryPrimitives.WriteInt64LittleEndian(carga.AsSpan(34, 8), ahora.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt64LittleEndian(carga.AsSpan(42, 8), (ahora + opciones.VigenciaReto).ToUnixTimeMilliseconds());

        return new RetoPruebaTrabajo(WebEncoders.Base64UrlEncode(_protector.Protect(carga)), opciones.DificultadBits);
    }

    public ResultadoVerificacionReto Verificar(string? token, string? nonce)
    {
        if (string.IsNullOrEmpty(token) || token.Length > LongitudMaximaToken
            || string.IsNullOrEmpty(nonce) || nonce.Length > LongitudMaximaNonce || !nonce.All(char.IsAsciiDigit))
            return ResultadoVerificacionReto.Ilegible;

        byte[] carga;
        try
        {
            carga = _protector.Unprotect(WebEncoders.Base64UrlDecode(token));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return ResultadoVerificacionReto.Ilegible;
        }

        if (carga.Length != LongitudCarga || carga[0] != Version)
            return ResultadoVerificacionReto.Ilegible;

        var id = new Guid(carga.AsSpan(1, 16));
        int bits = carga[33];
        var caduca = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(carga.AsSpan(42, 8)));

        if (_reloj.GetUtcNow() >= caduca)
            return ResultadoVerificacionReto.Caducado;

        if (BitsCeroIniciales(token, nonce) < bits)
            return ResultadoVerificacionReto.DificultadInsuficiente;

        // Solo una solución correcta consume el reto: un nonce equivocado no lo quema para quien sí lo resolvió.
        return _usados.Registrar(id, caduca) ? ResultadoVerificacionReto.Valido : ResultadoVerificacionReto.Reutilizado;
    }

    /// <summary>Bits cero al principio de SHA-256(UTF-8(token + ":" + nonce)), el mismo cálculo que el Web Worker.</summary>
    public static int BitsCeroIniciales(string token, string nonce)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(token + ":" + nonce), hash);

        var ceros = 0;
        foreach (var octeto in hash)
        {
            if (octeto == 0)
            {
                ceros += 8;
                continue;
            }

            return ceros + BitOperations.LeadingZeroCount((uint)octeto) - 24;
        }

        return ceros;
    }
}

/// <summary>
/// Ids de reto y nonces de sello del formulario (<see cref="ServicioTrampasFormulario"/>) ya aceptados, hasta su
/// caducidad: ambos son 128 bits aleatorios, así que comparten almacén sin chocar. En memoria de la instancia (ver
/// <see cref="ServicioRetoPruebaTrabajo"/>). Solo crece con verificaciones superadas (retos resueltos, cada uno con su
/// trabajo, y sellos que esperaron el tiempo mínimo junto a su reto) y se purga de caducados cada pocos registros. Quien
/// lo use en un envío verifica primero el reto, para que un sello solo se consuma tras el trabajo.
/// </summary>
public sealed class RegistroRetosUsados(TimeProvider reloj)
{
    private const int RegistrosEntreLimpiezas = 256;

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _usados = new();
    private long _registros;

    public int Cantidad => _usados.Count;

    /// <summary>Marca el id como usado; devuelve false si ya lo estaba.</summary>
    public bool Registrar(Guid id, DateTimeOffset caduca)
    {
        if (Interlocked.Increment(ref _registros) % RegistrosEntreLimpiezas == 0)
            PurgarCaducados();

        return _usados.TryAdd(id, caduca);
    }

    public void PurgarCaducados()
    {
        // Retirar un id caducado no reabre su reto: la verificación rechaza la caducidad antes de consultar el registro.
        var ahora = reloj.GetUtcNow();
        foreach (var (id, caduca) in _usados)
        {
            if (caduca <= ahora)
                _usados.TryRemove(id, out _);
        }
    }
}
