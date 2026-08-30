using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// Auditoría módulo 6 (ampliada por un hallazgo del módulo 1 sobre
/// GraphEmailService): los logs de aplicación no son un canal protegido ni
/// de retención corta, así que un error de Microsoft Graph/Entra ID solo
/// debe dejar en ellos el código, nunca el mensaje completo — que no está
/// bajo control de Hydra y puede repetir direcciones u otros datos.
/// </summary>
public class CodigoErrorGraphTests
{
    [Fact]
    public void Extrae_el_codigo_de_un_error_estandar_de_graph()
    {
        const string cuerpo = """{"error":{"code":"ErrorInvalidRecipients","message":"El destinatario juan.perez@cliente-real.example no existe."}}""";

        var codigo = CodigoErrorGraph.Extraer(cuerpo);

        codigo.Should().Be("ErrorInvalidRecipients");
        codigo.Should().NotContain("juan.perez");
    }

    [Theory]
    [InlineData("")]
    [InlineData("no es json")]
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData("{}")]
    [InlineData("""{"error":{}}""")]
    public void Devuelve_desconocido_si_no_puede_extraer_un_codigo(string cuerpo)
    {
        CodigoErrorGraph.Extraer(cuerpo).Should().Be("desconocido");
    }
}
