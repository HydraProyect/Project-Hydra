using CaeManager.Application.Common;
using System.Net;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.FileStorage;

/// <summary>
/// Almacenamiento en S3, particionado por tenant exactamente igual que
/// <see cref="DiskFileStorageService"/> (misma clave
/// <c>{tenantId:N}/{guid:N}{extensión}</c>, docs/MULTITENANCY.md § 4.6) —
/// mismo contrato, mismo criterio de aislamiento (un identificador de otro
/// tenant se comporta como "no existe", nunca revela que pertenece a otro
/// tenant — mismo criterio anti-IDOR del Issue #18), distinto backend.
///
/// Scoped, no Singleton, por la misma razón que <see cref="DiskFileStorageService"/>:
/// depende de <see cref="ITenantActual"/>, que es scoped — inyectarlo en un
/// singleton sería una dependencia cautiva que capturaría el primer tenant
/// resuelto para siempre.
///
/// <b>Pendiente al integrar con P1 #12</b> (cifrado en reposo de
/// <see cref="DiskFileStorageService"/> con <c>IDataProtector</c>, trabajado
/// en paralelo en otra sesión sobre este mismo informe): esta clase se
/// escribió antes de ese cambio y todavía NO cifra el contenido antes de
/// subirlo a S3. Si P1-12 llega primero, este backend queda como el único
/// sin cifrar el dato más sensible del sistema (PDFs de reconocimientos
/// médicos, art. 9 RGPD) — aplicarle el mismo <c>IDataProtector</c>
/// (protector <c>"CaeManager.Archivos.v1"</c>, para que un archivo cifrado
/// en un backend se pueda leer si se cambia al otro) antes de dar por
/// cerrado ese punto.
/// </summary>
public class S3FileStorageService : IFileStorageService, IDisposable
{
    private readonly AmazonS3Client _cliente;
    private readonly string _bucket;
    private readonly ITenantActual _tenantActual;

    public S3FileStorageService(IOptions<AlmacenamientoS3Options> opciones, ITenantActual tenantActual)
    {
        var config = opciones.Value;
        _bucket = config.BucketName!;
        _tenantActual = tenantActual;
        _cliente = new AmazonS3Client(config.AccessKeyId, config.SecretAccessKey, RegionEndpoint.GetBySystemName(config.Region));
    }

    public async Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantActual.TenantId
            ?? throw new InvalidOperationException("No se puede guardar un archivo sin un tenant resuelto.");

        var extension = Path.GetExtension(nombreArchivoOriginal);
        var identificador = $"{CarpetaDeTenant(tenantId)}/{Guid.NewGuid():N}{extension}";

        // AutoCloseStream = false: igual que DiskFileStorageService no cierra
        // `contenido` (solo el fichero que abre él mismo), el contrato de
        // IFileStorageService es que el llamador conserva la propiedad del
        // stream de entrada.
        await _cliente.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = identificador,
            InputStream = contenido,
            AutoCloseStream = false,
        }, cancellationToken);

        return identificador;
    }

    public async Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default)
    {
        var clave = ResolverClaveSegura(identificador)
            ?? throw new FileNotFoundException("No encontramos el archivo solicitado.", identificador);

        try
        {
            var respuesta = await _cliente.GetObjectAsync(_bucket, clave, cancellationToken);

            // Devolver respuesta.ResponseStream a secas pierde la propiedad de
            // GetObjectResponse: quien recibe el stream lo desecha a él, no a
            // la respuesta, que es la que retiene la respuesta HTTP y su
            // conexión. Con suficientes descargas eso agota el pool del cliente
            // de S3. El envoltorio hace que desechar el stream —que es lo único
            // que el contrato de IFileStorageService le da al llamador— deseche
            // los dos.
            return new FlujoObjetoS3(respuesta);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException("No encontramos el archivo solicitado.", identificador, ex);
        }
    }

    public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default)
    {
        var clave = ResolverClaveSegura(identificador);

        // Misma resolución segura que al abrir: un identificador de otro
        // tenant no resuelve clave, así que la purga de un tenant nunca
        // puede borrar archivos de otro. DeleteObjectAsync sobre una clave
        // que ya no existe no falla en S3 — encaja solo con el contrato de
        // "no falla si el archivo ya no está".
        return clave is null ? Task.CompletedTask : _cliente.DeleteObjectAsync(_bucket, clave, cancellationToken);
    }

    private string? ResolverClaveSegura(string identificador)
    {
        if (_tenantActual.TenantId is not { } tenantId) return null;

        var partes = identificador.Split('/', 2);
        if (partes.Length != 2) return null;

        if (!string.Equals(partes[0], CarpetaDeTenant(tenantId), StringComparison.Ordinal))
            return null;

        // El nombre de archivo nunca debería llevar '/', pero si un
        // identificador manipulado lo trajera, se sanea igual que en Disk:
        // solo el segmento de nombre de archivo, nunca una ruta con niveles.
        var nombreArchivo = Path.GetFileName(partes[1]);
        return $"{partes[0]}/{nombreArchivo}";
    }

    private static string CarpetaDeTenant(Guid tenantId) => tenantId.ToString("N");

    public void Dispose() => _cliente.Dispose();

    /// <summary>
    /// Stream de lectura que, al desecharse, desecha también la
    /// <see cref="GetObjectResponse"/> de la que salió. Existe solo para que
    /// la propiedad de los dos recursos viaje junta: <c>IFileStorageService</c>
    /// entrega un <see cref="Stream"/> y nada más, así que es el stream el que
    /// tiene que cargar con la respuesta.
    /// </summary>
    private sealed class FlujoObjetoS3(GetObjectResponse respuesta) : Stream
    {
        private readonly Stream _interno = respuesta.ResponseStream;

        public override bool CanRead => _interno.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => respuesta.ContentLength;

        public override long Position
        {
            get => _interno.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _interno.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _interno.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _interno.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _interno.ReadAsync(buffer, offset, count, cancellationToken);

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            _interno.CopyToAsync(destination, bufferSize, cancellationToken);

        public override void Flush() => _interno.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // La respuesta desecha su propio ResponseStream; desechar los
                // dos por separado seria desechar el stream dos veces.
                respuesta.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            respuesta.Dispose();
            await base.DisposeAsync();
        }
    }
}
