using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace CaeManager.Infrastructure.DataProtection;

/// <summary>
/// Un objeto S3 por clave de Data Protection, bajo <see cref="DataProtectionS3Options.Prefijo"/>
/// — puede compartir bucket con otros usos, siempre bajo su propio prefijo.
///
/// <see cref="IXmlRepository"/> es una interfaz síncrona (así la definió
/// ASP.NET Core, pensada originalmente para disco local) y el SDK de AWS solo ofrece
/// métodos asíncronos en net8.0. El puente lo hace <see cref="LlamadaSincronaAws"/>:
/// ejecuta la llamada fuera del SynchronizationContext del llamante y con un límite de
/// tiempo, porque Data Protection puede llegar aquí desde el hilo de un circuito de
/// Blazor (carga en frío o recarga forzada del anillo), y un <c>GetAwaiter().GetResult()</c>
/// directo sobre un <c>await</c> que captura ese contexto se interbloquea.
/// </summary>
public class S3XmlRepository : IXmlRepository
{
    private readonly IAmazonS3 _cliente;
    private readonly DataProtectionS3Options _opciones;
    private readonly TimeSpan _limite;
    private readonly TaskScheduler _planificador;

    public S3XmlRepository(IAmazonS3 cliente, DataProtectionS3Options opciones)
        : this(cliente, opciones, LlamadaSincronaAws.LimitePorDefecto)
    {
    }

    internal S3XmlRepository(IAmazonS3 cliente, DataProtectionS3Options opciones, TimeSpan limite)
        : this(cliente, opciones, limite, TaskScheduler.Default)
    {
    }

    /// <param name="planificador">Solo los tests pasan otro que <see cref="TaskScheduler.Default"/>.</param>
    internal S3XmlRepository(IAmazonS3 cliente, DataProtectionS3Options opciones, TimeSpan limite, TaskScheduler planificador)
    {
        _cliente = cliente;
        _opciones = opciones;
        _limite = limite;
        _planificador = planificador;
    }

    /// <remarks>
    /// El límite se aplica a cada petición (el listado y cada descarga), no a la lectura
    /// entera: un anillo grande o una latencia que se acumula entre peticiones que
    /// responden bien no debe impedir cargarlo. El presupuesto total es, por tanto,
    /// (1 + objetos) × límite como mucho; el listado lee una sola página (hasta 1.000
    /// objetos, como antes de existir el límite), y un anillo de Data Protection tiene
    /// del orden de una clave por cada 90 días.
    /// </remarks>
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var claves = LlamadaSincronaAws.Ejecutar(
            ListarClavesAsync,
            _limite,
            "El listado de las claves de Data Protection en S3",
            descartarResultadoTardio: null,
            liberarEntrada: null,
            _planificador);

        var elementos = new List<XElement>(claves.Count);
        foreach (var clave in claves)
        {
            var contenido = LlamadaSincronaAws.Ejecutar(
                token => LeerObjetoAsync(clave, token),
                _limite,
                "La lectura de una clave de Data Protection en S3",
                descartarResultadoTardio: null,
                liberarEntrada: null,
                _planificador);
            elementos.Add(XElement.Parse(contenido));
        }

