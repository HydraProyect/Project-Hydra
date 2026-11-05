using System.Net;
using System.Security.Cryptography;
using System.Text;
using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

public enum OrigenRespuestaSugerida
{
    Macro = 0,
    Vega = 1
}

public enum EstadoRespuestaSugerida
{
    /// <summary>Generada y todavía fuera del borrador: se puede insertar o descartar.</summary>
    Propuesta = 0,

    /// <summary>Insertada en el borrador sin revisar: bloquea todo envío en la conversación.</summary>
    SinRevisar = 1,

    /// <summary>Una persona la confirmó. Estado final.</summary>
    Revisada = 2,

    /// <summary>Descartada a mano o sustituida por otra generación. Estado final.</summary>
    Descartada = 3
}

/// <summary>
/// Texto propuesto para contestar al último entrante de una conversación: una macro rellenada o una redacción de Vega.
/// Nunca se envía sola y no se puede enviar por error: una persona revisa y envía siempre. Quien impide el envío en
/// servidor es <c>BloqueoRespuestaSugerida</c>, que mira <see cref="Estado"/>, <see cref="TextoHash"/> y, dentro de una
/// conversación, <see cref="Texto"/>. El Mensaje saliente que parte de una sugerencia revisada guarda su Id y
/// <see cref="OrigenTextoMensaje.IaRevisada"/> (si el <see cref="Origen"/> es Vega) o <see cref="OrigenTextoMensaje.MacroRevisada"/>,
/// para poder demostrar después qué contenido saliente nació de la IA.
///
/// <para>
/// Vive en el Tenant propietario de la conversación. Sin navegación desde <see cref="Conversacion"/>, como
/// <see cref="NotaInternaConversacion"/>; la integridad con el hilo y con el mensaje de origen la sostienen FK compuestas
/// con el Tenant creadas en SQL por la migración. Una conversación tiene como mucho una sugerencia activa
/// (<see cref="EstadoRespuestaSugerida.Propuesta"/> o <see cref="EstadoRespuestaSugerida.SinRevisar"/>), por índice único
/// parcial. El texto y su hash no cambian tras generarse: las columnas ni siquiera admiten UPDATE para el rol de la aplicación.
/// </para>
/// </summary>
public class RespuestaSugerida : EntidadBase
{
    public const int LongitudMaximaTexto = Mensaje.LongitudMaximaCuerpoHtml;
    public const int LongitudTextoHash = 64;

    private static readonly HashSet<string> EtiquetasEnLinea = new(
        ["b", "i", "em", "strong", "u", "span", "a", "font", "mark", "small", "sub", "sup"], StringComparer.OrdinalIgnoreCase);

    public static readonly Error ErrorTransicionNoPermitida = Error.Crear(
        "Sugerencia.TransicionNoPermitida", "Esta respuesta sugerida ya no admite esa acción.");

    public Guid ConversacionId { get; private set; }

    /// <summary>Último entrante de la conversación cuando se generó.</summary>
    public Guid MensajeOrigenId { get; private set; }

    public OrigenRespuestaSugerida Origen { get; private set; }
    public Guid? MacroId { get; private set; }

    /// <summary>Contenido con el mismo formato que el cuerpo que se enviaría; la auditoría lo enmascara.</summary>
    public string Texto { get; private set; } = string.Empty;

    /// <summary><see cref="CalcularTextoHash"/> de <see cref="Texto"/>: compara con un envío sin descargar el contenido.</summary>
    public string TextoHash { get; private set; } = string.Empty;

    public EstadoRespuestaSugerida Estado { get; private set; }
    public Guid GeneradaPorUsuarioId { get; private set; }
    public DateTime GeneradaEnUtc { get; private set; }
    public Guid? RevisadaPorUsuarioId { get; private set; }
    public DateTime? RevisadaEnUtc { get; private set; }

    public bool EstaActiva => Estado is EstadoRespuestaSugerida.Propuesta or EstadoRespuestaSugerida.SinRevisar;

    private RespuestaSugerida()
    {
    }

