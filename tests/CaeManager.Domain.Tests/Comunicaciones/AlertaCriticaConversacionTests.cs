using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Comunicaciones;

public class AlertaCriticaConversacionTests
{
    private static Mensaje Mensaje(string texto, DireccionMensaje direccion = DireccionMensaje.Entrante) =>
        new(Guid.NewGuid(), direccion, CanalConversacion.Correo, "empresa@example.test", texto, DateTime.UtcNow);

    [Theory]
    [InlineData("Hemos recibido una visita de la Inspección de Trabajo.")]
    [InlineData("Ens ha arribat un requeriment oficial de la Inspecció de Treball.")]
    [InlineData("Nos notifican una sanción por incumplimiento.")]
    [InlineData("Hi ha una denúncia presentada contra l'empresa.")]
    [InlineData("Iniciaremos acciones legales si no recibimos respuesta.")]
    [InlineData("Iniciarem accions legals aquesta setmana.")]
    [InlineData("S'ha notificat una sanció.")]
    [InlineData("<p>INSPECCIÓN&nbsp;DE <b>TRABAJO</b></p>")]
    public void Detecta_casos_en_espanol_y_catalan(string texto) =>
        DetectorExpresionesCriticas.Detectar(Mensaje(texto)).Should().NotBeNull();

    [Theory]
    [InlineData("Adjunto la plantilla de inspección del equipo.")]
    [InlineData("La transacción legalizada está disponible.")]
    [InlineData("Ens veiem demà per revisar els documents.")]
    public void Evita_coincidencias_obvias(string texto) =>
        DetectorExpresionesCriticas.Detectar(Mensaje(texto)).Should().BeNull();

    [Fact]
    public void Una_plantilla_saliente_nunca_dispara_alerta() =>
        DetectorExpresionesCriticas.Detectar(Mensaje("Plantilla: inspección de trabajo y sanción", DireccionMensaje.Saliente)).Should().BeNull();

    [Fact]
    public void La_lista_del_Operador_Cae_sustituye_la_predeterminada()
    {
        DetectorExpresionesCriticas.Detectar(Mensaje("Avís del jutjat"), ["avís del jutjat"]).Should().NotBeNull();
        DetectorExpresionesCriticas.Detectar(Mensaje("Denuncia"), ["avís del jutjat"]).Should().BeNull();
    }

    [Fact]
    public void La_segunda_opinion_no_resuelve_la_alerta()
    {
        var alerta = new AlertaCriticaConversacion(Mensaje("denuncia"), "denuncia", "denuncia", Guid.NewGuid(), DateTime.UtcNow);
        alerta.AnotarSegundaOpinion(false, 92);
        alerta.Estado.Should().Be(EstadoAlertaCritica.Pendiente);
        alerta.DecisionPorUsuarioId.Should().BeNull();
        var persona = Guid.NewGuid();
        alerta.Resolver(true, persona, DateTime.UtcNow);
        alerta.DecisionPorUsuarioId.Should().Be(persona);
        alerta.Estado.Should().Be(EstadoAlertaCritica.ConfirmadaYDerivada);
        Action segundaDecision = () => alerta.Resolver(false, Guid.NewGuid(), DateTime.UtcNow);
        segundaDecision.Should().Throw<InvalidOperationException>();
    }
}
