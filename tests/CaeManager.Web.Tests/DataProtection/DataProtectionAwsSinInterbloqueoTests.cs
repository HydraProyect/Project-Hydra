using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.S3;
using Amazon.S3.Model;
using CaeManager.Infrastructure.DataProtection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests.DataProtection;

/// <summary>
/// Las interfaces de Data Protection son síncronas y el SDK de AWS solo es asíncrono.
/// Data Protection puede llamarlas desde el hilo de un circuito de Blazor Server, cuyo
/// SynchronizationContext ejecuta un elemento de trabajo cada vez. Estos tests reproducen
/// ese contexto con <see cref="ContextoDeUnSoloHilo"/>: lo que se publica en él no se
/// ejecuta mientras el hilo dueño esté bloqueado, igual que en el renderizador.
///
/// Los dobles del SDK esperan con un <c>await</c> que captura el contexto, como haría
/// cualquier código asíncrono escrito sin <c>ConfigureAwait(false)</c>: así el test no
/// depende de que el SDK real se porte bien.
/// </summary>
public class DataProtectionAwsSinInterbloqueoTests
{
    private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EsperaInterbloqueo = TimeSpan.FromSeconds(1);

    [Fact]
    public void El_contexto_de_un_solo_hilo_interbloquea_el_patron_anterior_de_GetResult_directo()
    {
        // Control del instrumento: sin esto, que los demás tests terminen no
        // demostraría nada, porque un contexto que no interbloquea nunca daría verde
        // también con el código anterior.
        var contexto = new ContextoDeUnSoloHilo();

        var resultado = EjecutarEnHiloConContexto(
            contexto,
            () => OperacionQueCapturaElContexto().GetAwaiter().GetResult(),
            EsperaInterbloqueo);

        resultado.TerminoAntesDeLiberar.Should().BeFalse(
            "GetResult() bloquea el único hilo del contexto y la continuación del await queda en su cola");
        contexto.Publicaciones.Should().BeGreaterThan(0);
        resultado.Error.Should().BeNull("al liberar la cola la operación termina con normalidad");

        static async Task<int> OperacionQueCapturaElContexto()
        {
            await Task.Delay(20);
            return 1;
        }
    }

    [Fact]
    public void GetAllElements_termina_bajo_un_contexto_de_un_solo_hilo_y_devuelve_las_claves_del_prefijo()
    {
        var s3 = new S3Falso();
        s3.Objetos["claves-dp/key-1.xml"] = "<key id=\"1\" />";
        s3.Objetos["claves-dp/key-2.xml"] = "<key id=\"2\" />";
        var repositorio = new S3XmlRepository(s3.Crear(), Opciones());
        var contexto = new ContextoDeUnSoloHilo();

        IReadOnlyCollection<XElement>? elementos = null;
        var resultado = EjecutarEnHiloConContexto(contexto, () => elementos = repositorio.GetAllElements(), EsperaMaxima);

        resultado.TerminoAntesDeLiberar.Should().BeTrue("la lectura no debe depender del hilo bloqueado");
        resultado.Error.Should().BeNull();
        contexto.Publicaciones.Should().Be(0, "ninguna continuación debe intentar volver al contexto del llamante");
        elementos!.Select(e => (string?)e.Attribute("id")).Should().BeEquivalentTo("1", "2");
        s3.PrefijosListados.Should().Equal("claves-dp/");
        s3.LlamadasSinTokenCancelable.Should().Be(0, "cada llamada al SDK debe recibir el token del límite");
    }

    [Fact]
    public void StoreElement_termina_bajo_un_contexto_de_un_solo_hilo_y_escribe_la_clave_con_el_mismo_nombre()
    {
        var s3 = new S3Falso();
        var repositorio = new S3XmlRepository(s3.Crear(), Opciones());
        var contexto = new ContextoDeUnSoloHilo();

        var resultado = EjecutarEnHiloConContexto(
            contexto,
            () => repositorio.StoreElement(new XElement("key", new XAttribute("id", "3")), "key-3"),
            EsperaMaxima);

        resultado.TerminoAntesDeLiberar.Should().BeTrue();
        resultado.Error.Should().BeNull();
        contexto.Publicaciones.Should().Be(0);
        s3.LlamadasSinTokenCancelable.Should().Be(0);
        s3.Objetos.Should().ContainKey("claves-dp/key-3.xml");
        XElement.Parse(s3.Objetos["claves-dp/key-3.xml"]).Attribute("id")!.Value.Should().Be("3");
    }

