using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Web;
using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// P44b (decisiones del 2026-09-19): el canje de tokens del conector de Microsoft 365
/// se autentica con un CERTIFICADO en vez de con un secreto de cliente permanente.
/// Estas pruebas fijan el contrato del aserto (RFC 7523) sin red: un handler captura
/// el cuerpo del POST al endpoint de tokens, y el aserto se VERIFICA con la clave
/// pública del certificado, no solo se mira su forma.
/// </summary>
public sealed class Microsoft365CertificadoDeClienteTests : IDisposable
{
    private const string EndpointToken = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const string ClientId = "11111111-2222-3333-4444-555555555555";

    private readonly string _dir = Directory.CreateTempSubdirectory("m365-cert-").FullName;
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly X509Certificate2 _certificado;
    private readonly string _rutaCertificado;
    private readonly string _rutaClave;

    public Microsoft365CertificadoDeClienteTests()
    {
        var peticion = new CertificateRequest("CN=talveg-m365-prueba", _rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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

    private sealed class CapturaHandler : HttpMessageHandler
    {
        public List<string> Cuerpos { get; } = [];
        public List<Uri> Urls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!);
            Cuerpos.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"a","refresh_token":"r","expires_in":3600}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (Microsoft365GraphClient Cliente, CapturaHandler Handler) CrearCliente(Microsoft365GraphOptions opciones)
    {
        var handler = new CapturaHandler();
        var cliente = new Microsoft365GraphClient(
            new HttpClient(handler), Options.Create(opciones), NullLogger<Microsoft365GraphClient>.Instance);
        return (cliente, handler);
    }

    private Microsoft365GraphOptions ConCertificado() => new()
    {
        ClientId = ClientId,
        UrlPublicaBase = "https://app.ejemplo.test",
        CertificadoRuta = _rutaCertificado,
        ClavePrivadaRuta = _rutaClave,
    };

    private static System.Collections.Specialized.NameValueCollection Formulario(string cuerpo) => HttpUtility.ParseQueryString(cuerpo);

    private static byte[] DeBase64Url(string valor)
    {
        var relleno = valor.Replace('-', '+').Replace('_', '/');
        relleno = relleno.PadRight(relleno.Length + ((4 - (relleno.Length % 4)) % 4), '=');
        return Convert.FromBase64String(relleno);
    }

    private static string ABase64Url(byte[] datos) =>
        Convert.ToBase64String(datos).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task Con_certificado_el_canje_envia_un_aserto_firmado_y_NO_client_secret()
    {
        var opciones = ConCertificado();
        opciones.ClientSecret = "secreto-que-no-debe-enviarse"; // también configurado: el certificado manda
        var (cliente, handler) = CrearCliente(opciones);

        var resultado = await cliente.IntercambiarCodigoPorTokensAsync("codigo", "https://app.ejemplo.test/cb", CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        handler.Urls.Single().ToString().Should().Be(EndpointToken);
        var form = Formulario(handler.Cuerpos.Single());
        form["client_id"].Should().Be(ClientId);
        form["client_assertion_type"].Should().Be("urn:ietf:params:oauth:client-assertion-type:jwt-bearer");
        form["client_assertion"].Should().NotBeNullOrWhiteSpace();
        form["client_secret"].Should().BeNull("con certificado el secreto de cliente no se envía aunque esté configurado");
        handler.Cuerpos.Single().Should().NotContain("secreto-que-no-debe-enviarse");
        form["grant_type"].Should().Be("authorization_code");
    }

    [Fact]
    public async Task El_refresco_de_tokens_tambien_se_autentica_con_el_aserto()
    {
        var (cliente, handler) = CrearCliente(ConCertificado());

        var resultado = await cliente.RefrescarTokensAsync("refresh-token", CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var form = Formulario(handler.Cuerpos.Single());
        form["grant_type"].Should().Be("refresh_token");
        form["client_assertion"].Should().NotBeNullOrWhiteSpace();
        form["client_secret"].Should().BeNull();
    }

    [Fact]
    public async Task El_aserto_lleva_las_reclamaciones_y_la_cabecera_que_exige_Entra_y_su_firma_verifica_con_el_certificado()
    {
        var (cliente, handler) = CrearCliente(ConCertificado());
        var antes = DateTimeOffset.UtcNow.AddSeconds(-2);

        await cliente.IntercambiarCodigoPorTokensAsync("codigo", "https://app.ejemplo.test/cb", CancellationToken.None);

        var aserto = Formulario(handler.Cuerpos.Single())["client_assertion"]!;
        var partes = aserto.Split('.');
        partes.Should().HaveCount(3);

        using var cabecera = JsonDocument.Parse(DeBase64Url(partes[0]));
        cabecera.RootElement.GetProperty("alg").GetString().Should().Be("RS256");
        cabecera.RootElement.GetProperty("typ").GetString().Should().Be("JWT");
        cabecera.RootElement.GetProperty("x5t").GetString().Should().Be(ABase64Url(_certificado.GetCertHash()));
        cabecera.RootElement.GetProperty("x5t#S256").GetString().Should().Be(ABase64Url(SHA256.HashData(_certificado.RawData)));

        using var cuerpo = JsonDocument.Parse(DeBase64Url(partes[1]));
        var raiz = cuerpo.RootElement;
        raiz.GetProperty("iss").GetString().Should().Be(ClientId);
        raiz.GetProperty("sub").GetString().Should().Be(ClientId);
        raiz.GetProperty("aud").GetString().Should().Be(EndpointToken, "Entra exige que la audiencia sea el endpoint de tokens al que se envía");
        Guid.TryParse(raiz.GetProperty("jti").GetString(), out _).Should().BeTrue();
        var nbf = DateTimeOffset.FromUnixTimeSeconds(raiz.GetProperty("nbf").GetInt64());
        var exp = DateTimeOffset.FromUnixTimeSeconds(raiz.GetProperty("exp").GetInt64());
        nbf.Should().BeOnOrAfter(antes).And.BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(2));
        // Literal a propósito, no la constante del código: comparar Vigencia contra sí misma
        // no detectaría que alguien la subiera (mutación 60 min: verde con la versión anterior).
        (exp - nbf).Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(10), "Entra rechaza asertos que caducan a más de ~10 min")
            .And.BeGreaterThan(TimeSpan.FromMinutes(1), "un aserto casi caducado ya al firmarlo fallaría por desfase de reloj");

        // Lo esencial: la firma se verifica con la parte PÚBLICA del certificado.
        using var publica = _certificado.GetRSAPublicKey()!;
        publica.VerifyData(
            Encoding.ASCII.GetBytes($"{partes[0]}.{partes[1]}"), DeBase64Url(partes[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .Should().BeTrue("un aserto que no verifica contra el certificado subido a Entra sería rechazado (AADSTS700027)");
    }

    [Fact]
    public async Task Cada_canje_genera_un_aserto_nuevo_con_jti_distinto()
    {
        var (cliente, handler) = CrearCliente(ConCertificado());

        await cliente.RefrescarTokensAsync("r1", CancellationToken.None);
        await cliente.RefrescarTokensAsync("r2", CancellationToken.None);

        var jtis = handler.Cuerpos
            .Select(c => Formulario(c)["client_assertion"]!.Split('.')[1])
            .Select(p => JsonDocument.Parse(DeBase64Url(p)).RootElement.GetProperty("jti").GetString())
            .ToList();
        jtis.Should().HaveCount(2).And.OnlyHaveUniqueItems("Entra rechaza la reutilización de un mismo aserto");
    }

    [Fact]
    public async Task Una_clave_privada_en_formato_PKCS1_tambien_vale()
    {
        File.WriteAllText(_rutaClave, _rsa.ExportRSAPrivateKeyPem());
        var (cliente, handler) = CrearCliente(ConCertificado());

        var resultado = await cliente.RefrescarTokensAsync("r", CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        Formulario(handler.Cuerpos.Single())["client_assertion"].Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Sin_certificado_el_comportamiento_de_siempre_se_conserva_client_secret_y_ningun_aserto()
    {
        var (cliente, handler) = CrearCliente(new Microsoft365GraphOptions
        {
            ClientId = ClientId, ClientSecret = "secreto-de-siempre", UrlPublicaBase = "https://app.ejemplo.test",
        });

        var resultado = await cliente.RefrescarTokensAsync("r", CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var form = Formulario(handler.Cuerpos.Single());
        form["client_secret"].Should().Be("secreto-de-siempre");
        form["client_assertion"].Should().BeNull();
        form["client_assertion_type"].Should().BeNull();
    }

    [Fact]
    public async Task Un_certificado_ilegible_falla_de_forma_controlada_sin_llamar_a_Microsoft_y_sin_filtrar_la_ruta()
    {
        File.WriteAllText(_rutaClave, "esto no es un PEM");
        var (cliente, handler) = CrearCliente(ConCertificado());

        var resultado = await cliente.RefrescarTokensAsync("r", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.ErrorAutenticacion");
        resultado.Error.Mensaje.Should().NotContain(_dir).And.NotContain("PEM");
        handler.Cuerpos.Should().BeEmpty("sin aserto no hay petición: no se envía nada a Entra");
    }

    [Fact]
    public async Task Un_fichero_de_certificado_inexistente_falla_de_forma_controlada()
    {
        var opciones = ConCertificado();
        opciones.CertificadoRuta = Path.Combine(_dir, "no-existe.pem");
        var (cliente, handler) = CrearCliente(opciones);

        var resultado = await cliente.IntercambiarCodigoPorTokensAsync("c", "https://app.ejemplo.test/cb", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        handler.Cuerpos.Should().BeEmpty();
    }

    [Theory]
    [InlineData("id", "secreto", null, null, "https://x", true)]        // secreto solo: como hoy
    [InlineData("id", null, "c.pem", "k.pem", "https://x", true)]       // certificado solo: nuevo
    [InlineData("id", "secreto", "c.pem", "k.pem", "https://x", true)]  // los dos: manda el certificado
    [InlineData("id", null, null, null, "https://x", false)]            // ninguna credencial
    [InlineData("id", "secreto", "c.pem", null, "https://x", false)]    // certificado a medias NO cae al secreto
    [InlineData("id", "secreto", null, "k.pem", "https://x", false)]
    [InlineData("id", null, "c.pem", null, "https://x", false)]
    [InlineData(null, null, "c.pem", "k.pem", "https://x", false)]      // sin ClientId
    [InlineData("id", null, "c.pem", "k.pem", null, false)]             // sin URL pública
    public void EstaConfigurado_admite_secreto_o_certificado_completo_y_trata_el_certificado_a_medias_como_error(
        string? clientId, string? secreto, string? cert, string? clave, string? url, bool esperado)
    {
        var opciones = new Microsoft365GraphOptions
        {
            ClientId = clientId, ClientSecret = secreto, CertificadoRuta = cert, ClavePrivadaRuta = clave, UrlPublicaBase = url,
        };

        opciones.EstaConfigurado.Should().Be(esperado);
    }
}
