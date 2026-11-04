using System.Buffers.Binary;
using System.Security.Cryptography;
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
    EnvioDemasiadoRapido,
    SelloCaducado,
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
/// <b>Trampa de tiempo</b>: el sello es la hora de pintado del formulario, protegida con Data Protection, así que el
/// cliente no puede adelantarla. Un envío antes de <see cref="OpcionesAntiabusoAlta.TiempoMinimoEnvio"/> lo hace un
/// programa. El sello no es de un solo uso: lo que impide repetir envíos es el reto, que sí lo es, y el limitador.
/// </para>
/// </summary>
public sealed class ServicioTrampasFormulario
{
    public const string NombreCampoTrampa = "comprobacion_adicional";
    public const string NombreCampoSello = "sello_formulario";

    internal const string Proposito = "CaeManager.Web.AltaAutonoma.SelloFormulario.v1";
    private const byte Version = 1;
    private const int LongitudCarga = 1 + 8;
    private const int LongitudMaximaSello = 512;

    private readonly IDataProtector _protector;
    private readonly IOptions<OpcionesAntiabusoAlta> _opciones;
    private readonly TimeProvider _reloj;

    public ServicioTrampasFormulario(IDataProtectionProvider proteccion, IOptions<OpcionesAntiabusoAlta> opciones, TimeProvider reloj)
    {
        _protector = proteccion.CreateProtector(Proposito);
        _opciones = opciones;
        _reloj = reloj;
    }

    public string EmitirSello()
    {
        var carga = new byte[LongitudCarga];
        carga[0] = Version;
        BinaryPrimitives.WriteInt64LittleEndian(carga.AsSpan(1, 8), _reloj.GetUtcNow().ToUnixTimeMilliseconds());
        return WebEncoders.Base64UrlEncode(_protector.Protect(carga));
    }

    public ResultadoTrampasFormulario Verificar(string? valorCampoTrampa, string? sello)
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

        var pintado = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(carga.AsSpan(1, 8)));
        var transcurrido = _reloj.GetUtcNow() - pintado;
        var opciones = _opciones.Value;

        // Un sello del futuro (reloj atrasado entre instancias) da transcurrido negativo y cae aquí: se rechaza.
        if (transcurrido < opciones.TiempoMinimoEnvio)
            return ResultadoTrampasFormulario.EnvioDemasiadoRapido;

        return transcurrido > opciones.VigenciaSelloFormulario
            ? ResultadoTrampasFormulario.SelloCaducado
            : ResultadoTrampasFormulario.Superadas;
    }
}
