using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Comunicaciones;

/// <summary>
/// El origen del texto de un Mensaje y la sugerencia de la que parte van juntos: una sugerencia revisada se nombra, y un
/// texto de persona no nombra ninguna. Es lo que permite demostrar después qué contenido partió de Vega.
/// </summary>
public class OrigenTextoMensajeTests
{
    private static readonly DateTime Fecha = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

    private static Mensaje Crear(
        DireccionMensaje direccion = DireccionMensaje.Saliente, OrigenTextoMensaje? origen = null, Guid? sugerenciaId = null) =>
        origen is null
            ? new Mensaje(Guid.NewGuid(), direccion, CanalConversacion.Correo, "cae@buzon.test", "<p>Hola</p>", Fecha)
            : new Mensaje(
                Guid.NewGuid(), direccion, CanalConversacion.Correo, "cae@buzon.test", "<p>Hola</p>", Fecha,
                origenTexto: origen.Value, respuestaSugeridaId: sugerenciaId);

    [Theory]
    [InlineData(DireccionMensaje.Entrante)]
    [InlineData(DireccionMensaje.Saliente)]
    public void Por_defecto_el_texto_es_de_una_persona_y_no_nombra_sugerencia(DireccionMensaje direccion)
    {
        var mensaje = Crear(direccion);

        mensaje.OrigenTexto.Should().Be(OrigenTextoMensaje.Persona);
        mensaje.RespuestaSugeridaId.Should().BeNull();
    }

    [Fact]
    public void El_valor_cero_del_enum_es_Persona_porque_es_el_de_las_filas_anteriores_a_la_marca()
    {
        ((int)OrigenTextoMensaje.Persona).Should().Be(0);
        default(OrigenTextoMensaje).Should().Be(OrigenTextoMensaje.Persona);
    }

    [Theory]
    [InlineData(OrigenTextoMensaje.MacroRevisada)]
    [InlineData(OrigenTextoMensaje.IaRevisada)]
    public void Un_saliente_que_parte_de_una_sugerencia_revisada_la_nombra(OrigenTextoMensaje origen)
    {
        var sugerenciaId = Guid.NewGuid();

        var mensaje = Crear(origen: origen, sugerenciaId: sugerenciaId);

        mensaje.OrigenTexto.Should().Be(origen);
        mensaje.RespuestaSugeridaId.Should().Be(sugerenciaId);
    }

    [Theory]
    [InlineData(OrigenTextoMensaje.MacroRevisada)]
    [InlineData(OrigenTextoMensaje.IaRevisada)]
    public void Una_sugerencia_revisada_sin_Id_de_sugerencia_se_rechaza(OrigenTextoMensaje origen)
    {
        var crear = () => Crear(origen: origen, sugerenciaId: null);

        crear.Should().Throw<ArgumentException>().WithParameterName("respuestaSugeridaId");
    }

    [Fact]
    public void Un_texto_de_persona_con_Id_de_sugerencia_se_rechaza()
    {
        var crear = () => Crear(origen: OrigenTextoMensaje.Persona, sugerenciaId: Guid.NewGuid());

        crear.Should().Throw<ArgumentException>().WithParameterName("respuestaSugeridaId");
    }

    [Fact]
    public void Un_Id_de_sugerencia_vacio_se_rechaza_como_si_faltara()
    {
        var crear = () => Crear(origen: OrigenTextoMensaje.IaRevisada, sugerenciaId: Guid.Empty);

        crear.Should().Throw<ArgumentException>().WithParameterName("respuestaSugeridaId");
    }

    [Fact]
    public void Un_entrante_no_puede_partir_de_una_sugerencia()
    {
        var crear = () => Crear(DireccionMensaje.Entrante, OrigenTextoMensaje.IaRevisada, Guid.NewGuid());

        crear.Should().Throw<ArgumentException>().WithParameterName("origenTexto");
    }

    [Fact]
    public void Un_origen_fuera_del_enum_se_rechaza()
    {
        var crear = () => Crear(origen: (OrigenTextoMensaje)99, sugerenciaId: Guid.NewGuid());

        crear.Should().Throw<ArgumentException>().WithParameterName("origenTexto");
    }

    [Fact]
    public void AgregarMensaje_pasa_el_origen_y_la_sugerencia_al_mensaje()
    {
        var conversacion = new Conversacion("Documentación", clienteId: Guid.NewGuid());
        var sugerenciaId = Guid.NewGuid();

        var mensaje = conversacion.AgregarMensaje(
            DireccionMensaje.Saliente, CanalConversacion.Correo, "cae@buzon.test", "<p>Hola</p>",
            origenTexto: OrigenTextoMensaje.IaRevisada, respuestaSugeridaId: sugerenciaId);

        mensaje.OrigenTexto.Should().Be(OrigenTextoMensaje.IaRevisada);
        mensaje.RespuestaSugeridaId.Should().Be(sugerenciaId);
    }
}