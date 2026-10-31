using System.Security.Cryptography;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;

namespace CaeManager.Application.Exportacion;

/// <summary>
/// Zip de la exportación completa de UN Tenant propietario: cada fichero se escribe en streaming,
/// con su SHA-256 calculado al vuelo, y al terminar se añade <c>manifiesto.json</c> con la fecha,
/// el Tenant propietario, el recuento por entidad, el hash de cada fichero, los Documentos cuyo
/// binario ya no estaba en el almacén y lo que la exportación deja fuera a sabiendas. Solo da formato: qué filas entran y bajo qué contexto de
/// RLS se leen lo decide quien lo llena (<c>ExportacionCompletaTenant</c>, en Infrastructure).
///
/// <para>
/// <b>Cifrado.</b> Cada entrada, manifiesto incluido, va en AES-256 de WinZip (ASVS 4.0.3 V6.2.2,
/// V8.3.4), que el Tenant propietario abre con 7-Zip sin herramientas de TALVEG. No hay vía sin
/// contraseña. Los nombres de las entradas quedan en claro, porque el formato zip no los cifra:
/// nombran tipos de entidad e identificadores de Documento, nunca datos del Tenant.
/// </para>
/// </summary>
public sealed class ArchivoExportacion : IDisposable
{
    public const string RutaManifiesto = "manifiesto.json";

    /// <summary>Sin caracteres confundibles: la contraseña se copia a mano de una consola a otro canal.</summary>
    private const string AlfabetoContrasena = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private const int LongitudContrasena = 32;
    private const int LongitudMinimaContrasena = 20;
    private const int BitsClaveAes = 256;

    private readonly ZipOutputStream archivo;
    private readonly SortedDictionary<string, string> hashes = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> recuentos = new(StringComparer.Ordinal);
    private readonly List<Guid> documentosSinFichero = [];
    private readonly SortedDictionary<string, string> noIncluido = new(StringComparer.Ordinal);
    private readonly Guid tenantId;
    private readonly TimeProvider reloj;
    private bool terminado;

    /// <summary>Contraseña aleatoria de una exportación, unos 183 bits. Nunca se guarda ni se registra.</summary>
    public static string GenerarContrasena() => RandomNumberGenerator.GetString(AlfabetoContrasena, LongitudContrasena);

    public ArchivoExportacion(Stream destino, Guid tenantId, string contrasena, TimeProvider? reloj = null)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("El Tenant propietario es obligatorio.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(contrasena) || contrasena.Length < LongitudMinimaContrasena)
            throw new ArgumentException("La contraseña de la exportación es obligatoria y no puede ser corta.", nameof(contrasena));
        this.tenantId = tenantId;
        this.reloj = reloj ?? TimeProvider.System;
        archivo = new ZipOutputStream(destino) { IsStreamOwner = false, Password = contrasena };
    }

    public Task AgregarAsync(string ruta, Stream contenido, CancellationToken cancellationToken = default) =>
        EscribirAsync(ruta, salida => contenido.CopyToAsync(salida, cancellationToken), cancellationToken);

    /// <summary>
    /// Abre la entrada <paramref name="ruta"/> y deja que <paramref name="escribir"/> la llene; lo
    /// escrito se hashea a la vez que se comprime, sin guardarlo entero en memoria.
    /// </summary>
    public async Task EscribirAsync(string ruta, Func<Stream, Task> escribir, CancellationToken cancellationToken = default)
    {
        ExigirAbierto();
        if (!EsRutaValida(ruta) || hashes.ContainsKey(ruta))
            throw new ArgumentException("Ruta de archivo no válida o duplicada.", nameof(ruta));

        cancellationToken.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await archivo.PutNextEntryAsync(NuevaEntrada(ruta), cancellationToken);
        await using (var salida = new FlujoConHash(archivo, hash))
            await escribir(salida);
        await archivo.CloseEntryAsync(cancellationToken);
        hashes.Add(ruta, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    public void RegistrarRecuento(string entidad, long recuento)
    {
        ExigirAbierto();
        if (string.IsNullOrWhiteSpace(entidad) || recuento < 0) throw new ArgumentException("Recuento no válido.");
        recuentos.Add(entidad, recuento);
    }

    /// <summary>Documento con fila viva cuyo binario no está en el almacén: consta en el manifiesto, no se calla.</summary>
    public void RegistrarDocumentoSinFichero(Guid documentoId)
    {
        ExigirAbierto();
        documentosSinFichero.Add(documentoId);
    }

    /// <summary>
    /// Lo que la exportación sabe que deja fuera, con su motivo: el manifiesto no presenta como
    /// completo lo que no lo es.
    /// </summary>
    public void RegistrarNoIncluido(string que, string motivo)
    {
        ExigirAbierto();
        if (string.IsNullOrWhiteSpace(que) || string.IsNullOrWhiteSpace(motivo)) throw new ArgumentException("Exclusión sin nombre o sin motivo.");
        noIncluido.Add(que, motivo);
    }

    public async Task TerminarAsync(CancellationToken cancellationToken = default)
    {
        ExigirAbierto();
        terminado = true;
        await archivo.PutNextEntryAsync(NuevaEntrada(RutaManifiesto), cancellationToken);
        await JsonSerializer.SerializeAsync(archivo, new
        {
            fechaUtc = reloj.GetUtcNow(),
            tenantId,
            recuentos,
            sha256 = hashes,
            documentosSinFichero,
            noIncluido,
        }, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
        await archivo.CloseEntryAsync(cancellationToken);
    }

    public void Dispose() => archivo.Dispose();

    /// <summary>
    /// Ruta relativa con «/», sin segmentos vacíos, «.» ni «..», sin «\» ni «:» y distinta del
    /// manifiesto: ningún nombre que venga de los datos puede salirse de la carpeta al descomprimir.
    /// </summary>
    public static bool EsRutaValida(string ruta) =>
        !string.IsNullOrWhiteSpace(ruta) && ruta != RutaManifiesto && !ruta.StartsWith('/') &&
        !ruta.Contains('\\') && !ruta.Contains(':') &&
        ruta.Split('/').All(segmento => segmento is not ("" or "." or ".."));

    private ZipEntry NuevaEntrada(string ruta) => new(ruta)
    {
        AESKeySize = BitsClaveAes,
        CompressionMethod = CompressionMethod.Deflated,
        DateTime = reloj.GetUtcNow().UtcDateTime,
    };

    private void ExigirAbierto()
    {
        if (terminado) throw new InvalidOperationException("El manifiesto ya está escrito.");
    }

    private sealed class FlujoConHash(Stream destino, IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => destino.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => destino.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.AppendData(buffer, offset, count);
            destino.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hash.AppendData(buffer);
            destino.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            hash.AppendData(buffer.Span);
            return destino.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
