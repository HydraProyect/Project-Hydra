using System.Globalization;
using System.Resources;
using CaeManager.Application.Integraciones.Commands.ConectarBuzonMicrosoft365;
using CaeManager.Web.Features.Integraciones.Recursos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// El aviso de «compartidos-en-pausa» de Conexiones sale de los recursos, como sus vecinos, en las dos culturas. El texto
/// en castellano es el mismo mensaje fijo que devuelve el alta: sin datos de otro Tenant propietario.
/// </summary>
public class TextosConexionesTests
{
    private static readonly ResourceManager Recursos = new(typeof(TextosConexiones));

    [Fact]
    public void El_aviso_de_alta_compartida_en_pausa_esta_en_castellano_y_en_catalan()
    {
        Recursos.GetString("ErrorAltaCompartidaEnPausa", CultureInfo.GetCultureInfo("es-ES"))
            .Should().Be(ConectarBuzonMicrosoft365CommandHandler.MensajeAltaCompartidaEnPausa);

        var catalan = Recursos.GetString("ErrorAltaCompartidaEnPausa", CultureInfo.GetCultureInfo("ca-ES"));
        catalan.Should().NotBeNullOrWhiteSpace();
        catalan.Should().NotBe(ConectarBuzonMicrosoft365CommandHandler.MensajeAltaCompartidaEnPausa);
    }
}
