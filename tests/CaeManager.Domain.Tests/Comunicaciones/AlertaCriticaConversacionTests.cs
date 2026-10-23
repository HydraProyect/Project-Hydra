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
    [InlineData("⚠️ 📩 Nos notifican una sanción.")]
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

    [Fact]
    public void Deshacer_la_derivacion_devuelve_la_alerta_a_Pendiente_y_suelta_la_Incidencia()
    {
        var alerta = new AlertaCriticaConversacion(Mensaje("denuncia"), "denuncia", "denuncia", Guid.NewGuid(), DateTime.UtcNow);
        var persona = Guid.NewGuid();
        var incidencia = Guid.NewGuid();
        alerta.Resolver(true, persona, DateTime.UtcNow, incidencia);
        alerta.IncidenciaId.Should().Be(incidencia);

        alerta.DeshacerDerivacion(incidencia, persona);

        alerta.Estado.Should().Be(EstadoAlertaCritica.Pendiente);
        alerta.IncidenciaId.Should().BeNull();
        alerta.DecisionPorUsuarioId.Should().BeNull();
        alerta.DecisionEnUtc.Should().BeNull();
        // Vuelve a poder decidirse, también como «No es crítica».
        alerta.Resolver(false, persona, DateTime.UtcNow);
        alerta.Estado.Should().Be(EstadoAlertaCritica.Descartada);
    }

    [Fact]
    public void Solo_se_deshace_la_derivacion_propia_con_su_Incidencia()
    {
        var persona = Guid.NewGuid();
        var incidencia = Guid.NewGuid();
        var derivada = new AlertaCriticaConversacion(Mensaje("denuncia"), "denuncia", "denuncia", Guid.NewGuid(), DateTime.UtcNow);
        derivada.Resolver(true, persona, DateTime.UtcNow, incidencia);
        derivada.Invoking(a => a.DeshacerDerivacion(Guid.NewGuid(), persona)).Should().Throw<InvalidOperationException>();
        derivada.Invoking(a => a.DeshacerDerivacion(incidencia, Guid.NewGuid())).Should().Throw<InvalidOperationException>();

        var sinIncidencia = new AlertaCriticaConversacion(Mensaje("denuncia"), "denuncia", "denuncia", Guid.NewGuid(), DateTime.UtcNow);
        sinIncidencia.Resolver(true, persona, DateTime.UtcNow);
        sinIncidencia.Invoking(a => a.DeshacerDerivacion(incidencia, persona)).Should().Throw<InvalidOperationException>();

        var pendiente = new AlertaCriticaConversacion(Mensaje("denuncia"), "denuncia", "denuncia", Guid.NewGuid(), DateTime.UtcNow);
        pendiente.Invoking(a => a.DeshacerDerivacion(incidencia, persona)).Should().Throw<InvalidOperationException>();
        pendiente.Invoking(a => a.Resolver(false, persona, DateTime.UtcNow, incidencia)).Should().Throw<ArgumentException>();
        pendiente.Estado.Should().Be(EstadoAlertaCritica.Pendiente);
    }

    [Fact]
    public void Conserva_el_texto_original_incluso_al_final_de_un_mensaje_largo()
    {
        var resultado = DetectorExpresionesCriticas.Detectar(Mensaje(new string('x', 6000) + " Nos visitará la Inspección de Trabajo mañana."));
        resultado.Should().NotBeNull();
        resultado!.Texto.Should().Contain("Inspección de Trabajo");
        resultado.Texto.Length.Should().BeLessThanOrEqualTo(4000);
    }

    [Fact]
    public void El_dominio_rechaza_alertas_sobre_mensajes_salientes()
    {
        Action crear = () => new AlertaCriticaConversacion(Mensaje("denuncia", DireccionMensaje.Saliente), "denuncia", "denuncia", Guid.NewGuid(), DateTime.UtcNow);
        crear.Should().Throw<ArgumentException>();
    }
}