    private RespuestaSugerida(
        Guid conversacionId, Guid mensajeOrigenId, OrigenRespuestaSugerida origen, Guid? macroId, string texto,
        Guid generadaPorUsuarioId, DateTime generadaEnUtc)
    {
        if (conversacionId == Guid.Empty)
            throw new ArgumentException("La sugerencia debe pertenecer a una conversación.", nameof(conversacionId));
        if (mensajeOrigenId == Guid.Empty)
            throw new ArgumentException("La sugerencia debe contestar a un mensaje.", nameof(mensajeOrigenId));
        if (generadaPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La sugerencia la pide una persona.", nameof(generadaPorUsuarioId));
        if (string.IsNullOrWhiteSpace(texto))
            throw new ArgumentException("La sugerencia no puede estar vacía.", nameof(texto));
        if (texto.Length > LongitudMaximaTexto)
            throw new ArgumentException($"La sugerencia no puede superar {LongitudMaximaTexto} caracteres.", nameof(texto));

        ConversacionId = conversacionId;
        MensajeOrigenId = mensajeOrigenId;
        Origen = origen;
        MacroId = macroId;
        Texto = texto;
        TextoHash = CalcularTextoHash(texto);
        Estado = EstadoRespuestaSugerida.Propuesta;
        GeneradaPorUsuarioId = generadaPorUsuarioId;
        GeneradaEnUtc = generadaEnUtc;
    }

    public static RespuestaSugerida DesdeMacro(
        Guid conversacionId, Guid mensajeOrigenId, Guid macroId, string texto, Guid generadaPorUsuarioId, DateTime generadaEnUtc)
    {
        if (macroId == Guid.Empty)
            throw new ArgumentException("La sugerencia de una macro debe nombrarla.", nameof(macroId));

        return new RespuestaSugerida(
            conversacionId, mensajeOrigenId, OrigenRespuestaSugerida.Macro, macroId, texto, generadaPorUsuarioId, generadaEnUtc);
    }

    public static RespuestaSugerida DeVega(
        Guid conversacionId, Guid mensajeOrigenId, string texto, Guid generadaPorUsuarioId, DateTime generadaEnUtc) =>
        new(conversacionId, mensajeOrigenId, OrigenRespuestaSugerida.Vega, null, texto, generadaPorUsuarioId, generadaEnUtc);

    public Result Insertar()
    {
        if (Estado != EstadoRespuestaSugerida.Propuesta)
            return Result.Fallo(ErrorTransicionNoPermitida);

        Estado = EstadoRespuestaSugerida.SinRevisar;
        return Result.Exito();
    }

    public Result Confirmar(Guid usuarioId, DateTime ahoraUtc)
    {
        if (Estado != EstadoRespuestaSugerida.SinRevisar)
            return Result.Fallo(ErrorTransicionNoPermitida);

        Estado = EstadoRespuestaSugerida.Revisada;
        RevisadaPorUsuarioId = usuarioId;
        RevisadaEnUtc = ahoraUtc;
        return Result.Exito();
    }

    public Result Descartar()
    {
        if (!EstaActiva)
            return Result.Fallo(ErrorTransicionNoPermitida);

        Estado = EstadoRespuestaSugerida.Descartada;
        return Result.Exito();
    }

    /// <summary>SHA-256 en hexadecimal de <see cref="NormalizarTexto"/>: el mismo para lo que se genera y lo que se envía.</summary>
    public static string CalcularTextoHash(string texto) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizarTexto(texto))));

    /// <summary>
    /// Sin HTML, con las entidades decodificadas y los espacios colapsados. Una etiqueta en línea desaparece, porque no
    /// separa palabras («re<b>ci</b>bido» es «recibido»); cualquier otra deja un espacio. Tiempo lineal: el nombre que se
    /// lee para decidirlo es parte de la etiqueta que se salta después.
    /// </summary>
    public static string NormalizarTexto(string texto)
    {
        var ultimoCierre = texto.LastIndexOf('>');
        var sinEtiquetas = new StringBuilder(texto.Length);
        for (var posicion = 0; posicion < texto.Length; posicion++)
        {
            if (AbreEtiqueta(texto, posicion, ultimoCierre))
            {
                if (!EsEtiquetaEnLinea(texto, posicion))
                    sinEtiquetas.Append(' ');

                posicion = texto.IndexOf('>', posicion);
            }
            else
            {
                sinEtiquetas.Append(texto[posicion]);
            }
        }

        var decodificado = WebUtility.HtmlDecode(sinEtiquetas.ToString());
        return string.Join(' ', decodificado.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    // Un < suelto es texto: «5<10» no abre nada, aunque más adelante haya un >.
    private static bool AbreEtiqueta(string texto, int posicion, int ultimoCierre) =>
        texto[posicion] == '<' && posicion < ultimoCierre
        && (char.IsAsciiLetter(texto[posicion + 1]) || texto[posicion + 1] is '/' or '!');

    private static bool EsEtiquetaEnLinea(string texto, int posicion)
    {
        var inicio = texto[posicion + 1] == '/' ? posicion + 2 : posicion + 1;
        var fin = inicio;
        while (fin < texto.Length && char.IsAsciiLetterOrDigit(texto[fin]))
            fin++;

        return EtiquetasEnLinea.Contains(texto[inicio..fin]);
    }
}
