using System.Net;
using System.Text;
using System.Text.Json;
using CaeManager.Application.Integraciones.ConectoresPlataformaCae;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Ctaima;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// El conector de CTAIMACAE.net contra respuestas HTTP simuladas: ninguna prueba sale a la red. Cubre las
/// dos cabeceras de autenticación, la traducción de cada respuesta a <see cref="MotivoUsoConector"/>, el
/// timeout, y que ni la clave ni el texto del fabricante acaban en un log o en una excepción.
/// </summary>
public class ConectorCtaimaCaeTests
{
    private const string Clave = "clave-suscripcion-0f1e2d3c4b5a69788796a5b4c3d2e1f0";
    private const string TenantCtaima = "tenant-contratista-42";

    // Texto con datos de terceros como el que la API devuelve en un 404 documentado.
    private const string TextoDelTercero = "No employee was found with dni: 12345678Z in the contractor B00000000";

    private static readonly SolicitudAltaTrabajador Alta = new(
        Guid.NewGuid(), " Ana ", " de la Fuente Ruiz ", "12345678z", Guid.NewGuid().ToString("D"));

    private readonly HandlerFalso _handler = new();
    private readonly LoggerQueCaptura _logger = new();

    private ConectorCtaimaCae Crear(TimeSpan? timeout = null) =>
        new(new CtaimaCaeClient(
            new HttpClient(_handler) { Timeout = timeout ?? TimeSpan.FromSeconds(10) },
            Options.Create(new CtaimaCaeOptions()),
            _logger));

    [Fact]
    public void El_codigo_es_el_del_proveedor_sembrado_para_ctaimacae_net()
    {
        var sembrado = CaeManager.Infrastructure.Persistence.Seed.ProveedorPlataformaCaeSeedData.Datos
            .Should().ContainSingle(p => p.Dominios.Contains("ctaimacae.net"), "un solo proveedor sembrado sirve ctaimacae.net")
            .Subject;

        Crear().CodigoProveedor.Should().Be(sembrado.Codigo);
    }

    [Fact]
    public async Task Un_alta_201_envia_la_clave_y_el_tenantId_en_cabeceras_y_devuelve_el_employeeId()
    {
        _handler.Responder(HttpStatusCode.Created, """{"guid":"9b2f","employeeId":4711,"firstName":"Ana"}""");

        var resultado = await Crear().DarDeAltaTrabajadorAsync(new CredencialApiConector(Clave), TenantCtaima, Alta, CancellationToken.None);

        resultado.Fallo.Should().BeNull();
        resultado.Valor.Valor.Should().Be("4711");

        var peticion = _handler.Peticiones.Should().ContainSingle().Subject;
        peticion.Metodo.Should().Be(HttpMethod.Post);
        peticion.Uri.Should().Be(new Uri("https://ctaima.azure-api.net/api/ctaimacae/resource-management/api/internal-employees"));
        peticion.Uri.Query.Should().BeEmpty("la clave nunca va en la URL");
        peticion.Cabeceras["Ocp-Apim-Subscription-Key"].Should().ContainSingle().Which.Should().Be(Clave);
        peticion.Cabeceras["tenantId"].Should().ContainSingle().Which.Should().Be(TenantCtaima);

        using var cuerpo = JsonDocument.Parse(peticion.Cuerpo);
        var raiz = cuerpo.RootElement;
        raiz.GetProperty("identityCardNumber").GetString().Should().Be("12345678Z");
        raiz.GetProperty("identityCardType").GetString().Should().Be("NIF");
        raiz.GetProperty("firstName").GetString().Should().Be("Ana");
        raiz.GetProperty("firstSurname").GetString().Should().Be("de la Fuente Ruiz");
        raiz.GetProperty("enabled").GetBoolean().Should().BeTrue();
        raiz.TryGetProperty("secondSurname", out _).Should().BeFalse("un apellido que no se sabe partir no se inventa");
        peticion.Cuerpo.Should().NotContain(Alta.TrabajadorId.ToString(), "el Id de TALVEG no viaja a la plataforma");
    }

    [Fact]
    public async Task Un_alta_201_sin_employeeId_devuelve_el_guid()
    {
        _handler.Responder(HttpStatusCode.Created, """{"guid":"5F0C3B1E-2D4A-4C6B-9E8F-0A1B2C3D4E5F","employeeId":0}""");

        var resultado = await Crear().DarDeAltaTrabajadorAsync(new CredencialApiConector(Clave), TenantCtaima, Alta, CancellationToken.None);

        resultado.Valor.Valor.Should().Be("5f0c3b1e-2d4a-4c6b-9e8f-0a1b2c3d4e5f");
    }

