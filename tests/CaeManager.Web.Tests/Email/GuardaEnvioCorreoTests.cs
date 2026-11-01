using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.Email;
using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests.Email;

/// <summary>
/// C-04, decisión D-2 (a): la salida de correo real nace cerrada en todo entorno, porque staging
/// arranca como Production y <c>IHostEnvironment</c> no lo distingue. Solo la abre
/// <c>EnvioCorreo__Modo</c>, validada al arrancar.
/// </summary>
public class GuardaEnvioCorreoTests
{
    private const string Permitido = "pruebas@talveg.es";
    private const string Tercero = "contacto@tercero.example";

    private static EnvioCorreoOptions Opciones(string? modo, params string[] permitidos) =>
        new() { Modo = modo, DestinatariosPermitidos = permitidos };

    private static GuardaEnvioEmailService GuardaSmtp(IEmailService interno, EnvioCorreoOptions opciones) =>
        new(interno, Options.Create(opciones), NullLogger<GuardaEnvioEmailService>.Instance);

    private static GuardaEnvioMicrosoft365GraphClient GuardaGraph(IMicrosoft365GraphClient interno, EnvioCorreoOptions opciones) =>
        new(interno, Options.Create(opciones), NullLogger<GuardaEnvioMicrosoft365GraphClient>.Instance);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Cerrado")]
    public async Task Sin_abrirla_ningun_correo_sale_por_SMTP(string? modo)
    {
        var interno = new EspiaSmtp();

        var resultado = await GuardaSmtp(interno, Opciones(modo)).EnviarAsync(Tercero, "a", "c", TipoAvisoCorreo.Transaccional);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(EnvioCorreoOptions.CodigoErrorEnvioCerrado);
        interno.Envios.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_modo_que_no_se_reconoce_tampoco_abre_aunque_llegue_sin_validar()
    {
        var interno = new EspiaSmtp();

        var resultado = await GuardaSmtp(interno, Opciones("abierta")).EnviarAsync(Tercero, "a", "c", TipoAvisoCorreo.Transaccional);

        resultado.EsFallido.Should().BeTrue();
        interno.Envios.Should().BeEmpty();
    }

    [Fact]
    public async Task Abierto_delega_el_correo_tal_cual_en_SMTP()
    {
        var interno = new EspiaSmtp();

        var resultado = await GuardaSmtp(interno, Opciones("Abierto"))
            .EnviarAsync(Tercero, "asunto", "cuerpo", TipoAvisoCorreo.Seguridad, "gestora@talveg.es");

        resultado.EsExitoso.Should().BeTrue();
        interno.Envios.Should().Equal((Tercero, "asunto", "cuerpo", TipoAvisoCorreo.Seguridad, "gestora@talveg.es"));
    }

    [Theory]
    [InlineData(Permitido, true)]
    [InlineData("PRUEBAS@Talveg.es", true)]
    [InlineData(Tercero, false)]
    [InlineData("otra-pruebas@talveg.es", false)]
    [InlineData("pruebas@talveg.es, contacto@tercero.example", false)]
    [InlineData("Pruebas <pruebas@talveg.es>", false)]
    public async Task SoloPermitidos_deja_pasar_solo_la_direccion_exacta_de_la_lista(string destinatario, bool sale)
    {
        var interno = new EspiaSmtp();

        var resultado = await GuardaSmtp(interno, Opciones("SoloPermitidos", Permitido))
            .EnviarAsync(destinatario, "a", "c", TipoAvisoCorreo.Transaccional);

        resultado.EsExitoso.Should().Be(sale);
        interno.Envios.Should().HaveCount(sale ? 1 : 0);
    }

    [Fact]
    public async Task Cerrada_no_se_envia_ni_el_mensaje_nuevo_ni_la_respuesta_por_Graph()
    {
        var interno = new EspiaGraph();
        var guarda = GuardaGraph(interno, Opciones(null));

        var nuevo = await guarda.EnviarNuevoMensajeAsync("t", "b", [Tercero], "a", "c", null, default);
        var respuesta = await guarda.EnviarRespuestaAsync("t", "b", "origen", "c", null, default);

        nuevo.EsFallido.Should().BeTrue();
        nuevo.Error.Codigo.Should().Be(EnvioCorreoOptions.CodigoErrorEnvioCerrado);
        respuesta.EsFallido.Should().BeTrue();
        interno.MensajesNuevos.Should().Be(0);
        interno.Respuestas.Should().Be(0);
    }

    [Fact]
    public async Task SoloPermitidos_en_Graph_exige_que_todos_los_destinatarios_y_la_copia_esten_en_la_lista()
    {
        var interno = new EspiaGraph();
        var guarda = GuardaGraph(interno, Opciones("SoloPermitidos", Permitido));

        (await guarda.EnviarNuevoMensajeAsync("t", "b", [Permitido, Tercero], "a", "c", null, default)).EsFallido.Should().BeTrue();
        (await guarda.EnviarNuevoMensajeAsync("t", "b", [Permitido], "a", "c", null, default, [Tercero])).EsFallido.Should().BeTrue();
        (await guarda.EnviarNuevoMensajeAsync("t", "b", [], "a", "c", null, default)).EsFallido.Should().BeTrue();
        interno.MensajesNuevos.Should().Be(0);

        (await guarda.EnviarNuevoMensajeAsync("t", "b", [Permitido], "a", "c", null, default, [Permitido])).EsExitoso.Should().BeTrue();
        interno.MensajesNuevos.Should().Be(1);
    }

    [Fact]
    public async Task SoloPermitidos_no_deja_salir_una_respuesta_de_Graph_porque_su_destinatario_no_se_conoce_aqui()
    {
        var interno = new EspiaGraph();

        var resultado = await GuardaGraph(interno, Opciones("SoloPermitidos", Permitido))
            .EnviarRespuestaAsync("t", "b", "origen", "c", null, default);

        resultado.EsFallido.Should().BeTrue();
        interno.Respuestas.Should().Be(0);
    }

    [Fact]
    public async Task Abierto_delega_los_dos_envios_de_Graph()
    {
        var interno = new EspiaGraph();
        var guarda = GuardaGraph(interno, Opciones("Abierto"));

        (await guarda.EnviarNuevoMensajeAsync("t", "b", [Tercero], "a", "c", null, default)).EsExitoso.Should().BeTrue();
        (await guarda.EnviarRespuestaAsync("t", "b", "origen", "c", null, default)).EsExitoso.Should().BeTrue();
        interno.MensajesNuevos.Should().Be(1);
        interno.Respuestas.Should().Be(1);
    }

    /// <summary>
    /// Ingesta, tokens y suscripciones son lectura o mantenimiento de la Conexión: cerrar la salida
    /// no puede dejar un buzón sin entrada ni sin renovar su suscripción.
    /// </summary>
    [Fact]
    public async Task Cerrada_no_corta_lo_que_no_es_un_envio()
    {
        var resultado = await GuardaGraph(new EspiaGraph(), Opciones(null)).ListarCarpetasAsync("t", default);

        resultado.EsExitoso.Should().BeTrue();
    }

    /// <summary>
    /// Trinquete: un método de envío nuevo en el contrato tiene que pasar por la guarda. El decorador
    /// lo delegaría sin mirar nada si se añadiera sin pensar, así que el test obliga a ir a mirarlo.
    /// </summary>
    [Fact]
    public void El_contrato_de_Graph_no_tiene_mas_envios_que_los_dos_que_la_guarda_comprueba()
    {
        var envios = typeof(IMicrosoft365GraphClient).GetMethods()
            .Select(m => m.Name)
            .Where(n => n.Contains("Enviar", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Send", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Responder", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Reply", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Forward", StringComparison.OrdinalIgnoreCase));

        envios.Should().BeEquivalentTo(
            nameof(IMicrosoft365GraphClient.EnviarRespuestaAsync),
            nameof(IMicrosoft365GraphClient.EnviarNuevoMensajeAsync));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Cerrado")]
    [InlineData("cerrado")]
    [InlineData("Abierto")]
    public void Son_validos_el_valor_por_defecto_y_los_modos_sin_lista(string? modo) =>
        Opciones(modo).EsValida().Should().BeTrue();

    [Fact]
    public void SoloPermitidos_con_direcciones_es_valido() =>
        Opciones("SoloPermitidos", Permitido).EsValida().Should().BeTrue();

    [Theory]
    [InlineData("abierta")]
    [InlineData("true")]
    [InlineData("2")]
    public void Un_modo_desconocido_no_es_valido(string modo) =>
        Opciones(modo).EsValida().Should().BeFalse();

    [Fact]
    public void SoloPermitidos_sin_lista_no_es_valido() =>
        Opciones("SoloPermitidos").EsValida().Should().BeFalse();

    [Fact]
    public void Abierto_con_lista_no_es_valido_porque_la_lista_no_filtraria_nada() =>
        Opciones("Abierto", Permitido).EsValida().Should().BeFalse();

    [Theory]
    [InlineData("")]
    [InlineData("sin-arroba")]
    [InlineData(" pruebas@talveg.es")]
    [InlineData("Pruebas <pruebas@talveg.es>")]
    [InlineData("@talveg.es")]
    public void Una_entrada_de_la_lista_que_no_es_una_direccion_exacta_no_es_valida(string entrada) =>
        Opciones("SoloPermitidos", entrada).EsValida().Should().BeFalse();

    private static ServiceProvider Componer(params (string Clave, string Valor)[] claves)
    {
        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(claves.Select(c => KeyValuePair.Create<string, string?>(c.Clave, c.Valor)))
            .Build();
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddInfrastructure(configuracion, new EntornoFalso());
        return servicios.BuildServiceProvider();
    }

    [Fact]
    public void La_composicion_real_entrega_los_dos_envios_detras_de_la_guarda()
    {
        using var proveedor = Componer();
        using var ambito = proveedor.CreateScope();

        ambito.ServiceProvider.GetRequiredService<IEmailService>().Should().BeOfType<GuardaEnvioEmailService>();
        ambito.ServiceProvider.GetRequiredService<IMicrosoft365GraphClient>().Should().BeOfType<GuardaEnvioMicrosoft365GraphClient>();
    }

    [Fact]
    public void Las_variables_de_entorno_con_doble_guion_bajo_llegan_a_la_guarda()
    {
        Environment.SetEnvironmentVariable("GuardaCorreoPrueba__EnvioCorreo__Modo", "SoloPermitidos");
        Environment.SetEnvironmentVariable("GuardaCorreoPrueba__EnvioCorreo__DestinatariosPermitidos__0", Permitido);
        try
        {
            var configuracion = new ConfigurationBuilder().AddEnvironmentVariables("GuardaCorreoPrueba__").Build();
            var opciones = new EnvioCorreoOptions();
            configuracion.GetSection(EnvioCorreoOptions.SeccionConfiguracion).Bind(opciones);

            opciones.ModoEfectivo.Should().Be(ModoEnvioCorreo.SoloPermitidos);
            opciones.DestinatariosPermitidos.Should().Equal(Permitido);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GuardaCorreoPrueba__EnvioCorreo__Modo", null);
            Environment.SetEnvironmentVariable("GuardaCorreoPrueba__EnvioCorreo__DestinatariosPermitidos__0", null);
        }
    }

    [Fact]
    public void Sin_la_variable_arranca_y_queda_cerrada()
    {
        using var proveedor = Componer();

        proveedor.GetRequiredService<IStartupValidator>().Invoking(v => v.Validate()).Should().NotThrow();
        proveedor.GetRequiredService<IOptions<EnvioCorreoOptions>>().Value.ModoEfectivo.Should().Be(ModoEnvioCorreo.Cerrado);
    }

    [Fact]
    public void Un_valor_mal_escrito_impide_arrancar()
    {
        using var proveedor = Componer(("EnvioCorreo:Modo", "abierta"));

        proveedor.GetRequiredService<IStartupValidator>().Invoking(v => v.Validate())
            .Should().Throw<OptionsValidationException>().WithMessage("*EnvioCorreo*");
    }

    [Theory]
    [InlineData(null, LogLevel.Warning)]
    [InlineData("Cerrado", LogLevel.Warning)]
    [InlineData("Abierto", LogLevel.Information)]
    public async Task El_arranque_avisa_de_si_la_salida_esta_cerrada(string? modo, LogLevel nivel)
    {
        var logger = new LoggerDeNiveles<AvisoGuardaEnvioCorreoHostedService>();

        await new AvisoGuardaEnvioCorreoHostedService(Options.Create(Opciones(modo)), logger).StartAsync(default);

        logger.Niveles.Should().Equal(nivel);
    }

    [Fact]
    public async Task El_rastro_del_bloqueo_no_lleva_la_direccion_del_destinatario()
    {
        var logger = new LoggerDeNiveles<GuardaEnvioEmailService>();
        var guarda = new GuardaEnvioEmailService(new EspiaSmtp(), Options.Create(Opciones(null)), logger);

        await guarda.EnviarAsync(Tercero, "a", "c", TipoAvisoCorreo.Transaccional);

        logger.Niveles.Should().Equal(LogLevel.Warning);
        logger.Mensajes.Should().ContainSingle().Which.Should().Contain(EnvioCorreoOptions.TextoBloqueo).And.NotContain(Tercero);
    }

    /// <summary>
    /// Trinquete: lo único que hay detrás de la guarda son estos dos adaptadores, y nadie los pide
    /// por su tipo concreto, que es la forma de saltársela.
    /// </summary>
    [Fact]
    public void Nadie_en_src_implementa_otro_envio_ni_pide_el_adaptador_sin_guarda()
    {
        var ensamblados = new[]
        {
            typeof(IEmailService).Assembly, typeof(SmtpEmailService).Assembly, typeof(CaeManager.Web.Services.RecuperacionContrasena).Assembly,
        };
        var tipos = ensamblados.SelectMany(a => a.GetTypes()).ToList();

        tipos.Where(t => !t.IsInterface && typeof(IEmailService).IsAssignableFrom(t)).Should()
            .BeEquivalentTo([typeof(SmtpEmailService), typeof(GuardaEnvioEmailService)]);
        tipos.Where(t => !t.IsInterface && typeof(IMicrosoft365GraphClient).IsAssignableFrom(t)).Should()
            .BeEquivalentTo([typeof(Microsoft365GraphClient), typeof(GuardaEnvioMicrosoft365GraphClient)]);

        Type[] sinGuarda = [typeof(SmtpEmailService), typeof(Microsoft365GraphClient)];
        const BindingFlags todo = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var quienesLoPiden = tipos
            .Where(t => t.GetConstructors(todo).Any(c => c.GetParameters().Any(p => sinGuarda.Contains(p.ParameterType)))
                || t.GetProperties(todo).Any(p => sinGuarda.Contains(p.PropertyType)))
            .Select(t => t.FullName);

        quienesLoPiden.Should().BeEmpty();
    }

    private sealed class EspiaSmtp : IEmailService
    {
        public List<(string Destinatario, string Asunto, string Cuerpo, TipoAvisoCorreo Tipo, string? ResponderA)> Envios { get; } = [];

        public Task<Result> EnviarAsync(
            string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
            string? responderA = null, CancellationToken cancellationToken = default)
        {
            Envios.Add((destinatarioEmail, asunto, cuerpoHtml, tipo, responderA));
            return Task.FromResult(Result.Exito());
        }
    }

    private sealed class EspiaGraph : IMicrosoft365GraphClient
    {
        public int MensajesNuevos { get; private set; }
        public int Respuestas { get; private set; }

        public Task<Result> EnviarRespuestaAsync(
            string accessToken, string buzonEmail, string mensajeExternoIdOrigen, string cuerpoHtml,
            IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken)
        {
            Respuestas++;
            return Task.FromResult(Result.Exito());
        }

        public Task<Result<MensajeEnviadoGraphDto>> EnviarNuevoMensajeAsync(
            string accessToken, string buzonEmail, IReadOnlyList<string> destinatarios, string asunto, string cuerpoHtml,
            IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken,
            IReadOnlyList<string>? copia = null)
        {
            MensajesNuevos++;
            return Task.FromResult(Result.Exito(new MensajeEnviadoGraphDto("mensaje", "hilo")));
        }

        public Task<Result<IReadOnlyList<CarpetaGraphDto>>> ListarCarpetasAsync(string accessToken, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Exito<IReadOnlyList<CarpetaGraphDto>>([]));

        public string ConstruirUrlAutorizacion(string redirectUri, string state) => throw new NotSupportedException();
        public Task<Result<TokensGraphDto>> IntercambiarCodigoPorTokensAsync(string code, string redirectUri, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result<TokensGraphDto>> RefrescarTokensAsync(string refreshToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result<string>> ObtenerBuzonEmailAsync(string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result<MensajeGraphDto>> ObtenerMensajeAsync(string accessToken, string buzonEmail, string mensajeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result<byte[]>> ObtenerContenidoAdjuntoAsync(string accessToken, string mensajeId, string adjuntoExternoId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<MensajeResumenGraphDto>>> ListarMensajesAsync(string accessToken, string carpetaExternoId, int top, int skip, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result<SuscripcionGraphDto>> CrearSuscripcionAsync(string accessToken, string buzonEmail, string notificationUrl, string clientState, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result<SuscripcionGraphDto>> RenovarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result> EliminarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IReadOnlyList<string> ExtraerMensajeIdsDeNotificacion(string payloadJson) => throw new NotSupportedException();
        public string? ExtraerClientStateDeNotificacion(string payloadJson) => throw new NotSupportedException();
        public string? ExtraerSubscriptionIdDeNotificacion(string payloadJson) => throw new NotSupportedException();
    }

    private sealed class LoggerDeNiveles<T> : ILogger<T>
    {
        public List<LogLevel> Niveles { get; } = [];
        public List<string> Mensajes { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Niveles.Add(logLevel);
            Mensajes.Add(formatter(state, exception));
        }
    }

    private sealed class EntornoFalso : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "CaeManager";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