    [Fact]
    public void Encrypt_y_Decrypt_terminan_bajo_un_contexto_de_un_solo_hilo_y_conservan_la_clave()
    {
        var kms = new KmsFalso();
        var cliente = kms.Crear();
        var cifrador = new KmsXmlEncryptor(cliente, "alias/data-protection");
        var descifrador = new KmsXmlDecryptor(
            new ServiceCollection().AddSingleton(cliente).BuildServiceProvider());
        var original = new XElement("descriptor", new XElement("masterKey", "secreto"));

        var contextoCifrado = new ContextoDeUnSoloHilo();
        Microsoft.AspNetCore.DataProtection.XmlEncryption.EncryptedXmlInfo? cifrado = null;
        var resultadoCifrado = EjecutarEnHiloConContexto(
            contextoCifrado, () => cifrado = cifrador.Encrypt(original), EsperaMaxima);

        resultadoCifrado.TerminoAntesDeLiberar.Should().BeTrue("el cifrado no debe depender del hilo bloqueado");
        resultadoCifrado.Error.Should().BeNull();
        contextoCifrado.Publicaciones.Should().Be(0);
        kms.KeyIdsUsados.Should().Equal("alias/data-protection");

        var contextoDescifrado = new ContextoDeUnSoloHilo();
        XElement? descifrado = null;
        var resultadoDescifrado = EjecutarEnHiloConContexto(
            contextoDescifrado, () => descifrado = descifrador.Decrypt(cifrado!.EncryptedElement), EsperaMaxima);

        resultadoDescifrado.TerminoAntesDeLiberar.Should().BeTrue("el descifrado no debe depender del hilo bloqueado");
        resultadoDescifrado.Error.Should().BeNull();
        contextoDescifrado.Publicaciones.Should().Be(0);
        XNode.DeepEquals(descifrado, original).Should().BeTrue();
        kms.LlamadasSinTokenCancelable.Should().Be(0, "cifrado y descifrado deben recibir el token del límite");
    }

    [Fact]
    public void Un_descifrado_con_KMS_que_no_responde_se_corta_en_el_limite_y_cancela_la_peticion()
    {
        var kms = new KmsFalso { NoResponderAlDescifrar = true };
        var cliente = kms.Crear();
        var descifrador = new KmsXmlDecryptor(
            new ServiceCollection().AddSingleton(cliente).BuildServiceProvider(),
            TimeSpan.FromMilliseconds(300));

        var accion = () => descifrador.Decrypt(new XElement(KmsXmlEncryptor.NombreElemento, Convert.ToBase64String([1, 2, 3])));

        accion.Should().Throw<TimeoutException>().WithMessage("*KMS*");
        SpinWait.SpinUntil(() => kms.TokenCancelado, EsperaMaxima).Should().BeTrue();
    }

    [Fact]
    public void Un_TimeoutException_de_la_propia_operacion_no_se_reetiqueta_como_limite_vencido()
    {
        var propia = new TimeoutException("del SDK");

        var accion = () => LlamadaSincronaAws.Ejecutar<int>(_ => throw propia, EsperaMaxima, "La operación de prueba");

        accion.Should().Throw<TimeoutException>().Which.Should().BeSameAs(propia);
    }

    [Fact]
    public void Una_excepcion_de_la_operacion_llega_sin_envolver_en_AggregateException()
    {
        var accion = () => LlamadaSincronaAws.Ejecutar(
            async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("acceso denegado por KMS");
            },
            EsperaMaxima,
            "La operación de prueba");

