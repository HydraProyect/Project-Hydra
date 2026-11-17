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

    /// <summary>
    /// Seguimiento de la #2319 (issue #2124): el botón Confirmar del aviso del hueco sale de dos fuentes, porque la regla de
    /// Architecture no deja a un Boton de Comunicaciones usar este .resx: Conexiones y el diálogo usan
    /// <c>TextosConexiones.AccionConfirmarAvisoHueco</c> y Buzón usa <c>TextosComunes.AccionConfirmar</c>. Aquí se fija que
    /// dicen lo mismo en cada cultura, en imperativo en catalán (como «Cancel·la»), y que el texto del aviso nombra el botón
    /// con ese mismo verbo.
    /// </summary>
    [Theory]
    [InlineData("es-ES", "Confirmar", "Cancelar")]
    [InlineData("ca-ES", "Confirma", "Cancel·la")]
    public void El_boton_Confirmar_del_aviso_del_hueco_dice_lo_mismo_en_Conexiones_y_en_Buzon(string cultura, string confirmar, string cancelar)
    {
        var cultureInfo = CultureInfo.GetCultureInfo(cultura);
        var comunes = new ResourceManager(typeof(CaeManager.Web.Recursos.TextosComunes));

        Recursos.GetString("AccionConfirmarAvisoHueco", cultureInfo).Should().Be(confirmar);
        comunes.GetString("AccionConfirmar", cultureInfo).Should().Be(confirmar, "Buzón lo toma de TextosComunes y debe coincidir con Conexiones");
        Recursos.GetString("AccionCancelarAvisoHueco", cultureInfo).Should().Be(cancelar, "el otro botón del diálogo va en el mismo modo verbal");
        Recursos.GetString("AvisoCorreoNoRecuperadoConfirmable", cultureInfo).Should().Contain(" " + confirmar + ";", "el aviso nombra el botón real");
    }
}
