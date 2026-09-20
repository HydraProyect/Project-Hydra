using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// Chequeo de legibilidad del certificado al arrancar (P44b, reserva de #739). Lo que adelanta no es
/// dejar de fallar en silencio —con ambas rutas informadas el fallo ya salía, en cada canje, como
/// <c>CertificadoNoLegible</c>— sino la SEÑAL: al arranque, que es cuando hay una persona mirando el
/// despliegue. Tres condiciones que estas pruebas fijan: solo legibilidad (no se lee ni un byte), nunca
/// fatal, y sin rutas ni contenido en el log.
/// </summary>
public sealed class Microsoft365LegibilidadDelCertificadoTests : IDisposable
{
    private const string MarcaDeRuta = "RUTA-QUE-NO-DEBE-SALIR";
    private const string RutaCertificado = "/run/secretos/" + MarcaDeRuta + "-cert.pem";
    private const string RutaClave = "/run/secretos/" + MarcaDeRuta + "-clave.pem";

    private readonly string _dir = Directory.CreateTempSubdirectory("m365-legibilidad-").FullName;

    public void Dispose()
    {
        // Un fichero con modo 000 no se borra en Unix si el directorio no admite escritura: se restaura antes.
        foreach (var fichero in Directory.GetFiles(_dir))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(fichero, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Directory.Delete(_dir, recursive: true);
    }

    private static Microsoft365GraphOptions ConCertificado(string certificado = RutaCertificado, string clave = RutaClave) => new()
    {
        ClientId = "id",
        UrlPublicaBase = "https://x",
        CertificadoRuta = certificado,
        ClavePrivadaRuta = clave,
    };

    private static AvisoConfiguracionMicrosoft365HostedService Crear(
        Microsoft365GraphOptions opciones, LoggerCapturador logger, Func<string, Stream>? abrir) =>
        new(Options.Create(opciones), logger, abrir);

    [Fact]
    public async Task Sin_permiso_de_lectura_en_la_clave_avisa_nombrando_la_opcion_y_sin_ruta_ni_excepcion()
    {
        var logger = new LoggerCapturador();
        // La excepción REAL de .NET nombra la ruta: si el aviso incluyera ex.Message, la marca saldría en el log.
        Stream Abrir(string ruta) => ruta == RutaClave
            ? throw new UnauthorizedAccessException($"Access to the path '{ruta}' is denied.")
            : new MemoryStream();

        await Crear(ConCertificado(), logger, Abrir).StartAsync(CancellationToken.None);

        var aviso = logger.Entradas.Should().ContainSingle().Which;
        aviso.Nivel.Should().Be(LogLevel.Warning);
        aviso.Texto.Should().Contain("ClavePrivadaRuta").And.Contain("permiso de lectura").And.Contain("CertificadoNoLegible");
        aviso.Texto.Should().NotContain(MarcaDeRuta).And.NotContain("Access to the path", "ni la ruta ni el mensaje de la excepción");
    }

    [Fact]
    public async Task Con_permiso_de_lectura_en_los_dos_ficheros_no_avisa_y_no_lee_ni_un_byte()
    {
        var logger = new LoggerCapturador();
        var espias = new List<EspiaDeFlujo>();
        Stream Abrir(string ruta)
        {
            var espia = new EspiaDeFlujo();
            espias.Add(espia);
            return espia;
        }

        await Crear(ConCertificado(), logger, Abrir).StartAsync(CancellationToken.None);

        logger.Entradas.Should().BeEmpty("con permiso no hay nada que avisar");
        espias.Should().HaveCount(2, "control positivo: los dos ficheros SÍ se abren");
        espias.Should().OnlyContain(e => !e.SeLeyo, "solo legibilidad: el certificado y la clave no se cargan en memoria al arrancar");
        espias.Should().OnlyContain(e => e.SeCerro, "abrir y cerrar: no se deja el fichero abierto");
    }

    [Fact]
    public async Task Un_certificado_inexistente_avisa_de_que_no_existe_nombrando_solo_la_opcion()
    {
        var logger = new LoggerCapturador();
        Stream Abrir(string ruta) => ruta == RutaCertificado
            ? throw new FileNotFoundException($"Could not find file '{ruta}'.")
            : new MemoryStream();

        await Crear(ConCertificado(), logger, Abrir).StartAsync(CancellationToken.None);

        var aviso = logger.Entradas.Should().ContainSingle().Which;
        aviso.Texto.Should().Contain("CertificadoRuta").And.Contain("no existe");
        aviso.Texto.Should().NotContain("ClavePrivadaRuta").And.NotContain(MarcaDeRuta);
    }

    [Fact]
    public async Task Los_dos_ficheros_ilegibles_dan_un_aviso_por_cada_opcion()
    {
        var logger = new LoggerCapturador();
        Stream Abrir(string ruta) => throw new IOException($"boom en {ruta}");

        await Crear(ConCertificado(), logger, Abrir).StartAsync(CancellationToken.None);

        logger.Entradas.Should().HaveCount(2);
        logger.Entradas.Should().Contain(e => e.Texto.Contains("CertificadoRuta") && e.Texto.Contains("no se pudo abrir"));
        logger.Entradas.Should().Contain(e => e.Texto.Contains("ClavePrivadaRuta") && e.Texto.Contains("no se pudo abrir"));
        logger.Entradas.Should().OnlyContain(e => !e.Texto.Contains(MarcaDeRuta) && !e.Texto.Contains("boom"));
    }

    [Fact]
    public async Task Nunca_es_fatal_ni_con_una_excepcion_inesperada_al_abrir_ni_con_unas_opciones_que_revientan()
    {
        var logger = new LoggerCapturador();

        var conAbrirQueReviente = Crear(ConCertificado(), logger, _ => throw new InvalidOperationException("inesperada " + MarcaDeRuta));
        var accion = () => conAbrirQueReviente.StartAsync(CancellationToken.None);
        await accion.Should().NotThrowAsync("un aviso de arranque no puede abortar el arranque");

        var conOpcionesQueRevienten = new AvisoConfiguracionMicrosoft365HostedService(new OpcionesQueRevientan(), logger);
        var accion2 = () => conOpcionesQueRevienten.StartAsync(CancellationToken.None);
        await accion2.Should().NotThrowAsync();

        logger.Entradas.Should().NotBeEmpty("se avisa, no se calla");
        logger.Entradas.Should().OnlyContain(e => !e.Texto.Contains(MarcaDeRuta));
    }

    [Fact]
    public async Task Con_ficheros_reales_uno_inexistente_avisa_y_uno_legible_no()
    {
        var legible = Path.Combine(_dir, "cert.pem");
        File.WriteAllText(legible, "contenido-que-no-se-lee");
        var inexistente = Path.Combine(_dir, "NO-EXISTE", "clave.pem");
        var logger = new LoggerCapturador();

        await Crear(ConCertificado(legible, inexistente), logger, abrir: null).StartAsync(CancellationToken.None);

        var aviso = logger.Entradas.Should().ContainSingle().Which;
        aviso.Texto.Should().Contain("ClavePrivadaRuta").And.Contain("no existe");
        aviso.Texto.Should().NotContain("NO-EXISTE").And.NotContain(_dir);
    }

    [Fact]
    public async Task Con_el_modo_del_fichero_a_cero_avisa_y_al_devolver_el_permiso_deja_de_avisar()
    {
        // La propiedad que motivó el chequeo, contra el sistema de ficheros REAL: un PEM sin permiso de
        // lectura para el proceso. Solo tiene sentido en Unix y sin ser root (root ignora los bits de
        // modo): en Windows o como root el caso no aplica y la prueba termina sin afirmar nada — la
        // cobertura real de este caso la da el CI en Linux.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;

        var cert = Path.Combine(_dir, "cert.pem");
        var clave = Path.Combine(_dir, "clave.pem");
        File.WriteAllText(cert, "x");
        File.WriteAllText(clave, "x");
        File.SetUnixFileMode(clave, UnixFileMode.None);

        var sinPermiso = new LoggerCapturador();
        await Crear(ConCertificado(cert, clave), sinPermiso, abrir: null).StartAsync(CancellationToken.None);
        sinPermiso.Entradas.Should().ContainSingle().Which.Texto.Should().Contain("ClavePrivadaRuta").And.Contain("permiso de lectura");

        File.SetUnixFileMode(clave, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var conPermiso = new LoggerCapturador();
        await Crear(ConCertificado(cert, clave), conPermiso, abrir: null).StartAsync(CancellationToken.None);
        conPermiso.Entradas.Should().BeEmpty();
    }

    [Fact]
    public void El_contenedor_de_dependencias_construye_el_servicio_con_el_parametro_opcional_sin_registrar()
    {
        // El tercer parámetro del constructor es opcional: si el contenedor no supiera resolverlo, la
        // aplicación real fallaría al arrancar aunque estas pruebas —que lo construyen a mano— pasaran.
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddSingleton(Options.Create(ConCertificado()));
        servicios.AddHostedService<AvisoConfiguracionMicrosoft365HostedService>();
        using var proveedor = servicios.BuildServiceProvider();

        proveedor.GetServices<IHostedService>().Should().ContainSingle(s => s is AvisoConfiguracionMicrosoft365HostedService);
    }

    private sealed class OpcionesQueRevientan : IOptions<Microsoft365GraphOptions>
    {
        public Microsoft365GraphOptions Value => throw new InvalidOperationException("opciones rotas " + MarcaDeRuta);
    }

    /// <summary>Un flujo que registra si alguien intentó leerlo o cerrarlo: la prueba de «solo abre».</summary>
    private sealed class EspiaDeFlujo : Stream
    {
        public bool SeLeyo { get; private set; }
        public bool SeCerro { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            SeLeyo = true;
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            SeCerro = true;
            base.Dispose(disposing);
        }
    }

    private sealed class LoggerCapturador : ILogger<AvisoConfiguracionMicrosoft365HostedService>
    {
        public List<(LogLevel Nivel, string Texto)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
    }
}
