using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// Reservas de la revisión de #739 sobre el aserto de cliente (P44b), medidas
/// contra <see cref="AsertoClienteMicrosoft365"/> DIRECTAMENTE y no solo a través
/// del cliente HTTP:
/// <list type="bullet">
/// <item>el aserto es base64url de verdad (sin <c>+</c>, <c>/</c> ni <c>=</c>): una
/// aserción directa, no la que dependía de que los hashes del certificado
/// autofirmado, que se regenera en cada ejecución, contuvieran <c>+</c> o <c>/</c>;</item>
/// <item><c>iat</c> se afirma (borrar esa reclamación no ponía nada rojo);</item>
/// <item>«no filtra la ruta ni la clave» se mide con un logger que CAPTURA lo que
/// se escribe, no con <c>NullLogger</c>, que descarta el mensaje.</item>
/// </list>
/// </summary>
public sealed class AsertoClienteMicrosoft365Tests : IDisposable
{
    private const string EndpointToken = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const string ClientId = "11111111-2222-3333-4444-555555555555";
    private const string ClavePrivadaFalsa =
        // Partida a propósito: el escaneo de secretos (gitleaks) no debe ver un bloque de clave privada en el fichero.
        "-----BEGIN PRIV" + "ATE KEY-----\nMARCA-DE-CLAVE-SECRETA-0123456789\n-----END PRIV" + "ATE KEY-----\n";

    private readonly string _dir = Directory.CreateTempSubdirectory("m365-aserto-").FullName;
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly X509Certificate2 _certificado;
    private readonly string _rutaCertificado;
    private readonly string _rutaClave;