    [Theory]
    [InlineData("""{"employeeId":0}""")]
    [InlineData("""{"guid":"No employee was found","employeeId":0}""")]
    [InlineData("""{"guid":"00000000-0000-0000-0000-000000000000","employeeId":-3}""")]
    [InlineData("null")]
    [InlineData("<html>no es json</html>")]
    public async Task Un_alta_201_con_un_cuerpo_sin_identificador_o_ilegible_es_inesperado(string cuerpo)
    {
        _handler.Responder(HttpStatusCode.Created, cuerpo);

        var resultado = await Crear().DarDeAltaTrabajadorAsync(new CredencialApiConector(Clave), TenantCtaima, Alta, CancellationToken.None);

        resultado.Fallo.Should().Be(MotivoUsoConector.Inesperado);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, MotivoUsoConector.RechazadoPorPlataforma)]
    [InlineData(HttpStatusCode.Conflict, MotivoUsoConector.RechazadoPorPlataforma)]
    [InlineData(HttpStatusCode.NotFound, MotivoUsoConector.NoEncontradoEnPlataforma)]
    [InlineData(HttpStatusCode.Unauthorized, MotivoUsoConector.CredencialRechazada)]
    [InlineData(HttpStatusCode.Forbidden, MotivoUsoConector.CredencialRechazada)]
    [InlineData(HttpStatusCode.TooManyRequests, MotivoUsoConector.LimiteSuperado)]
    [InlineData(HttpStatusCode.InternalServerError, MotivoUsoConector.PlataformaNoDisponible)]
    [InlineData(HttpStatusCode.ServiceUnavailable, MotivoUsoConector.PlataformaNoDisponible)]
    [InlineData(HttpStatusCode.Found, MotivoUsoConector.Inesperado)]
    public async Task Un_alta_rechazada_se_traduce_a_un_motivo_sin_el_texto_del_tercero(HttpStatusCode codigo, MotivoUsoConector esperado)
    {
        _handler.Responder(codigo, TextoDelTercero);

        var resultado = await Crear().DarDeAltaTrabajadorAsync(new CredencialApiConector(Clave), TenantCtaima, Alta, CancellationToken.None);

        resultado.Fallo.Should().Be(esperado);
        _logger.Todo.Should().Contain(((int)codigo).ToString(), "el log dice qué código HTTP se recibió");
        _logger.Todo.Should().NotContain("12345678Z").And.NotContain("B00000000").And.NotContain("No employee");
        _logger.Todo.Should().NotContain(Clave).And.NotContain(TenantCtaima);
    }

    [Fact]
    public async Task Un_timeout_es_plataforma_no_disponible_y_no_lanza()
    {
        _handler.Colgar();

        var resultado = await Crear(TimeSpan.FromMilliseconds(200))
            .DarDeAltaTrabajadorAsync(new CredencialApiConector(Clave), TenantCtaima, Alta, CancellationToken.None);

        resultado.Fallo.Should().Be(MotivoUsoConector.PlataformaNoDisponible);
        _logger.Todo.Should().NotContain(Clave).And.NotContain(TenantCtaima);
    }

    [Fact]
    public async Task La_cancelacion_de_quien_llama_se_propaga_para_que_la_puerta_la_registre_como_cancelada()
    {
        _handler.Colgar();
        using var cancelacion = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var llamada = () => Crear().DarDeAltaTrabajadorAsync(new CredencialApiConector(Clave), TenantCtaima, Alta, cancelacion.Token);

        await llamada.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Un_fallo_de_red_es_plataforma_no_disponible_sin_copiar_su_mensaje()
    {
        _handler.Fallar(new HttpRequestException(TextoDelTercero));

        var resultado = await Crear().DarDeAltaTrabajadorAsync(new CredencialApiConector(Clave), TenantCtaima, Alta, CancellationToken.None);

        resultado.Fallo.Should().Be(MotivoUsoConector.PlataformaNoDisponible);
        _logger.Todo.Should().NotContain("No employee").And.NotContain(Clave).And.NotContain(TenantCtaima);
    }

    [Theory]
    [InlineData("clave\r\nX-Inyectada: 1")]
    [InlineData("clave con espacios")]
    [InlineData("")]
    public async Task Una_clave_que_no_cabe_en_una_cabecera_se_rechaza_sin_llamar_y_sin_excepcion_que_la_copie(string clave)
    {
        var resultado = await Crear().DarDeAltaTrabajadorAsync(new CredencialApiConector(clave), TenantCtaima, Alta, CancellationToken.None);

        resultado.Fallo.Should().Be(MotivoUsoConector.SinCredencial, "la plataforma no llegó a ver la llamada");
        _handler.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public void El_manejador_real_no_sigue_redirecciones_ni_guarda_cookies_entre_conexiones()
    {
        using var manejador = CtaimaCaeClient.CrearManejadorSinRedirecciones();

        manejador.AllowAutoRedirect.Should().BeFalse("la clave va en una cabecera que .NET reenviaría al destino del 3xx");
        manejador.UseCookies.Should().BeFalse("el manejador del pool se comparte entre las conexiones de todos los Tenants");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("tenant\r\nX-Inyectada: 1")]
    public async Task Sin_un_tenantId_valido_no_se_llama(string? tenant)
    {
        var resultado = await Crear().DarDeAltaTrabajadorAsync(new CredencialApiConector(Clave), tenant, Alta, CancellationToken.None);

        resultado.Fallo.Should().Be(MotivoUsoConector.DatosInsuficientes);
        _handler.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task Por_la_puerta_hoy_ninguna_operacion_llama_porque_no_llegan_los_datos_que_la_api_exige()
    {
        var conector = Crear();
        var credencial = new CredencialApiConector(Clave);
        using var contenido = new MemoryStream([1, 2, 3]);

        var alta = await conector.DarDeAltaTrabajadorAsync(credencial, Alta, CancellationToken.None);
        var lectura = await conector.LeerEstadoDocumentoAsync(credencial, new SolicitudLecturaEstado(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        var subida = await conector.SubirDocumentoAsync(
            credencial, new SolicitudSubidaDocumento(Guid.NewGuid(), Guid.NewGuid(), contenido, Guid.NewGuid().ToString("D")), CancellationToken.None);

        alta.Fallo.Should().Be(MotivoUsoConector.DatosInsuficientes);
        lectura.Fallo.Should().Be(MotivoUsoConector.DatosInsuficientes);
        subida.Fallo.Should().Be(MotivoUsoConector.DatosInsuficientes);
        _handler.Peticiones.Should().BeEmpty();
    }

    [Theory]
    [InlineData("12345678Z", "NIF", "12345678Z")]
    [InlineData("x1234567l", "NIF", "X1234567L")]
    [InlineData("12.345.678-z", "NIF", "12345678Z")]
    [InlineData(" 12345678 Z ", "NIF", "12345678Z")]
    [InlineData("PAA-123456", "Others", "PAA-123456")]
    [InlineData("1234567Z", "Others", "1234567Z")]
    public void El_tipo_de_documento_es_NIF_solo_para_un_DNI_o_un_NIE(string documento, string tipo, string enviado)
    {
        var peticion = ConectorCtaimaCae.PeticionDeAlta(Alta with { Dni = documento });

        peticion.IdentityCardType.Should().Be(tipo);
        peticion.IdentityCardNumber.Should().Be(enviado);
        peticion.ToString().Should().NotContain(peticion.IdentityCardNumber, "los datos del Trabajador no se escriben en un log");
    }

    [Fact]
    public void La_credencial_y_la_solicitud_no_se_escriben_al_interpolarlas()
    {
        $"{new CredencialApiConector(Clave)} {Alta}".Should().NotContain(Clave).And.NotContain("12345678");
    }

    [Theory]
    [InlineData("https://ctaima.azure-api.net/api/ctaimacae/", 30, true)]
    [InlineData("http://ctaima.azure-api.net/api/ctaimacae/", 30, false)]
    [InlineData("https://ctaima.azure-api.net/api/ctaimacae", 30, false)]
    [InlineData("https://ctaima.azure-api.net/api/ctaimacae/?subscription-key=x", 30, false)]
    [InlineData("https://usuario:clave@ctaima.azure-api.net/api/ctaimacae/", 30, false)]
    [InlineData("https://ctaima.azure-api.net/api/ctaimacae/", 0, false)]
    public void Las_opciones_solo_aceptan_https_absoluto_acabado_en_barra(string url, int timeout, bool valida)
    {
        new CtaimaCaeOptions { UrlBase = url, TimeoutSegundos = timeout }.EsValida().Should().Be(valida);
    }

    [Fact]
    public void Las_opciones_por_defecto_son_validas()
    {
        new CtaimaCaeOptions().EsValida().Should().BeTrue();
    }

    private sealed record PeticionVista(HttpMethod Metodo, Uri Uri, Dictionary<string, string[]> Cabeceras, string Cuerpo);

    private sealed class HandlerFalso : HttpMessageHandler
    {
        private Func<CancellationToken, Task<HttpResponseMessage>> _respuesta =
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        public List<PeticionVista> Peticiones { get; } = [];

        public void Responder(HttpStatusCode codigo, string cuerpo) =>
            _respuesta = _ => Task.FromResult(new HttpResponseMessage(codigo)
            {
                Content = new StringContent(cuerpo, Encoding.UTF8, "application/json"),
            });

        public void Colgar() =>
            _respuesta = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            };

        public void Fallar(Exception excepcion) => _respuesta = _ => throw excepcion;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var cuerpo = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Peticiones.Add(new PeticionVista(
                request.Method,
                request.RequestUri!,
                request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                cuerpo));
            return await _respuesta(cancellationToken);
        }
    }

    private sealed class LoggerQueCaptura : ILogger<CtaimaCaeClient>
    {
        private readonly List<string> _entradas = [];

        public string Todo => string.Join('\n', _entradas);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _entradas.Add(formatter(state, exception));
            if (exception is not null)
                _entradas.Add(exception.ToString());

            if (state is IEnumerable<KeyValuePair<string, object?>> valores)
                _entradas.AddRange(valores.Select(v => $"{v.Key}={v.Value}"));
        }
    }
}
