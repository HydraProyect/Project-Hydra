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

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
