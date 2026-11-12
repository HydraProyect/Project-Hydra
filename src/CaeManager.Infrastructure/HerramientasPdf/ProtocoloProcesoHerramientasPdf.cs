using System.Buffers.Binary;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;

namespace CaeManager.Infrastructure.HerramientasPdf;

/// <summary>
/// Lo que viaja entre el proceso web y el proceso aparte que abre PDF ajenos, en binario por la entrada y la salida
/// estándar. Enteros en little-endian.
///
/// Petición: operación (1 byte), número de PDF (int32), solo al extraer un tramo sus parámetros (página inicial y final,
/// int32, y cota, int64) y, por cada PDF, su longitud (int32) y sus bytes.
/// Respuesta: estado (1 byte, 0 éxito o 1 fallo). Un fallo lleva solo el código (1 byte, índice en
/// <see cref="CodigosDeFallo"/> o, en Dividir, en <see cref="CodigosDeFalloDivision"/>): el texto lo pone el proceso web.
/// Una inspección con éxito lleva páginas (int32), firmado (1 byte) y bytes de estructura (int64); una unión, páginas
/// (int32), longitud (int32) y el PDF unido; una apertura para dividir, páginas (int32) y firmado (1 byte); un tramo,
/// longitud (int32) y el PDF del tramo.
///
/// El proceso web no se fía de la respuesta: un PDF hostil ha pasado por el otro lado. Cada campo se comprueba contra los
/// topes de <see cref="LimitesUnionPdf"/> y una respuesta que no cuadra se trata como ilegible.
/// </summary>
internal static class ProtocoloProcesoHerramientasPdf
{
    public const byte OperacionInspeccionar = 1;
    public const byte OperacionUnir = 2;
    public const byte OperacionAbrirParaDividir = 3;
    public const byte OperacionExtraerTramo = 4;

    private const byte EstadoExito = 0;
    private const byte EstadoFallo = 1;

    /// <summary>Con lo que empieza todo PDF (ISO 32000-1, 7.5.2): el padre lo exige al contenido de una unión.</summary>
    private static ReadOnlySpan<byte> CabeceraPdf => "%PDF-"u8;

    /// <summary>Los únicos códigos que el proceso aparte puede devolver: los de una entrada que dependen del PDF.</summary>
    internal static readonly string[] CodigosDeFallo =
    [
        ErroresUnirPdf.ConContrasena,
        ErroresUnirPdf.Ilegible,
        ErroresUnirPdf.DemasiadoGrande,
        ErroresUnirPdf.DemasiadasPaginas,
        ErroresUnirPdf.DemasiadoComplejo,
    ];

    /// <summary>Los códigos que el proceso aparte puede devolver al dividir: los de una entrada que dependen del PDF.</summary>
    internal static readonly string[] CodigosDeFalloDivision =
    [
        ErroresDivisorPdf.ConContrasena,
        ErroresDivisorPdf.Ilegible,
        ErroresDivisorPdf.DemasiadoGrande,
        ErroresDivisorPdf.DemasiadasPaginas,
        ErroresDivisorPdf.DemasiadoComplejo,
        ErroresDivisorPdf.RangoInvalido,
    ];

    /// <summary>Lo más que ocupa la respuesta a una apertura para dividir.</summary>
    public const int MaximoBytesRespuestaApertura = 64;

    /// <summary>La cota más alta que se admite para un tramo: la del original más grande que se puede dividir.</summary>
    public static readonly long MaximaCotaTramo = LimitesDivisionPdf.TamanoMaximoTramo(LimitesDivisionPdf.TamanoMaximoOriginalBytes);

    /// <summary>Lo más que ocupa la respuesta a un tramo con esta cota: el tramo y su cabecera.</summary>
    public static int MaximoBytesRespuestaTramo(long cota) => (int)LimitesDivisionPdf.BytesRespuestaTramo(Math.Min(cota, MaximaCotaTramo));

    /// <summary>Lo más que ocupa la respuesta a una inspección.</summary>
    public const int MaximoBytesRespuestaInspeccion = 64;

    /// <summary>
    /// Lo más que ocupa la respuesta a una unión: el unido copia las páginas de PDF que juntos no pasan del total, así que el
    /// doble del total, con su cabecera, ya es holgura.
    /// </summary>
    public const int MaximoBytesRespuestaUnion = (int)(2 * LimitesUnionPdf.TamanoMaximoTotalBytes) + 64;