        accion.Should().Throw<InvalidOperationException>().WithMessage("acceso denegado por KMS");
    }

    [Fact]
    public void Una_llamada_a_S3_que_no_responde_se_corta_en_el_limite_y_cancela_la_peticion()
    {
        var s3 = new S3Falso { NoResponder = true };
        var limite = TimeSpan.FromMilliseconds(300);
        var repositorio = new S3XmlRepository(s3.Crear(), Opciones(), limite);

        var cronometro = Stopwatch.StartNew();
        var accion = () => repositorio.GetAllElements();

        accion.Should().Throw<TimeoutException>().WithMessage("*S3*");
        cronometro.Elapsed.Should().BeLessThan(EsperaMaxima);
        // La cancelación llega al doble en el pool, en paralelo con la excepción.
        SpinWait.SpinUntil(() => s3.TokenCancelado, EsperaMaxima).Should().BeTrue(
            "el SDK recibe el token y la petición se cancela, no solo se deja de esperar");
    }

    [Fact]
    public void Una_llamada_a_KMS_que_ignora_la_cancelacion_tambien_se_corta_en_el_limite()
    {
        var kms = new KmsFalso { NoResponderNiAtenderCancelacion = true };
        var cifrador = new KmsXmlEncryptor(kms.Crear(), "alias/data-protection", TimeSpan.FromMilliseconds(300));

        var cronometro = Stopwatch.StartNew();
        var accion = () => cifrador.Encrypt(new XElement("descriptor"));

        accion.Should().Throw<TimeoutException>().WithMessage("*KMS*");
        cronometro.Elapsed.Should().BeLessThan(EsperaMaxima);
    }

    private static DataProtectionS3Options Opciones() => new()
    {
        BucketName = "bucket-pruebas",
        Prefijo = "claves-dp/"
    };

    private sealed record ResultadoEnHilo(bool TerminoAntesDeLiberar, Exception? Error);

    /// <summary>
    /// Ejecuta <paramref name="accion"/> en un hilo propio que tiene instalado
    /// <paramref name="contexto"/>. Si no termina en <paramref name="espera"/>, libera la
    /// cola del contexto para que el hilo pueda acabar y no quede colgado en el proceso
    /// de tests.
    /// </summary>
    private static ResultadoEnHilo EjecutarEnHiloConContexto(ContextoDeUnSoloHilo contexto, Action accion, TimeSpan espera)
    {
        Exception? error = null;
        var terminado = new ManualResetEventSlim();
        var hilo = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(contexto);
            try
            {
                accion();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                terminado.Set();
            }
        })
        {
            IsBackground = true,
            Name = "hilo-del-circuito-simulado"
        };

        hilo.Start();
        var terminoAntesDeLiberar = terminado.Wait(espera);
        if (!terminoAntesDeLiberar)
        {
            contexto.Liberar();
            terminado.Wait(EsperaMaxima).Should().BeTrue("al liberar la cola el hilo debe poder terminar");
        }

        return new ResultadoEnHilo(terminoAntesDeLiberar, error);
    }

    /// <summary>
    /// SynchronizationContext de un único hilo lógico, como el del renderizador de
    /// Blazor Server: lo publicado espera en cola a que el elemento en curso termine.
    /// Aquí el elemento en curso es la llamada bloqueante, así que la cola solo se
    /// vacía cuando el test llama a <see cref="Liberar"/>.
    /// </summary>
    private sealed class ContextoDeUnSoloHilo : SynchronizationContext
    {
        private readonly object _cerrojo = new();
        private readonly Queue<(SendOrPostCallback Callback, object? Estado)> _pendientes = new();
        private bool _liberado;
        private int _publicaciones;

        public int Publicaciones => Volatile.Read(ref _publicaciones);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _publicaciones);
            lock (_cerrojo)
            {
                if (!_liberado)
                {
                    _pendientes.Enqueue((d, state));
                    return;
                }
            }

            ThreadPool.QueueUserWorkItem(_ => d(state));
        }

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException("El renderizador no ejecuta trabajo síncrono ajeno mientras está ocupado.");

        public override SynchronizationContext CreateCopy() => this;

        public void Liberar()
        {
            List<(SendOrPostCallback Callback, object? Estado)> pendientes;
            lock (_cerrojo)
            {
                _liberado = true;
                pendientes = [.. _pendientes];
                _pendientes.Clear();
            }

            foreach (var (callback, estado) in pendientes)
            {
                ThreadPool.QueueUserWorkItem(_ => callback(estado));
            }
        }
    }

    /// <summary>Doble de un interfaz del SDK: solo responde a los métodos que se configuran.</summary>
    public class DobleAws<T> : DispatchProxy where T : class
    {
        private Func<MethodInfo, object?[], object?> _manejador = (_, _) => null;

        public static T Crear(Func<MethodInfo, object?[], object?> manejador)
        {
            var proxy = Create<T, DobleAws<T>>();
            ((DobleAws<T>)(object)proxy)._manejador = manejador;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            _manejador(targetMethod!, args ?? []);
    }

    private sealed class S3Falso
    {
        public ConcurrentDictionary<string, string> Objetos { get; } = new();
        public ConcurrentQueue<string> PrefijosListados { get; } = new();
        public bool NoResponder { get; init; }
        public bool TokenCancelado { get; private set; }
        public int LlamadasSinTokenCancelable => Volatile.Read(ref _llamadasSinTokenCancelable);
        private int _llamadasSinTokenCancelable;

        private CancellationToken Anotar(CancellationToken token)
        {
            if (!token.CanBeCanceled)
            {
                Interlocked.Increment(ref _llamadasSinTokenCancelable);
            }

            return token;
        }

        public IAmazonS3 Crear() => DobleAws<IAmazonS3>.Crear((metodo, argumentos) =>
            (metodo.Name, argumentos) switch
            {
                ("ListObjectsV2Async", [ListObjectsV2Request peticion, CancellationToken token]) => ListarAsync(peticion, Anotar(token)),
                ("GetObjectAsync", [string bucket, string clave, CancellationToken token]) => LeerAsync(bucket, clave, Anotar(token)),
                ("PutObjectAsync", [PutObjectRequest peticion, CancellationToken token]) => EscribirAsync(peticion, Anotar(token)),
                _ => throw new NotSupportedException($"El doble de S3 no implementa {metodo}.")
            });

        private async Task<ListObjectsV2Response> ListarAsync(ListObjectsV2Request peticion, CancellationToken token)
        {
            peticion.BucketName.Should().Be("bucket-pruebas");
            PrefijosListados.Enqueue(peticion.Prefix);
            if (NoResponder)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    TokenCancelado = true;
                    throw;
                }
            }

            await Task.Delay(20, token);
            return new ListObjectsV2Response
            {
                S3Objects = Objetos.Keys
                    .Where(k => k.StartsWith(peticion.Prefix, StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal)
                    .Select(k => new S3Object { Key = k })
                    .ToList()
            };
        }

        private async Task<GetObjectResponse> LeerAsync(string bucket, string clave, CancellationToken token)
        {
            bucket.Should().Be("bucket-pruebas");
            await Task.Delay(20, token);
            return new GetObjectResponse
            {
                ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes(Objetos[clave]))
            };
        }

        private async Task<PutObjectResponse> EscribirAsync(PutObjectRequest peticion, CancellationToken token)
        {
            peticion.BucketName.Should().Be("bucket-pruebas");
            await Task.Delay(20, token);
            Objetos[peticion.Key] = peticion.ContentBody;
            return new PutObjectResponse();
        }
    }

    private sealed class KmsFalso
    {
        private const string Prefijo = "cifrado:";

        public ConcurrentQueue<string> KeyIdsUsados { get; } = new();
        public bool NoResponderNiAtenderCancelacion { get; init; }
        public bool NoResponderAlDescifrar { get; init; }
        public bool TokenCancelado { get; private set; }
        public int LlamadasSinTokenCancelable => Volatile.Read(ref _llamadasSinTokenCancelable);
        private int _llamadasSinTokenCancelable;

        private CancellationToken Anotar(CancellationToken token)
        {
            if (!token.CanBeCanceled)
            {
                Interlocked.Increment(ref _llamadasSinTokenCancelable);
            }

            return token;
        }

        public IAmazonKeyManagementService Crear() => DobleAws<IAmazonKeyManagementService>.Crear((metodo, argumentos) =>
            (metodo.Name, argumentos) switch
            {
                ("EncryptAsync", [EncryptRequest peticion, CancellationToken token]) => CifrarAsync(peticion, Anotar(token)),
                ("DecryptAsync", [DecryptRequest peticion, CancellationToken token]) => DescifrarAsync(peticion, Anotar(token)),
                _ => throw new NotSupportedException($"El doble de KMS no implementa {metodo}.")
            });

        // El token se recibe pero no se atiende a propósito: modela una capa que
        // ignora la cancelación, para probar la espera acotada.
        private async Task<EncryptResponse> CifrarAsync(EncryptRequest peticion, CancellationToken _)
        {
            KeyIdsUsados.Enqueue(peticion.KeyId);
            if (NoResponderNiAtenderCancelacion)
            {
                await new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            }

            await Task.Delay(20);
            var textoPlano = Encoding.UTF8.GetString(peticion.Plaintext.ToArray());
            return new EncryptResponse
            {
                CiphertextBlob = new MemoryStream(Encoding.UTF8.GetBytes(Prefijo + textoPlano))
            };
        }

        private async Task<DecryptResponse> DescifrarAsync(DecryptRequest peticion, CancellationToken token)
        {
            if (NoResponderAlDescifrar)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    TokenCancelado = true;
                    throw;
                }
            }

            await Task.Delay(20, token);
            var cifrado = Encoding.UTF8.GetString(peticion.CiphertextBlob.ToArray());
            cifrado.Should().StartWith(Prefijo);
            return new DecryptResponse
            {
                Plaintext = new MemoryStream(Encoding.UTF8.GetBytes(cifrado[Prefijo.Length..]))
            };
        }
    }
}
