using System.Buffers.Binary;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;

namespace CaeManager.Infrastructure.HerramientasPdf;

/// <summary>
/// Lo que viaja entre el proceso web y el proceso aparte que abre PDF ajenos, en binario por la entrada y la salida
/// estándar. Enteros en little-endian.
///
/// Petición: operación (1 byte), número de PDF (int32) y, por cada uno, su longitud (int32) y sus bytes.
/// Respuesta: estado (1 byte, 0 éxito o 1 fallo). Un fallo lleva solo el código (1 byte, índice en
/// <see cref="CodigosDeFallo"/>): el texto lo pone el proceso web. Una inspección con éxito lleva páginas (int32), firmado
/// (1 byte) y bytes de estructura (int64); una unión, páginas (int32), longitud (int32) y el PDF unido.
///
/// El proceso web no se fía de la respuesta: un PDF hostil ha pasado por el otro lado. Cada campo se comprueba contra los
/// topes de <see cref="LimitesUnionPdf"/> y una respuesta que no cuadra se trata como ilegible.
/// </summary>
internal static class ProtocoloProcesoHerramientasPdf
{
    public const byte OperacionInspeccionar = 1;
    public const byte OperacionUnir = 2;

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

    /// <summary>Lo más que ocupa la respuesta a una inspección.</summary>
    public const int MaximoBytesRespuestaInspeccion = 64;

    /// <summary>
    /// Lo más que ocupa la respuesta a una unión: el unido copia las páginas de PDF que juntos no pasan del total, así que el
    /// doble del total, con su cabecera, ya es holgura.
    /// </summary>
    public const int MaximoBytesRespuestaUnion = (int)(2 * LimitesUnionPdf.TamanoMaximoTotalBytes) + 64;

    public sealed record Peticion(byte Operacion, IReadOnlyList<byte[]> Pdfs);

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
            OperacionInspeccionar => numero == 1,
            OperacionUnir => numero is >= 1 and <= LimitesUnionPdf.MaximoFicheros,
            _ => false,
        };
        if (!valido)
            return null;

        var pdfs = new List<byte[]>(numero);
        long total = 0;
        Span<byte> longitud = stackalloc byte[4];
        for (var i = 0; i < numero; i++)
        {
            if (!LeerExacto(origen, longitud))
                return null;

            var bytes = BinaryPrimitives.ReadInt32LittleEndian(longitud);
            total += bytes;
            if (bytes < 0 || total > LimitesUnionPdf.TamanoMaximoTotalBytes)
                return null;

            var pdf = new byte[bytes];
            if (!LeerExacto(origen, pdf))
                return null;
            pdfs.Add(pdf);
        }

        // Nada detrás de la petición.
        return origen.ReadByte() == -1 ? new Peticion(operacion, pdfs) : null;
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

    private static bool EscribirSiFallo<T>(Stream destino, Result<T> resultado)
    {
        if (resultado.EsExitoso)
            return false;

        var indice = Array.IndexOf(CodigosDeFallo, resultado.Error.Codigo);
        // Un código que no es de una entrada no sale del motor; si saliera, es un PDF que no se pudo leer.
        destino.Write([EstadoFallo, (byte)(indice < 0 ? Array.IndexOf(CodigosDeFallo, ErroresUnirPdf.Ilegible) : indice)]);
        return true;
    }

    private static bool LeerFallo<T>(ReadOnlySpan<byte> bytes, out Result<T>? fallo)
    {
        fallo = null;
        if (bytes.Length != 2 || bytes[0] != EstadoFallo)
            return false;

        if (bytes[1] < CodigosDeFallo.Length)
            fallo = Result.Fallo<T>(Error.Crear(CodigosDeFallo[bytes[1]], MensajeDe(CodigosDeFallo[bytes[1]])));
        return fallo is not null;
    }

    internal static string MensajeDe(string codigo) => codigo switch
    {
        ErroresUnirPdf.ConContrasena => "El PDF tiene contraseña.",
        ErroresUnirPdf.DemasiadoGrande => "El PDF supera el tamaño máximo.",
        ErroresUnirPdf.DemasiadasPaginas => "El PDF tiene demasiadas páginas.",
        ErroresUnirPdf.DemasiadoComplejo => "El PDF es demasiado complejo para unirlo.",
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
