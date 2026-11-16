using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Comunicaciones;

/// <summary>
/// Origen del texto de un mensaje (issue #2007): solo un saliente de una persona lo tiene, se fija una vez, y solo una
/// sugerencia revisada de la misma conversación puede ser su origen. Lo que importa para el correo es
/// <see cref="Mensaje.ContenidoGeneradoPorIa"/>, verdadero solo con Vega.
/// </summary>
public class MensajeOrigenContenidoTests
{
    private static readonly DateTime Ahora = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

    private static Mensaje Saliente(Guid conversacionId, bool esAutomatico = false) =>
        new(conversacionId, DireccionMensaje.Saliente, CanalConversacion.Correo, "cae@empresa.test", "<p>Recibido</p>", Ahora,
            esAutomatico: esAutomatico);

    private static RespuestaSugerida Revisada(Guid conversacionId, bool deVega = true)
    {
        var sugerencia = deVega
            ? RespuestaSugerida.DeVega(conversacionId, Guid.NewGuid(), "<p>Recibido</p>", Guid.NewGuid(), Ahora)
            : RespuestaSugerida.DesdeMacro(conversacionId, Guid.NewGuid(), Guid.NewGuid(), "<p>Recibido</p>", Guid.NewGuid(), Ahora);
        sugerencia.Insertar();
        sugerencia.Confirmar(Guid.NewGuid(), Ahora);
        return sugerencia;
    }

    [Fact]
    public void Un_mensaje_nuevo_no_tiene_origen_hasta_que_se_fija()
    {
        var mensaje = Saliente(Guid.NewGuid());

        mensaje.OrigenContenido.Should().BeNull();
        mensaje.RespuestaSugeridaId.Should().BeNull();
        mensaje.ContenidoGeneradoPorIa.Should().BeFalse();
    }

    [Fact]
    public void Escrito_a_mano_es_Persona_sin_sugerencia_y_sin_marca_de_IA()
    {
        var mensaje = Saliente(Guid.NewGuid());

        mensaje.RegistrarOrigenPersona();

        mensaje.OrigenContenido.Should().Be(OrigenContenidoMensaje.Persona);
        mensaje.RespuestaSugeridaId.Should().BeNull();
        mensaje.ContenidoGeneradoPorIa.Should().BeFalse();
    }

    [Fact]
    public void Desde_una_sugerencia_de_Vega_revisada_es_VegaRevisada_con_su_identificador_y_marca_de_IA()
    {
        var conversacionId = Guid.NewGuid();
        var sugerencia = Revisada(conversacionId);
        var mensaje = Saliente(conversacionId);

        mensaje.RegistrarOrigenDesde(sugerencia);

        mensaje.OrigenContenido.Should().Be(OrigenContenidoMensaje.VegaRevisada);
        mensaje.RespuestaSugeridaId.Should().Be(sugerencia.Id);
        mensaje.ContenidoGeneradoPorIa.Should().BeTrue();
    }

    [Fact]
    public void Desde_una_macro_revisada_es_MacroRevisada_sin_marca_de_IA()
    {
        var conversacionId = Guid.NewGuid();
        var sugerencia = Revisada(conversacionId, deVega: false);
        var mensaje = Saliente(conversacionId);

        mensaje.RegistrarOrigenDesde(sugerencia);

        mensaje.OrigenContenido.Should().Be(OrigenContenidoMensaje.MacroRevisada);
        mensaje.RespuestaSugeridaId.Should().Be(sugerencia.Id);
        mensaje.ContenidoGeneradoPorIa.Should().BeFalse();
    }

    [Theory]
    [InlineData(EstadoRespuestaSugerida.Propuesta)]
    [InlineData(EstadoRespuestaSugerida.SinRevisar)]
    public void Una_sugerencia_no_revisada_no_puede_ser_el_origen(EstadoRespuestaSugerida estado)
    {
        var conversacionId = Guid.NewGuid();
        var sugerencia = RespuestaSugerida.DeVega(conversacionId, Guid.NewGuid(), "<p>Recibido</p>", Guid.NewGuid(), Ahora);
        if (estado == EstadoRespuestaSugerida.SinRevisar) sugerencia.Insertar();
        var mensaje = Saliente(conversacionId);

        var registrar = () => mensaje.RegistrarOrigenDesde(sugerencia);

        registrar.Should().Throw<InvalidOperationException>();
        mensaje.OrigenContenido.Should().BeNull();
    }

    /// <summary>
    /// Decisión de Chris del 2026-11-16 (#2007): el origen de IA se conserva mientras el texto siga en el redactor, aunque la
    /// sugerencia se descarte o se sustituya después de insertarla.
    /// </summary>
    [Fact]
    public void Una_sugerencia_de_Vega_descartada_tras_insertarla_es_el_origen_VegaRevisada()
    {
        var conversacionId = Guid.NewGuid();
        var sugerencia = RespuestaSugerida.DeVega(conversacionId, Guid.NewGuid(), "<p>Recibido</p>", Guid.NewGuid(), Ahora);
        sugerencia.Insertar();
        sugerencia.Descartar();
        var mensaje = Saliente(conversacionId);

        mensaje.RegistrarOrigenDesde(sugerencia);

        mensaje.OrigenContenido.Should().Be(OrigenContenidoMensaje.VegaRevisada);
        mensaje.RespuestaSugeridaId.Should().Be(sugerencia.Id);
        mensaje.ContenidoGeneradoPorIa.Should().BeTrue();
    }

    [Fact]
    public void Un_mensaje_nuevo_puede_partir_de_la_sugerencia_revisada_de_otro_hilo()
    {
        var mensaje = Saliente(Guid.NewGuid());
        var sugerencia = Revisada(Guid.NewGuid());

        mensaje.RegistrarOrigenDesde(sugerencia);

        mensaje.OrigenContenido.Should().Be(OrigenContenidoMensaje.VegaRevisada);
        mensaje.RespuestaSugeridaId.Should().Be(sugerencia.Id);
    }

    [Fact]
    public void El_origen_se_fija_una_sola_vez()
    {
        var conversacionId = Guid.NewGuid();
        var mensaje = Saliente(conversacionId);
        mensaje.RegistrarOrigenDesde(Revisada(conversacionId));

        var aPersona = () => mensaje.RegistrarOrigenPersona();
        var aOtra = () => mensaje.RegistrarOrigenDesde(Revisada(conversacionId));

        aPersona.Should().Throw<InvalidOperationException>();
        aOtra.Should().Throw<InvalidOperationException>();
        mensaje.OrigenContenido.Should().Be(OrigenContenidoMensaje.VegaRevisada);
    }

    [Fact]
    public void Un_entrante_no_tiene_origen()
    {
        var entrante = new Mensaje(
            Guid.NewGuid(), DireccionMensaje.Entrante, CanalConversacion.Correo, "ana@contrata.test", "<p>Adjunto</p>", Ahora);

        var registrar = () => entrante.RegistrarOrigenPersona();

        registrar.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Un_saliente_automatico_no_tiene_origen()
    {
        var acuse = Saliente(Guid.NewGuid(), esAutomatico: true);

        var registrar = () => acuse.RegistrarOrigenPersona();

        registrar.Should().Throw<InvalidOperationException>();
    }
}
