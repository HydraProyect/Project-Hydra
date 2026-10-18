using CaeManager.Domain.Comunicaciones;
using FluentAssertions;

namespace CaeManager.Domain.Tests;

public class IntencionConversacionTests
{
    [Theory]
    [InlineData(ConfianzaIntencion.Baja, false)]
    [InlineData(ConfianzaIntencion.Media, false)]
    [InlineData(ConfianzaIntencion.Alta, true)]
    public void Solo_cuentan_propuestas_de_confianza_alta_o_confirmadas(ConfianzaIntencion confianza, bool cuenta)
    {
        var intencion = new IntencionConversacion(Guid.NewGuid(), Guid.NewGuid(), IntencionCae.Otras, confianza);
        intencion.CuentaParaMetricas.Should().Be(cuenta);
        intencion.ConfirmadaPorPersona.Should().BeFalse();
        intencion.Confirmar(IntencionCae.Otras);
        intencion.CuentaParaMetricas.Should().BeTrue();
        intencion.CambiadaPorPersona.Should().BeFalse();
    }

    [Fact]
    public void La_correccion_conserva_la_propuesta_y_su_confianza()
    {
        var intencion = new IntencionConversacion(Guid.NewGuid(), Guid.NewGuid(), IntencionCae.Otras, ConfianzaIntencion.Baja);
        intencion.Confirmar(IntencionCae.UrgenciaAcceso);
        intencion.IntencionPropuesta.Should().Be(IntencionCae.Otras);
        intencion.IntencionActual.Should().Be(IntencionCae.UrgenciaAcceso);
        intencion.Confianza.Should().Be(ConfianzaIntencion.Baja);
        intencion.CambiadaPorPersona.Should().BeTrue();
        intencion.CuentaParaMetricas.Should().BeTrue();
    }

    [Fact]
    public void Rechaza_intenciones_fuera_del_catalogo()
    {
        var crear = () => new IntencionConversacion(Guid.NewGuid(), Guid.NewGuid(), (IntencionCae)99, ConfianzaIntencion.Alta);
        crear.Should().Throw<ArgumentOutOfRangeException>();
        var intencion = new IntencionConversacion(Guid.NewGuid(), Guid.NewGuid(), IntencionCae.Otras, ConfianzaIntencion.Baja);
        var confirmar = () => intencion.Confirmar((IntencionCae)0);
        confirmar.Should().Throw<ArgumentOutOfRangeException>();
        intencion.ConfirmadaPorPersona.Should().BeFalse();
    }
}