    /// <param name="Tramo">Solo al extraer un tramo.</param>
    public sealed record Peticion(byte Operacion, IReadOnlyList<byte[]> Pdfs, ParametrosTramo? Tramo = null);

    /// <summary>Páginas (desde 1, ambas incluidas) y cota en bytes del tramo que se extrae.</summary>
    public sealed record ParametrosTramo(int PaginaInicial, int PaginaFinal, long Cota);

    public static async Task EscribirPeticionTramoAsync(Stream destino, byte[] pdf, ParametrosTramo tramo, CancellationToken cancellationToken)
    {
        var cabecera = new byte[5 + 16];
        cabecera[0] = OperacionExtraerTramo;
        BinaryPrimitives.WriteInt32LittleEndian(cabecera.AsSpan(1), 1);
        BinaryPrimitives.WriteInt32LittleEndian(cabecera.AsSpan(5), tramo.PaginaInicial);
        BinaryPrimitives.WriteInt32LittleEndian(cabecera.AsSpan(9), tramo.PaginaFinal);
        BinaryPrimitives.WriteInt64LittleEndian(cabecera.AsSpan(13), tramo.Cota);
        await destino.WriteAsync(cabecera, cancellationToken);

        var longitud = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(longitud, pdf.Length);
        await destino.WriteAsync(longitud, cancellationToken);
        await destino.WriteAsync(pdf, cancellationToken);
        await destino.FlushAsync(cancellationToken);
    }