        return elementos;
    }

    /// <remarks>
    /// El XML puede llevar la clave maestra sin cifrar (cuando no hay KMS ni certificado,
    /// en los entornos que lo permiten), así que viaja en un <see cref="CuerpoRevocable"/>
    /// que se revoca y se borra al volver, termine la escritura o venza el límite: el búfer
    /// propio no sobrevive a esta llamada aunque la escritura siga en curso (las copias que
    /// ya hubieran hecho el SDK o la pila HTTP quedan fuera, ver abajo). Si el SDK aún
    /// no había leído el cuerpo entero, su siguiente lectura falla en vez de entregar ceros,
    /// así que S3 no puede guardar una clave corrupta; la escritura se pierde, y Data
    /// Protection, que ya la dio por fallida, generará otra clave.
    /// Lo que no se puede borrar: el <see cref="string"/> que produce
    /// <see cref="XNode.ToString()"/> (inmutable; vive lo que tarde el recolector, y no lo
    /// retiene la escritura), el propio <see cref="XElement"/>, que es de Data Protection,
    /// y las copias que el SDK o la pila HTTP hagan al enviar.
    /// </remarks>
    public void StoreElement(XElement element, string friendlyName)
    {
        var clave = $"{_opciones.Prefijo}{friendlyName}.xml";
        // Los mismos bytes que enviaba ContentBody (UTF-8 sin BOM del mismo texto).
        var cuerpo = new CuerpoRevocable(Encoding.UTF8.GetBytes(element.ToString()));
        try
        {
            LlamadaSincronaAws.Ejecutar(
                token => AlmacenarAsync(clave, cuerpo, token),
                _limite,
                "La escritura de una clave de Data Protection en S3",
                liberarEntrada: null,
                _planificador);
        }
        finally
        {
            // También si venció el límite y la escritura sigue en curso: desde aquí el SDK
            // ya no puede leer el XML, solo fallar al intentarlo.
            cuerpo.Revocar();
        }
    }

    private async Task<IReadOnlyList<string>> ListarClavesAsync(CancellationToken token)
    {
        var respuestaListado = await _cliente.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _opciones.BucketName,
            Prefix = _opciones.Prefijo
        }, token).ConfigureAwait(false);

        // AWSSDK.S3 4.x deja S3Objects a null si el servicio no devuelve objetos
        // (AWSConfigs.InitializeCollections es false por defecto): un prefijo vacío
        // es un anillo vacío, no un error.
        return respuestaListado.S3Objects?.Select(objeto => objeto.Key).ToList() ?? [];
    }

    private async Task<string> LeerObjetoAsync(string clave, CancellationToken token)
    {
        using var respuestaObjeto = await _cliente
            .GetObjectAsync(_opciones.BucketName, clave, token)
            .ConfigureAwait(false);
        using var lector = new StreamReader(respuestaObjeto.ResponseStream);
        return await lector.ReadToEndAsync(token).ConfigureAwait(false);
    }

    private Task AlmacenarAsync(string clave, CuerpoRevocable cuerpo, CancellationToken token) =>
        // El SDK cierra el flujo al terminar (AutoCloseStream), lo que también lo revoca;
        // quien lo revoca siempre, termine o no la petición, es StoreElement.
        _cliente.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _opciones.BucketName,
            Key = clave,
            InputStream = cuerpo
        }, token);

    /// <summary>
    /// Cuerpo de una escritura en S3 que se puede revocar mientras el SDK aún lo lee. Tras
    /// <see cref="Revocar"/> sus bytes están a ceros, y leer, posicionarse o pedir la
    /// longitud lanza <see cref="ObjectDisposedException"/>: nunca entrega ceros. Lectura y
    /// revocación comparten un cerrojo, así que cada lectura copia bytes íntegros o falla,
    /// y lo que el SDK obtiene es siempre un prefijo del XML original.
    ///
    /// Por qué así S3 no puede guardar una clave corrupta: una petición cuyo cuerpo falla
    /// al leerse no se completa (la pila HTTP la aborta, y S3 no guarda un objeto con menos
    /// bytes de los que declara <c>Content-Length</c>), y un reintento del SDK, que vuelve
    /// a posicionar el flujo al principio, también falla. Lo que puede completarse tarde es
    /// una petición cuyo cuerpo ya se leyó entero, con el XML íntegro.
    ///
    /// No es un <see cref="MemoryStream"/> a propósito: este tiene caminos
    /// (<c>CopyTo</c>, <c>WriteTo</c>, <c>TryGetBuffer</c>) que leen su búfer sin pasar por
    /// <c>Read</c>; aquí todo lo que lee pasa por <see cref="Read(Span{byte})"/>.
    /// </summary>
    internal sealed class CuerpoRevocable : Stream
    {
        private readonly object _cerrojo = new();
        private readonly byte[] _bytes;
        private int _posicion;
        private bool _revocado;

        /// <param name="bytes">Se usan sin copia y se borran al revocar.</param>
        internal CuerpoRevocable(byte[] bytes) => _bytes = bytes;

        public override bool CanRead => !Volatile.Read(ref _revocado);

        public override bool CanSeek => !Volatile.Read(ref _revocado);

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                lock (_cerrojo)
                {
                    LanzarSiRevocado();
                    return _bytes.Length;
                }
            }
        }

        public override long Position
        {
            get
            {
                lock (_cerrojo)
                {
                    LanzarSiRevocado();
                    return _posicion;
                }
            }
            set => Seek(value, SeekOrigin.Begin);
        }

        /// <summary>Si los bytes están a ceros; para los tests.</summary>
        internal bool Borrado
        {
            get
            {
                lock (_cerrojo)
                {
                    return !_bytes.AsSpan().ContainsAnyExcept((byte)0);
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            lock (_cerrojo)
            {
                LanzarSiRevocado();
                var leidos = Math.Min(buffer.Length, _bytes.Length - _posicion);
                if (leidos <= 0)
                {
                    return 0;
                }

                _bytes.AsSpan(_posicion, leidos).CopyTo(buffer);
                _posicion += leidos;
                return leidos;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<int>(cancellationToken);
            }

            try
            {
                return ValueTask.FromResult(Read(buffer.Span));
            }
            catch (Exception ex)
            {
                return ValueTask.FromException<int>(ex);
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (_cerrojo)
            {
                LanzarSiRevocado();
                var destino = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _posicion + offset,
                    SeekOrigin.End => _bytes.Length + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin))
                };
                ArgumentOutOfRangeException.ThrowIfNegative(destino, nameof(offset));
                ArgumentOutOfRangeException.ThrowIfGreaterThan(destino, _bytes.Length, nameof(offset));
                _posicion = (int)destino;
                return destino;
            }
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>
        /// Borra los bytes y hace fallar toda lectura posterior. Idempotente. Solo espera a
        /// una lectura en curso, que es una copia en memoria de este mismo búfer.
        /// </summary>
        internal void Revocar()
        {
            lock (_cerrojo)
            {
                Volatile.Write(ref _revocado, true);
                CryptographicOperations.ZeroMemory(_bytes);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Revocar();
            }

            base.Dispose(disposing);
        }

        private void LanzarSiRevocado() => ObjectDisposedException.ThrowIf(_revocado, this);
    }
}
