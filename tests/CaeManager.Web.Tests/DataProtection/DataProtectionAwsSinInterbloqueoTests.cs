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
    // Cota superior de lo que debe terminar: holgada para no fallar bajo carga, y
    // menor que el límite por defecto de 30 s para que un interbloqueo se vea como tal
    // y no como un TimeoutException del límite.
    private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(20);

    // Solo se usa para afirmar que algo NO terminó (el control del interbloqueo): la
    // carga no puede convertir un interbloqueo en un verde.
    private static readonly TimeSpan EsperaInterbloqueo = TimeSpan.FromSeconds(1);

    // Límite de las pruebas que esperan un vencimiento. Si el pool no arranca la
    // operación dentro de él, el puente ya no la invoca; los tests que necesitan que el
    // doble se haya invocado lo comprueban de forma explícita para que, bajo una carga
    // extrema, el fallo diga que la prueba no fue concluyente y no parezca un defecto.
    private static readonly TimeSpan LimiteCorto = TimeSpan.FromSeconds(1);
    private const string SinArrancar = "la prueba solo es concluyente si el pool arrancó la operación antes del límite";

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
        kms.TextosPlanosRecibidos.Should().ContainSingle()
            .Which.ToArray().Should().OnlyContain(b => b == 0, "el texto plano enviado a KMS se borra al salir de Encrypt");
        kms.TextosPlanosDevueltos.Should().ContainSingle()
            .Which.ToArray().Should().OnlyContain(b => b == 0, "el texto plano devuelto por KMS se borra al salir de Decrypt");
    }

    [Fact]
    public void GetAllElements_aplica_el_limite_a_cada_peticion_y_no_a_la_lectura_entera()
    {
        // Cuatro peticiones (listado y tres descargas) que responden bien, cada una
        // muy por debajo del límite, pero cuya suma lo supera.
        var limite = TimeSpan.FromSeconds(2);
        var s3 = new S3Falso { RetrasoPorPeticion = TimeSpan.FromMilliseconds(700) };
        s3.Objetos["claves-dp/key-1.xml"] = "<key id=\"1\" />";
        s3.Objetos["claves-dp/key-2.xml"] = "<key id=\"2\" />";
        s3.Objetos["claves-dp/key-3.xml"] = "<key id=\"3\" />";
        var repositorio = new S3XmlRepository(s3.Crear(), Opciones(), limite);

        var cronometro = Stopwatch.StartNew();
        var elementos = repositorio.GetAllElements();
        cronometro.Stop();

        cronometro.Elapsed.Should().BeGreaterThan(limite, "la latencia acumulada supera el límite de una petición");
        elementos.Select(e => (string?)e.Attribute("id")).Should().Equal("1", "2", "3");
        s3.TokensRecibidos.Should().HaveCount(4).And.OnlyHaveUniqueItems(
            "cada petición al SDK tiene su propio límite y su propio token");
    }

    [Fact]
    public void GetAllElements_trata_S3Objects_nulo_como_un_anillo_vacio()
    {
        var s3 = new S3Falso { ListadoSinS3Objects = true };
        var repositorio = new S3XmlRepository(s3.Crear(), Opciones());

        repositorio.GetAllElements().Should().BeEmpty();
        s3.PrefijosListados.Should().Equal("claves-dp/");
    }

    [Fact]
    public void Una_escritura_que_S3_completa_tras_el_limite_se_da_por_vencida_aunque_acabe_ocurriendo()
    {
        // Documenta la semántica: vencer el límite no deshace una petición ya enviada.
        var s3 = new S3Falso { EscrituraRetenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var repositorio = new S3XmlRepository(s3.Crear(), Opciones(), LimiteCorto);

        var accion = () => repositorio.StoreElement(new XElement("key", new XAttribute("id", "4")), "key-4");

        accion.Should().Throw<TimeoutException>().WithMessage("*S3*");
        s3.EscrituraIniciada.IsSet.Should().BeTrue(SinArrancar);
        s3.Objetos.Should().NotContainKey("claves-dp/key-4.xml");

        s3.EscrituraRetenida.SetResult();

        s3.EscrituraTerminada.Wait(EsperaMaxima).Should().BeTrue();
        XElement.Parse(s3.Objetos["claves-dp/key-4.xml"]).Attribute("id")!.Value.Should().Be(
            "4", "la escritura tardía llega íntegra: la clave es válida aunque Data Protection genere otra");
    }

    [Fact]
    public void La_excepcion_de_una_escritura_que_falla_tras_el_limite_queda_observada()
    {
        var mensaje = $"fallo tardío {Guid.NewGuid():N}";
        var noObservadas = new ConcurrentQueue<Exception>();
        void AlNoObservada(object? _, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x.Message == mensaje))
            {
                noObservadas.Enqueue(e.Exception);
            }
        }

        TaskScheduler.UnobservedTaskException += AlNoObservada;
        try
        {
            var s3 = new S3Falso
            {
                EscrituraRetenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                FalloDeLaEscrituraRetenida = mensaje
            };
            var repositorio = new S3XmlRepository(s3.Crear(), Opciones(), LimiteCorto);

            var accion = () => repositorio.StoreElement(new XElement("key"), "key-5");
            accion.Should().Throw<TimeoutException>();
            s3.EscrituraIniciada.IsSet.Should().BeTrue(SinArrancar);

            s3.EscrituraRetenida.SetResult();
            s3.EscrituraTerminada.Wait(EsperaMaxima).Should().BeTrue();

            // Una tarea con excepción sin observar solo lo avisa al finalizarse.
            for (var i = 0; i < 5; i++)
            {
                Thread.Sleep(50);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            noObservadas.Should().BeEmpty("la excepción tardía se observa al abandonar la operación");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= AlNoObservada;
        }
    }

    [Fact]
    public void Un_descifrado_que_llega_tras_el_limite_borra_su_texto_plano()
    {
        var kms = new KmsFalso { DescifradoRetenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cliente = kms.Crear();
        var descifrador = new KmsXmlDecryptor(new ServiceCollection().AddSingleton(cliente).BuildServiceProvider(), LimiteCorto);
        var cifrado = Convert.ToBase64String(Encoding.UTF8.GetBytes("cifrado:<descriptor>secreto</descriptor>"));

        var accion = () => descifrador.Decrypt(new XElement(KmsXmlEncryptor.NombreElemento, cifrado));
        accion.Should().Throw<TimeoutException>().WithMessage("*KMS*");
        kms.DescifradoIniciado.IsSet.Should().BeTrue(SinArrancar);

        kms.DescifradoRetenido.SetResult();

        SpinWait.SpinUntil(
                () => kms.TextosPlanosDevueltos.TryPeek(out var flujo) && flujo.ToArray().All(b => b == 0),
                EsperaMaxima)
            .Should().BeTrue("nadie usará la respuesta tardía y su texto plano no debe quedar en memoria");
    }

    [Fact]
    public void Un_cifrado_que_vence_el_limite_borra_el_texto_plano_que_retiene_la_peticion()
    {
        var kms = new KmsFalso { NoResponderNiAtenderCancelacion = true };
        var cifrador = new KmsXmlEncryptor(kms.Crear(), "alias/data-protection", LimiteCorto);

        var accion = () => cifrador.Encrypt(new XElement("descriptor", new XElement("masterKey", "secreto")));

        accion.Should().Throw<TimeoutException>();
        kms.TextosPlanosRecibidos.Should().ContainSingle(SinArrancar)
            .Which.ToArray().Should().OnlyContain(b => b == 0, "la petición abandonada no retiene el texto plano");
    }

    [Fact]
    public void Una_operacion_que_el_pool_no_arranca_antes_del_limite_libera_al_llamante_y_ya_no_se_invoca()
    {
        // El planificador retiene el trabajo: modela un pool saturado que no arranca la
        // operación a tiempo. La espera no depende del pool, así que vence igual.
        var planificador = new PlanificadorRetenido();
        var invocaciones = 0;

        var cronometro = Stopwatch.StartNew();
        var accion = () => LlamadaSincronaAws.Ejecutar(
            _ =>
            {
                Interlocked.Increment(ref invocaciones);
                return Task.FromResult(1);
            },
            LimiteCorto,
            "La operación de prueba",
            descartarResultadoTardio: null,
            planificador);

        accion.Should().Throw<TimeoutException>();
        cronometro.Elapsed.Should().BeLessThan(EsperaMaxima);
        planificador.Encoladas.Should().Be(1);

        planificador.Liberar();

        Volatile.Read(ref invocaciones).Should().Be(0, "con el token ya cancelado no se llega a llamar al SDK");
    }

    [Fact]
    public void Los_callbacks_de_cancelacion_del_SDK_no_corren_en_el_hilo_del_llamante()
    {
        using var liberarCallback = new ManualResetEventSlim();
        var hiloLlamante = Environment.CurrentManagedThreadId;
        var hiloCallback = 0;
        var callbackTerminado = false;
        var iniciada = false;

        var accion = () => LlamadaSincronaAws.Ejecutar<int>(
            token =>
            {
                Volatile.Write(ref iniciada, true);
                // Un callback de cancelación que bloquea, como podría registrar una capa
                // del SDK: si corriera en el hilo del llamante, lo retendría.
                token.Register(() =>
                {
                    Volatile.Write(ref hiloCallback, Environment.CurrentManagedThreadId);
                    liberarCallback.Wait(EsperaMaxima);
                    Volatile.Write(ref callbackTerminado, true);
                });
                return new TaskCompletionSource<int>().Task;
            },
            LimiteCorto,
            "La operación de prueba");

        accion.Should().Throw<TimeoutException>();
        Volatile.Read(ref iniciada).Should().BeTrue(SinArrancar);
        Volatile.Read(ref callbackTerminado).Should().BeFalse(
            "el llamante recibe el TimeoutException sin esperar al callback de cancelación");

        liberarCallback.Set();
        SpinWait.SpinUntil(() => Volatile.Read(ref callbackTerminado), EsperaMaxima).Should().BeTrue();
        Volatile.Read(ref hiloCallback).Should().NotBe(hiloLlamante);
    }

    [Fact]
    public void Un_descifrado_con_KMS_que_no_responde_se_corta_en_el_limite_y_cancela_la_peticion()
    {
        var kms = new KmsFalso { NoResponderAlDescifrar = true };
        var cliente = kms.Crear();
        var descifrador = new KmsXmlDecryptor(
            new ServiceCollection().AddSingleton(cliente).BuildServiceProvider(),
            LimiteCorto);

        var accion = () => descifrador.Decrypt(new XElement(KmsXmlEncryptor.NombreElemento, Convert.ToBase64String([1, 2, 3])));

        accion.Should().Throw<TimeoutException>().WithMessage("*KMS*");
        kms.DescifradoIniciado.IsSet.Should().BeTrue(SinArrancar);
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
        var repositorio = new S3XmlRepository(s3.Crear(), Opciones(), LimiteCorto);

        var cronometro = Stopwatch.StartNew();
        var accion = () => repositorio.GetAllElements();

        accion.Should().Throw<TimeoutException>().WithMessage("*S3*");
        s3.PrefijosListados.Should().NotBeEmpty(SinArrancar);
        cronometro.Elapsed.Should().BeLessThan(EsperaMaxima);
        // La cancelación llega al doble en el pool, en paralelo con la excepción.
        SpinWait.SpinUntil(() => s3.TokenCancelado, EsperaMaxima).Should().BeTrue(
            "el SDK recibe el token y la petición se cancela, no solo se deja de esperar");
    }

    [Fact]
    public void Una_llamada_a_KMS_que_ignora_la_cancelacion_tambien_se_corta_en_el_limite()
    {
        var kms = new KmsFalso { NoResponderNiAtenderCancelacion = true };
        var cifrador = new KmsXmlEncryptor(kms.Crear(), "alias/data-protection", LimiteCorto);

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

    /// <summary>
    /// Planificador que no ejecuta nada hasta <see cref="Liberar"/>: modela un pool que,
    /// saturado, no llega a arrancar el trabajo encolado.
    /// </summary>
    private sealed class PlanificadorRetenido : TaskScheduler
    {
        private readonly ConcurrentQueue<Task> _tareas = new();

        public int Encoladas => _tareas.Count;

        public void Liberar()
        {
            while (_tareas.TryDequeue(out var tarea))
            {
                TryExecuteTask(tarea);
            }
        }

        protected override void QueueTask(Task task) => _tareas.Enqueue(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => _tareas.ToArray();
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
        public ConcurrentQueue<CancellationToken> TokensRecibidos { get; } = new();
        public bool NoResponder { get; init; }
        public bool ListadoSinS3Objects { get; init; }
        public TimeSpan RetrasoPorPeticion { get; init; } = TimeSpan.FromMilliseconds(20);

        /// <summary>
        /// Si se fija, la escritura espera a esta puerta ignorando el token: modela una
        /// petición ya enviada que S3 completa después del límite.
        /// </summary>
        public TaskCompletionSource? EscrituraRetenida { get; init; }

        /// <summary>Si se fija, la escritura retenida falla con este mensaje al liberarse.</summary>
        public string? FalloDeLaEscrituraRetenida { get; init; }

        public ManualResetEventSlim EscrituraIniciada { get; } = new();
        public ManualResetEventSlim EscrituraTerminada { get; } = new();
        public bool TokenCancelado { get; private set; }
        public int LlamadasSinTokenCancelable => Volatile.Read(ref _llamadasSinTokenCancelable);
        private int _llamadasSinTokenCancelable;

        private CancellationToken Anotar(CancellationToken token)
        {
            TokensRecibidos.Enqueue(token);
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

            await Task.Delay(RetrasoPorPeticion, token);
            return new ListObjectsV2Response
            {
                // Como AWSSDK.S3 4.x con InitializeCollections a false cuando el
                // servicio no devuelve objetos.
                S3Objects = ListadoSinS3Objects
                    ? null
                    : Objetos.Keys
                        .Where(k => k.StartsWith(peticion.Prefix, StringComparison.Ordinal))
                        .Order(StringComparer.Ordinal)
                        .Select(k => new S3Object { Key = k })
                        .ToList()
            };
        }

        private async Task<GetObjectResponse> LeerAsync(string bucket, string clave, CancellationToken token)
        {
            bucket.Should().Be("bucket-pruebas");
            await Task.Delay(RetrasoPorPeticion, token);
            return new GetObjectResponse
            {
                ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes(Objetos[clave]))
            };
        }

        private async Task<PutObjectResponse> EscribirAsync(PutObjectRequest peticion, CancellationToken token)
        {
            peticion.BucketName.Should().Be("bucket-pruebas");
            EscrituraIniciada.Set();
            try
            {
                if (EscrituraRetenida is not null)
                {
                    await EscrituraRetenida.Task;
                    if (FalloDeLaEscrituraRetenida is not null)
                    {
                        throw new InvalidOperationException(FalloDeLaEscrituraRetenida);
                    }
                }
                else
                {
                    await Task.Delay(RetrasoPorPeticion, token);
                }

                Objetos[peticion.Key] = peticion.ContentBody;
                return new PutObjectResponse();
            }
            finally
            {
                EscrituraTerminada.Set();
            }
        }
    }

    private sealed class KmsFalso
    {
        private const string Prefijo = "cifrado:";

        public ConcurrentQueue<string> KeyIdsUsados { get; } = new();
        public ConcurrentQueue<MemoryStream> TextosPlanosRecibidos { get; } = new();
        public ConcurrentQueue<MemoryStream> TextosPlanosDevueltos { get; } = new();
        public bool NoResponderNiAtenderCancelacion { get; init; }
        public bool NoResponderAlDescifrar { get; init; }

        /// <summary>
        /// Si se fija, el descifrado espera a esta puerta ignorando el token y luego
        /// responde: modela una respuesta que llega después del límite.
        /// </summary>
        public TaskCompletionSource? DescifradoRetenido { get; init; }

        public ManualResetEventSlim DescifradoIniciado { get; } = new();
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
            TextosPlanosRecibidos.Enqueue(peticion.Plaintext);
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
            DescifradoIniciado.Set();
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

            if (DescifradoRetenido is not null)
            {
                await DescifradoRetenido.Task;
            }
            else
            {
                await Task.Delay(20, token);
            }

            var cifrado = Encoding.UTF8.GetString(peticion.CiphertextBlob.ToArray());
            cifrado.Should().StartWith(Prefijo);
            // Como el SDK: un MemoryStream escribible sobre los bytes recibidos.
            var textoPlano = new MemoryStream(Encoding.UTF8.GetBytes(cifrado[Prefijo.Length..]));
            TextosPlanosDevueltos.Enqueue(textoPlano);
            return new DecryptResponse { Plaintext = textoPlano };
        }
    }
}