    public static async Task EscribirPeticionAsync(Stream destino, byte operacion, IReadOnlyList<byte[]> pdfs, CancellationToken cancellationToken)
    {
        var cabecera = new byte[5];
        cabecera[0] = operacion;
        BinaryPrimitives.WriteInt32LittleEndian(cabecera.AsSpan(1), pdfs.Count);
        await destino.WriteAsync(cabecera, cancellationToken);

        var longitud = new byte[4];
        foreach (var pdf in pdfs)
        {
            BinaryPrimitives.WriteInt32LittleEndian(longitud, pdf.Length);
            await destino.WriteAsync(longitud, cancellationToken);
            await destino.WriteAsync(pdf, cancellationToken);
        }

        await destino.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Lee la petición en el proceso aparte, con los mismos topes que el proceso web ya aplicó (número de PDF y total de
    /// bytes) para no reservar a ciegas lo que diga una cabecera. <c>null</c> si no cuadra.
    /// </summary>
    public static Peticion? LeerPeticion(Stream origen)
    {
        Span<byte> cabecera = stackalloc byte[5];
        if (!LeerExacto(origen, cabecera))
            return null;

        var operacion = cabecera[0];
        var numero = BinaryPrimitives.ReadInt32LittleEndian(cabecera[1..]);
        var valido = operacion switch
        {
            OperacionInspeccionar or OperacionAbrirParaDividir or OperacionExtraerTramo => numero == 1,
            OperacionUnir => numero is >= 1 and <= LimitesUnionPdf.MaximoFicheros,
            _ => false,
        };
        if (!valido)
            return null;

        ParametrosTramo? tramo = null;
        if (operacion == OperacionExtraerTramo)
        {
            Span<byte> parametros = stackalloc byte[16];
            if (!LeerExacto(origen, parametros))
                return null;

            tramo = new ParametrosTramo(
                BinaryPrimitives.ReadInt32LittleEndian(parametros),
                BinaryPrimitives.ReadInt32LittleEndian(parametros[4..]),
                BinaryPrimitives.ReadInt64LittleEndian(parametros[8..]));
            if (tramo.PaginaInicial < 1 || tramo.PaginaFinal < tramo.PaginaInicial || tramo.Cota < 1 || tramo.Cota > MaximaCotaTramo)
                return null;
        }

        var maximoTotal = operacion is OperacionUnir or OperacionInspeccionar
            ? LimitesUnionPdf.TamanoMaximoTotalBytes
            : LimitesDivisionPdf.TamanoMaximoOriginalBytes;
        var pdfs = new List<byte[]>(numero);
        long total = 0;
        Span<byte> longitud = stackalloc byte[4];
        for (var i = 0; i < numero; i++)
        {
            if (!LeerExacto(origen, longitud))
                return null;

            var bytes = BinaryPrimitives.ReadInt32LittleEndian(longitud);
            total += bytes;
            if (bytes < 0 || total > maximoTotal)
                return null;

            var pdf = new byte[bytes];
            if (!LeerExacto(origen, pdf))
                return null;
            pdfs.Add(pdf);
        }

        // Nada detrás de la petición.
        return origen.ReadByte() == -1 ? new Peticion(operacion, pdfs, tramo) : null;
    }

    public static void EscribirInspeccion(Stream destino, Result<InfoPdfParaUnir> resultado)
    {
        if (!EscribirSiFallo(destino, resultado))
        {
            Span<byte> cuerpo = stackalloc byte[1 + 4 + 1 + 8];
            cuerpo[0] = EstadoExito;
            BinaryPrimitives.WriteInt32LittleEndian(cuerpo[1..], resultado.Valor.NumeroPaginas);
            cuerpo[5] = resultado.Valor.EstaFirmado ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt64LittleEndian(cuerpo[6..], resultado.Valor.BytesEstructura);
            destino.Write(cuerpo);
        }

        destino.Flush();
    }

    public static void EscribirUnion(Stream destino, Result<PdfUnido> resultado)
    {
        if (!EscribirSiFallo(destino, resultado))
        {
            Span<byte> cabecera = stackalloc byte[1 + 4 + 4];
            cabecera[0] = EstadoExito;
            BinaryPrimitives.WriteInt32LittleEndian(cabecera[1..], resultado.Valor.NumeroPaginas);
            BinaryPrimitives.WriteInt32LittleEndian(cabecera[5..], resultado.Valor.Contenido.Length);
            destino.Write(cabecera);
            destino.Write(resultado.Valor.Contenido);
        }

        destino.Flush();
    }

    public static void EscribirApertura(Stream destino, Result<(int NumeroPaginas, bool EstaFirmado)> resultado)
    {
        if (!EscribirSiFallo(destino, resultado, CodigosDeFalloDivision, ErroresDivisorPdf.Ilegible))
        {
            Span<byte> cuerpo = stackalloc byte[1 + 4 + 1];
            cuerpo[0] = EstadoExito;
            BinaryPrimitives.WriteInt32LittleEndian(cuerpo[1..], resultado.Valor.NumeroPaginas);
            cuerpo[5] = resultado.Valor.EstaFirmado ? (byte)1 : (byte)0;
            destino.Write(cuerpo);
        }

        destino.Flush();
    }

    public static void EscribirTramo(Stream destino, Result<byte[]> resultado)
    {
        if (!EscribirSiFallo(destino, resultado, CodigosDeFalloDivision, ErroresDivisorPdf.Ilegible))
        {
            Span<byte> cabecera = stackalloc byte[1 + 4];
            cabecera[0] = EstadoExito;
            BinaryPrimitives.WriteInt32LittleEndian(cabecera[1..], resultado.Valor.Length);
            destino.Write(cabecera);
            destino.Write(resultado.Valor);
        }

        destino.Flush();
    }

    /// <summary>La apertura para dividir que devolvió el proceso aparte, o <c>null</c> si la respuesta no cuadra.</summary>
    public static Result<(int NumeroPaginas, bool EstaFirmado)>? LeerApertura(ReadOnlyMemory<byte> respuesta)
    {
        var bytes = respuesta.Span;
        if (LeerFallo<(int, bool)>(bytes, CodigosDeFalloDivision, out var fallo))
            return fallo;

        if (bytes.Length != 6 || bytes[0] != EstadoExito || bytes[5] > 1)
            return null;

        var paginas = BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]);
        if (paginas is < 1 or > CaeManager.Application.Trabajadores.Extraccion.LimitesLecturaPalabrasPdf.MaximoPaginas)
            return null;

        return Result.Exito((paginas, bytes[5] == 1));
    }

