using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Alertas;
using FluentAssertions;

namespace CaeManager.Web.Tests.Email;

/// <summary>
/// <c>AlertasPorCorreo:UrlBase</c> no tiene validador de arranque. Una URL que no sea absoluta http(s)
/// se trata como «sin configurar»: el resumen sale sin enlace, en vez de abortar el resumen del Tenant
/// (el botón rechaza con excepción los destinos que no son http absolutos) o de escribir un enlace roto.
/// </summary>
public class EnvioAlertasVencimientoCorreoTests
{
    private static readonly IReadOnlyList<AlertaDto> Alertas =
    [
        new AlertaDto(
            Guid.NewGuid(), Guid.NewGuid(), "Marta Gómez", Guid.NewGuid(), "ITA", new DateOnly(2026, 12, 1),
            EstadoDocumento.Proximo, null, "Centro Norte"),
    ];

    [Theory]
    [InlineData("https://app.talveg.es", "https://app.talveg.es/alertas")]
    [InlineData("https://app.talveg.es/", "https://app.talveg.es/alertas")]
    [InlineData("  https://app.talveg.es/  ", "https://app.talveg.es/alertas")]
    [InlineData("http://localhost:5000", "http://localhost:5000/alertas")]
    public void Con_una_URL_base_http_valida_el_resumen_lleva_el_enlace_a_Alertas(string urlBase, string esperado)
    {
        var (_, cuerpo) = EnvioAlertasVencimientoHostedService.ConstruirCorreo(Alertas, urlBase);

        cuerpo.Should().Contain($"<a href=\"{esperado}\"");
        cuerpo.Should().Contain("Ver el detalle en Alertas");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("app.talveg.es")]
    [InlineData("no es una url")]
    [InlineData("/alertas")]
    [InlineData("ftp://app.talveg.es")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:info@talveg.es")]
    public void Con_una_URL_base_ausente_o_invalida_el_resumen_sale_sin_enlace_y_sin_lanzar(string? urlBase)
    {
        var construir = () => EnvioAlertasVencimientoHostedService.ConstruirCorreo(Alertas, urlBase);

        var (asunto, cuerpo) = construir.Should().NotThrow().Subject;

        cuerpo.Should().NotContainEquivalentOf("<a ").And.NotContainEquivalentOf("href");
        cuerpo.Should().Contain("Resumen diario de documentación pendiente");
        asunto.Should().Contain("1 alerta(s)");
    }
}