    public AsertoClienteMicrosoft365Tests()
    {
        var peticion = new CertificateRequest("CN=talveg-m365-aserto", _rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        _certificado = peticion.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        _rutaCertificado = Path.Combine(_dir, "cert.pem");
        _rutaClave = Path.Combine(_dir, "clave.pem");
        File.WriteAllText(_rutaCertificado, _certificado.ExportCertificatePem());
        File.WriteAllText(_rutaClave, _rsa.ExportPkcs8PrivateKeyPem());
    }

    public void Dispose()
    {
        _certificado.Dispose();
        _rsa.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static byte[] DeBase64Url(string valor)
    {
        var estandar = valor.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(estandar.PadRight(estandar.Length + ((4 - (estandar.Length % 4)) % 4), '='));
    }

    [Fact]
    public void El_aserto_es_base64url_sin_mas_ni_barra_ni_igual_en_ninguno_de_sus_tres_segmentos()
    {
        // Varios asertos, no uno: la firma RSA de 256 bytes se codifica en 342 caracteres y, en
        // base64 estándar, cada uno tiene 2/64 de ser «+» o «/»; con tres asertos la probabilidad de
        // que una codificación defectuosa pase desapercibida es despreciable. Además, 256 bytes
        // dejan SIEMPRE «==» de relleno en la firma: quitar el TrimEnd('=') se ve el 100 % de las veces.
        for (var i = 0; i < 3; i++)
        {
            var aserto = AsertoClienteMicrosoft365.Crear(ClientId, EndpointToken, _rutaCertificado, _rutaClave, DateTimeOffset.UtcNow);

            aserto.EsExitoso.Should().BeTrue();
            aserto.Valor.Should().MatchRegex(@"^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$",
                "un JWT solo admite el alfabeto base64url: un «+», un «/» o un «=» en cualquier segmento lo invalida ante Entra");
            aserto.Valor.Should().NotContainAny("+", "/", "=");
        }
    }

    [Fact]
    public void El_aserto_lleva_iat_igual_al_instante_de_firma()
    {
        var ahora = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

        var aserto = AsertoClienteMicrosoft365.Crear(ClientId, EndpointToken, _rutaCertificado, _rutaClave, ahora).Valor;

        using var cuerpo = JsonDocument.Parse(DeBase64Url(aserto.Split('.')[1]));
        cuerpo.RootElement.GetProperty("iat").GetInt64().Should().Be(ahora.ToUnixTimeSeconds());
        cuerpo.RootElement.GetProperty("nbf").GetInt64().Should().Be(ahora.ToUnixTimeSeconds());
    }

    [Fact]
    public void Crear_con_una_ruta_inexistente_falla_sin_nombrar_la_ruta_en_el_error()
    {
        // La excepción real de .NET SÍ contiene la ruta («Could not find file '…'»): si el error
        // devuelto pasara a ser ex.Message, este test se pondría rojo.
        var rutaConMarca = Path.Combine(_dir, "MARCA-DE-RUTA", "no-existe.pem");

        var resultado = AsertoClienteMicrosoft365.Crear(ClientId, EndpointToken, rutaConMarca, _rutaClave, DateTimeOffset.UtcNow);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.CertificadoNoLegible");
        resultado.Error.Mensaje.Should().NotContain("MARCA-DE-RUTA").And.NotContain(_dir);
    }

    [Fact]
    public void Crear_con_una_clave_ilegible_no_devuelve_material_de_la_clave_en_el_error()
    {
        File.WriteAllText(_rutaClave, ClavePrivadaFalsa);

        var resultado = AsertoClienteMicrosoft365.Crear(ClientId, EndpointToken, _rutaCertificado, _rutaClave, DateTimeOffset.UtcNow);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Mensaje.Should().NotContain("MARCA-DE-CLAVE-SECRETA").And.NotContain("PRIVATE KEY");
    }

    [Fact]
    public async Task El_cliente_no_escribe_en_el_log_la_ruta_del_certificado_al_fallar()
    {
        var rutaConMarca = Path.Combine(_dir, "MARCA-DE-RUTA", "no-existe.pem");
        var logger = new LoggerCapturador();
        var cliente = CrearCliente(logger, rutaConMarca, _rutaClave);

        var resultado = await cliente.RefrescarTokensAsync("r", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();

        // Control positivo del instrumento: el logger SÍ ve el fallo. Sin esta aserción, un logger
        // que no capturase nada haría pasar el «no aparece la ruta» de abajo por la razón equivocada.
        logger.Entradas.Should().Contain(e => e.Nivel == LogLevel.Error && e.Texto.Contains("CertificadoNoLegible"),
            "el fallo se registra con su código, sin más");

        logger.Entradas.Should().OnlyContain(e =>
                !e.Texto.Contains("MARCA-DE-RUTA") && !e.Texto.Contains(_dir) && !e.Texto.Contains("no-existe.pem"),
            "ni el mensaje ni la excepción del log pueden nombrar la ruta del certificado");
    }

    [Fact]
    public async Task El_cliente_no_escribe_en_el_log_el_contenido_de_una_clave_privada_ilegible()
    {
        File.WriteAllText(_rutaClave, ClavePrivadaFalsa);
        var logger = new LoggerCapturador();
        var cliente = CrearCliente(logger, _rutaCertificado, _rutaClave);

        var resultado = await cliente.RefrescarTokensAsync("r", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        logger.Entradas.Should().Contain(e => e.Nivel == LogLevel.Error, "control positivo: el logger observa el fallo");
        logger.Entradas.Should().OnlyContain(e =>
            !e.Texto.Contains("MARCA-DE-CLAVE-SECRETA") && !e.Texto.Contains("PRIVATE KEY") && !e.Texto.Contains(_dir));
    }

    private static Microsoft365GraphClient CrearCliente(LoggerCapturador logger, string certificadoRuta, string clavePrivadaRuta) =>
        new(new HttpClient(new SinRedHandler()),
            Options.Create(new Microsoft365GraphOptions
            {
                ClientId = ClientId,
                UrlPublicaBase = "https://app.ejemplo.test",
                CertificadoRuta = certificadoRuta,
                ClavePrivadaRuta = clavePrivadaRuta,
            }),
            logger);

    /// <summary>Un fallo al firmar no debe llegar a la red: si llega, es un fallo del test.</summary>
    private sealed class SinRedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No debería haber petición de red: el aserto no se pudo generar.");
    }

    /// <summary>
    /// Captura el mensaje formateado, los pares de la plantilla Y la excepción (con su mensaje y
    /// traza): una ruta o una clave se pueden colar por cualquiera de los tres. <c>NullLogger</c>
    /// los descarta.
    /// </summary>
    private sealed class LoggerCapturador : ILogger<Microsoft365GraphClient>
    {
        public List<(LogLevel Nivel, string Texto)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var sb = new StringBuilder(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> pares)
            {
                foreach (var par in pares)
                    sb.Append(' ').Append(par.Key).Append('=').Append(par.Value);
            }

            if (exception is not null)
                sb.Append(' ').Append(exception);

            Entradas.Add((logLevel, sb.ToString()));
        }
    }
}