    /// <summary>
    /// El tramo que devolvió el proceso aparte, o <c>null</c> si la respuesta no cuadra: tampoco si pasa de la cota pedida,
    /// aunque el lanzador ya corta la salida que pasa de lo que cabe en ella.
    /// </summary>
    public static Result<byte[]>? LeerTramo(ReadOnlyMemory<byte> respuesta, long cota)
    {
        var bytes = respuesta.Span;
        if (LeerFallo<byte[]>(bytes, CodigosDeFalloDivision, out var fallo))
            return fallo;

        if (bytes.Length < 5 || bytes[0] != EstadoExito)
            return null;

        var longitud = BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]);
        if (longitud <= 0 || longitud > cota || longitud != bytes.Length - 5 || !bytes[5..].StartsWith(CabeceraPdf))
            return null;

        return Result.Exito(respuesta[5..].ToArray());
    }

    /// <summary>La inspección que devolvió el proceso aparte, o <c>null</c> si la respuesta no cuadra.</summary>
    public static Result<InfoPdfParaUnir>? LeerInspeccion(ReadOnlyMemory<byte> respuesta)
    {
        var bytes = respuesta.Span;
        if (LeerFallo<InfoPdfParaUnir>(bytes, out var fallo))
            return fallo;

        if (bytes.Length != 14 || bytes[0] != EstadoExito || bytes[5] > 1)
            return null;

        var paginas = BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]);
        var estructura = BinaryPrimitives.ReadInt64LittleEndian(bytes[6..]);
        if (paginas is < 0 or > LimitesUnionPdf.MaximoPaginasTotales || estructura is < 0 or > LimitesUnionPdf.MaximoBytesEstructuraPorPdf)
            return null;

        return Result.Exito(new InfoPdfParaUnir(paginas, bytes[5] == 1, estructura));
    }

    /// <summary>La unión que devolvió el proceso aparte, o <c>null</c> si la respuesta no cuadra.</summary>
    public static Result<PdfUnido>? LeerUnion(ReadOnlyMemory<byte> respuesta)
    {
        var bytes = respuesta.Span;
        if (LeerFallo<PdfUnido>(bytes, out var fallo))
            return fallo;

        if (bytes.Length < 9 || bytes[0] != EstadoExito)
            return null;

        var paginas = BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]);
        var longitud = BinaryPrimitives.ReadInt32LittleEndian(bytes[5..]);
        if (paginas is < 1 or > LimitesUnionPdf.MaximoPaginasTotales || longitud <= 0 || longitud != bytes.Length - 9)
            return null;

        // Lo que se entrega como PDF tiene que empezar como uno: el padre no da por bueno cualquier contenido del hijo.
        if (!bytes[9..].StartsWith(CabeceraPdf))
            return null;

        return Result.Exito(new PdfUnido(respuesta[9..].ToArray(), paginas));
    }

    private static bool EscribirSiFallo<T>(Stream destino, Result<T> resultado) =>
        EscribirSiFallo(destino, resultado, CodigosDeFallo, ErroresUnirPdf.Ilegible);

    private static bool EscribirSiFallo<T>(Stream destino, Result<T> resultado, string[] codigos, string ilegible)
    {
        if (resultado.EsExitoso)
            return false;

        var indice = Array.IndexOf(codigos, resultado.Error.Codigo);
        // Un código que no es de una entrada no sale del motor; si saliera, es un PDF que no se pudo leer.
        destino.Write([EstadoFallo, (byte)(indice < 0 ? Array.IndexOf(codigos, ilegible) : indice)]);
        return true;
    }

    private static bool LeerFallo<T>(ReadOnlySpan<byte> bytes, out Result<T>? fallo) => LeerFallo(bytes, CodigosDeFallo, out fallo);

    private static bool LeerFallo<T>(ReadOnlySpan<byte> bytes, string[] codigos, out Result<T>? fallo)
    {
        fallo = null;
        if (bytes.Length != 2 || bytes[0] != EstadoFallo)
            return false;

        if (bytes[1] < codigos.Length)
            fallo = Result.Fallo<T>(Error.Crear(codigos[bytes[1]], MensajeDe(codigos[bytes[1]])));
        return fallo is not null;
    }

    internal static string MensajeDe(string codigo) => codigo switch
    {
        ErroresUnirPdf.ConContrasena => "El PDF tiene contraseña.",
        ErroresUnirPdf.DemasiadoGrande => "El PDF supera el tamaño máximo.",
        ErroresUnirPdf.DemasiadasPaginas => "El PDF tiene demasiadas páginas.",
        ErroresUnirPdf.DemasiadoComplejo => "El PDF es demasiado complejo para unirlo.",
        ErroresDivisorPdf.ConContrasena => "El PDF tiene contraseña.",
        ErroresDivisorPdf.DemasiadoGrande => "El PDF o el tramo supera el tamaño que se puede preparar.",
        ErroresDivisorPdf.DemasiadasPaginas => "El PDF tiene demasiadas páginas.",
        ErroresDivisorPdf.DemasiadoComplejo => "El PDF es demasiado complejo para procesarlo.",
        ErroresDivisorPdf.RangoInvalido => "Las páginas no están en el PDF.",
        _ => "No hemos podido leer este PDF.",
    };

    private static bool LeerExacto(Stream origen, Span<byte> destino)
    {
        try
        {
            origen.ReadExactly(destino);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }
}
