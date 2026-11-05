using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.AltaAutonoma.Antiabuso;

/// <summary>Resultado de las trampas del formulario. Igual que el reto: se distingue para registrar, no para responder.</summary>
public enum ResultadoTrampasFormulario
{
    Superadas,
    CampoTrampaRelleno,
    SelloIlegible,
    SelloDeOtroReto,
    EnvioDemasiadoRapido,
    SelloCaducado,
    SelloReutilizado,
}

/// <summary>
/// Honeypot y trampa de tiempo del formulario del alta (ASVS 4.0.3 V11.1.4).
///
/// <para>
/// <b>Honeypot</b>: un campo de texto fuera de la vista, del orden de tabulación y del árbol de accesibilidad
/// (<c>TrampasFormularioAlta.razor</c>). Una persona no lo ve ni lo alcanza; un programa que rellena todo lo que
/// encuentra, sí. Cualquier valor, incluso solo espacios, cuenta como relleno. Nombre y etiqueta son neutros a
/// propósito: uno que suene a dirección web, organización o contacto invita al autocompletado del navegador, que ignora
/// <c>autocomplete="off"</c> en esos campos y delataría a una persona.
/// </para>
///
/// <para>
/// <b>Trampa de tiempo</b>: el sello lleva un nonce aleatorio, la hora de pintado del formulario y el SHA-256 del token
/// del reto que se pintó con él, todo protegido con Data Protection, así que el cliente no puede adelantar la hora ni
/// cambiar de reto. Un envío antes de <see cref="OpcionesAntiabusoAlta.TiempoMinimoEnvio"/> lo hace un programa. El
/// sello solo vale con su reto y una sola vez: el nonce se consume al superarse, en el mismo
/// <see cref="RegistroRetosUsados"/> que los ids de reto. Sin esa atadura, un sello viejo, ya pasada la espera, valdría
/// con cualquier reto recién emitido y saltaría la espera durante toda su vigencia.
/// </para>
/// </summary>
public sealed class ServicioTrampasFormulario
{
    public const string NombreCampoTrampa = "comprobacion_adicional";
    public const string NombreCampoSello = "sello_formulario";

    internal const string Proposito = "CaeManager.Web.AltaAutonoma.SelloFormulario.v1";
    private const byte Version = 2;
    private const int LongitudCarga = 1 + 16 + 8 + SHA256.HashSizeInBytes;
    private const int LongitudMaximaSello = 512;

    private readonly IDataProtector _protector;
    private readonly IOptions<OpcionesAntiabusoAlta> _opciones;
    private readonly TimeProvider _reloj;
    private readonly RegistroRetosUsados _usados;

    public ServicioTrampasFormulario(
        IDataProtectionProvider proteccion, IOptions<OpcionesAntiabusoAlta> opciones, TimeProvider reloj, RegistroRetosUsados usados)
    {
        _protector = proteccion.CreateProtector(Proposito);
        _opciones = opciones;
        _reloj = reloj;
        _usados = usados;
    }

    /// <summary>Emite el sello del formulario atado al token del reto que se pinta con él.</summary>
    public string EmitirSello(string tokenReto)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenReto);

        var carga = new byte[LongitudCarga];
        carga[0] = Version;
        RandomNumberGenerator.Fill(carga.AsSpan(1, 16));
        BinaryPrimitives.WriteInt64LittleEndian(carga.AsSpan(17, 8), _reloj.GetUtcNow().ToUnixTimeMilliseconds());
        SHA256.HashData(Encoding.UTF8.GetBytes(tokenReto), carga.AsSpan(25, SHA256.HashSizeInBytes));
        return WebEncoders.Base64UrlEncode(_protector.Protect(carga));
    }

    /// <summary>
    /// Comprueba honeypot y sello contra el token del reto enviado en el mismo formulario. Solo un sello que lo supera
    /// todo se consume: un envío demasiado rápido o con otro reto no lo quema.
    /// </summary>
    public ResultadoTrampasFormulario Verificar(string? valorCampoTrampa, string? sello, string? tokenReto)
    {
        if (!string.IsNullOrEmpty(valorCampoTrampa))
            return ResultadoTrampasFormulario.CampoTrampaRelleno;

        if (string.IsNullOrEmpty(sello) || sello.Length > LongitudMaximaSello)
            return ResultadoTrampasFormulario.SelloIlegible;

        byte[] carga;
        try
        {
            carga = _protector.Unprotect(WebEncoders.Base64UrlDecode(sello));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return ResultadoTrampasFormulario.SelloIlegible;
        }

        if (carga.Length != LongitudCarga || carga[0] != Version)
            return ResultadoTrampasFormulario.SelloIlegible;

        if (string.IsNullOrEmpty(tokenReto) || tokenReto.Length > ServicioRetoPruebaTrabajo.LongitudMaximaToken)
            return ResultadoTrampasFormulario.SelloDeOtroReto;

        Span<byte> hashReto = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(tokenReto), hashReto);
        if (!CryptographicOperations.FixedTimeEquals(hashReto, carga.AsSpan(25, SHA256.HashSizeInBytes)))
            return ResultadoTrampasFormulario.SelloDeOtroReto;

        var nonce = new Guid(carga.AsSpan(1, 16));
        var pintado = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(carga.AsSpan(17, 8)));
        var ahora = _reloj.GetUtcNow();
        var transcurrido = ahora - pintado;
        var opciones = _opciones.Value;

        // Un sello del futuro (reloj atrasado entre instancias) da transcurrido negativo y cae aquí: se rechaza.
        if (transcurrido < opciones.TiempoMinimoEnvio)
            return ResultadoTrampasFormulario.EnvioDemasiadoRapido;

        // Caduca antes de que el registro pueda olvidar su nonce (RegistroRetosUsados.MargenPurga), como el reto: nunca
        // vale tras purgarse, ni siquiera para quien leyó la hora justo antes de caducar.
        var caduca = pintado + opciones.VigenciaSelloFormulario;
        if (ahora >= caduca)
            return ResultadoTrampasFormulario.SelloCaducado;

        return _usados.Registrar(nonce, caduca) ? ResultadoTrampasFormulario.Superadas : ResultadoTrampasFormulario.SelloReutilizado;
    }
}
