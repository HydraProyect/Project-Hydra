using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Higiene de exposición de red (auditoría IT, S-D-6 y S-D-10): el Postgres de
/// desarrollo con credenciales públicas solo escucha en loopback, y la app
/// desplegada filtra el Host en vez de aceptar cualquiera (<c>AllowedHosts: "*"</c>
/// queda solo como valor de desarrollo en appsettings.json).
/// </summary>
public class ExposicionDeRedDelDespliegueTests
{
    [Fact]
    public void El_postgres_de_desarrollo_solo_publica_su_puerto_en_loopback()
    {
        var puertos = File.ReadAllLines(Path.Combine(RaizDelRepositorio(), "docker-compose.yml"))
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#') && l.StartsWith("- \"", StringComparison.Ordinal) && l.Contains(':'))
            .ToList();

        puertos.Should().NotBeEmpty("el compose de desarrollo publica el puerto de Postgres");
        puertos.Should().OnlyContain(l => l.StartsWith("- \"127.0.0.1:", StringComparison.Ordinal),
            "postgres/postgres es una credencial pública: publicar en todas las interfaces expone la base de " +
            "desarrollo a la red local del equipo del desarrollador.");
    }

    [Theory]
    [InlineData("docker-compose.produccion.yml")]
    [InlineData("docker-compose.staging.yml")]
    public void La_app_desplegada_filtra_el_host_y_nunca_acepta_cualquiera(string fichero)
    {
        var lineasActivas = File.ReadAllLines(Path.Combine(RaizDelRepositorio(), "deploy", "local", fichero))
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#'))
            .Where(l => l.StartsWith("AllowedHosts:", StringComparison.Ordinal))
            .ToList();

        lineasActivas.Should().ContainSingle(
            "el servicio app-azul (del que hereda app-verde) fija AllowedHosts por entorno");
        lineasActivas[0].Should().NotContain("*", "un comodín deja la app aceptando cualquier cabecera Host");
        lineasActivas[0].Should().Contain("localhost",
            "el healthcheck del contenedor y las sondas por docker exec llaman a http://localhost:8080/salud");
        lineasActivas[0].Should().Contain(fichero.Contains("produccion", StringComparison.Ordinal) ? "${DOMINIO}" : "staging.talveg.es",
            "sin el dominio público en AllowedHosts la app rechazaría con 400 todo el tráfico que llega por Caddy");
    }

    /// <summary>S-C-3: el cliente de WhatsApp Cloud API se registra con el manejador sin redirecciones.</summary>
    [Fact]
    public void El_cliente_http_de_whatsapp_se_registra_sin_seguir_redirecciones()
    {
        var registro = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Infrastructure",
            "DependencyInjection", "InfrastructureServiceCollectionExtensions.cs"));

        registro.Should().Contain("ConfigurePrimaryHttpMessageHandler(WhatsAppCloudApiClient.CrearManejadorSinRedirecciones)",
            "el HttpClient de WhatsApp lleva el Bearer de la línea; no debe seguir un 3xx a un destino sin validar.");
    }

    /// <summary>
    /// El cliente de CTAIMACAE.net lleva la clave de suscripción en una cabecera propia, que .NET reenvía al
    /// seguir un 3xx (solo limpia <c>Authorization</c>), y el alta no es idempotente del otro lado: sin
    /// redirecciones, sin el pipeline de reintentos y con el valor de todas las cabeceras tachado en el log.
    /// </summary>
    [Fact]
    public void El_cliente_http_de_ctaima_no_sigue_redirecciones_no_reintenta_y_tacha_sus_cabeceras()
    {
        var contenido = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Infrastructure",
            "DependencyInjection", "InfrastructureServiceCollectionExtensions.cs"));

        var inicio = contenido.IndexOf("AddHttpClient<CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Ctaima.CtaimaCaeClient>", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(0, "el cliente de CTAIMACAE.net se registra con AddHttpClient en este fichero");
        var fin = contenido.IndexOf("AddTransient<CaeManager.Application.Integraciones.ConectoresPlataformaCae.IConectorPlataformaCae", inicio, StringComparison.Ordinal);
        fin.Should().BeGreaterThan(inicio, "el conector se registra justo después de su cliente");
        var registro = contenido[inicio..fin];

        registro.Should().Contain("ConfigurePrimaryHttpMessageHandler(CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Ctaima.CtaimaCaeClient.CrearManejadorSinRedirecciones)");
        // Tachado total con predicado: RedactLoggedHeaders(lista) sustituye el predicado por defecto de .NET 9+
        // (que tacha todo) y dejaría en claro Set-Cookie, Location u Ocp-Apim-Trace-Location.
        registro.Should().Contain(".RedactLoggedHeaders(_ => true)",
            "el log del HttpClient de CTAIMACAE.net tacha el valor de todas las cabeceras, no solo la clave y el tenantId");
        registro.Split("RedactLoggedHeaders(").Length.Should().Be(2,
            "una segunda llamada a RedactLoggedHeaders, con lista o predicado, sustituiría el tachado total");
        registro.Should().NotContain("AplicarResilienciaHttp", "un reintento tras un timeout puede dar de alta dos veces al mismo Trabajador");
        registro.Should().NotContain("AddStandardResilienceHandler");

        // Fuera del tramo: un segundo AddHttpClient<CtaimaCaeClient> vuelve al mismo cliente con nombre y su
        // RedactLoggedHeaders o su resiliencia ganarían; un ConfigureHttpClientDefaults aplicaría reintentos o un
        // tachado parcial a todos los clientes, este incluido.
        System.Text.RegularExpressions.Regex.Matches(contenido, @"AddHttpClient<[\w.]*CtaimaCaeClient>").Count.Should().Be(1,
            "el cliente de CTAIMACAE.net se configura en un único registro");
        Directory.EnumerateFiles(Path.Combine(RaizDelRepositorio(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("ConfigureHttpClientDefaults", StringComparison.Ordinal))
            .Should().BeEmpty("una configuración por defecto de todos los HttpClient alcanzaría también al de CTAIMACAE.net");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
