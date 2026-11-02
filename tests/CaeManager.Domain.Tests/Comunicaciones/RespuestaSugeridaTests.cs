using CaeManager.Domain.Comunicaciones;
using FluentAssertions;

namespace CaeManager.Domain.Tests.Comunicaciones;

public class RespuestaSugeridaTests
{
    private static readonly DateTime Ahora = new(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc);

    private static RespuestaSugerida Nueva() =>
        RespuestaSugerida.DeVega(Guid.NewGuid(), Guid.NewGuid(), "<p>Borrador</p>", Guid.NewGuid(), Ahora);

    private static RespuestaSugerida EnEstado(EstadoRespuestaSugerida estado)
    {
        var respuesta = Nueva();
        switch (estado)
        {
            case EstadoRespuestaSugerida.SinRevisar:
                respuesta.Insertar();
                break;
            case EstadoRespuestaSugerida.Revisada:
                respuesta.Insertar();
                respuesta.Confirmar(Guid.NewGuid(), Ahora);
                break;
            case EstadoRespuestaSugerida.Descartada:
                respuesta.Descartar();
                break;
        }

        return respuesta;
    }

    [Fact]
    public void Nace_propuesta_y_activa()
    {
        var respuesta = Nueva();

        respuesta.Estado.Should().Be(EstadoRespuestaSugerida.Propuesta);
        respuesta.EstaActiva.Should().BeTrue();
        respuesta.Origen.Should().Be(OrigenRespuestaSugerida.Vega);
        respuesta.MacroId.Should().BeNull();
        respuesta.RevisadaPorUsuarioId.Should().BeNull();
    }

    [Fact]
    public void Desde_una_macro_nombra_la_macro()
    {
        var macroId = Guid.NewGuid();

        var respuesta = RespuestaSugerida.DesdeMacro(Guid.NewGuid(), Guid.NewGuid(), macroId, "<p>Macro</p>", Guid.NewGuid(), Ahora);

        respuesta.Origen.Should().Be(OrigenRespuestaSugerida.Macro);
        respuesta.MacroId.Should().Be(macroId);
    }

    [Fact]
    public void Una_macro_vacia_no_vale_como_origen()
    {
        var crear = () => RespuestaSugerida.DesdeMacro(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, "<p>Macro</p>", Guid.NewGuid(), Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void El_texto_vacio_no_es_una_sugerencia(string texto)
    {
        var crear = () => RespuestaSugerida.DeVega(Guid.NewGuid(), Guid.NewGuid(), texto, Guid.NewGuid(), Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Sin_persona_que_la_pida_no_hay_sugerencia()
    {
        var crear = () => RespuestaSugerida.DeVega(Guid.NewGuid(), Guid.NewGuid(), "<p>Borrador</p>", Guid.Empty, Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Insertar_la_deja_sin_revisar_y_sigue_activa()
    {
        var respuesta = Nueva();

        respuesta.Insertar().EsExitoso.Should().BeTrue();

        respuesta.Estado.Should().Be(EstadoRespuestaSugerida.SinRevisar);
        respuesta.EstaActiva.Should().BeTrue();
    }

    [Fact]
    public void Confirmar_guarda_quien_y_cuando_y_deja_de_estar_activa()
    {
        var respuesta = EnEstado(EstadoRespuestaSugerida.SinRevisar);
        var usuarioId = Guid.NewGuid();

        respuesta.Confirmar(usuarioId, Ahora).EsExitoso.Should().BeTrue();

        respuesta.Estado.Should().Be(EstadoRespuestaSugerida.Revisada);
        respuesta.RevisadaPorUsuarioId.Should().Be(usuarioId);
        respuesta.RevisadaEnUtc.Should().Be(Ahora);
        respuesta.EstaActiva.Should().BeFalse();
    }

    [Theory]
    [InlineData(EstadoRespuestaSugerida.Propuesta)]
    [InlineData(EstadoRespuestaSugerida.SinRevisar)]
    public void Descartar_cabe_mientras_esta_activa(EstadoRespuestaSugerida estado)
    {
        var respuesta = EnEstado(estado);

        respuesta.Descartar().EsExitoso.Should().BeTrue();

        respuesta.Estado.Should().Be(EstadoRespuestaSugerida.Descartada);
        respuesta.RevisadaPorUsuarioId.Should().BeNull();
    }

    [Fact]
    public void Una_propuesta_no_se_confirma_sin_pasar_por_insertar()
    {
        var respuesta = Nueva();

        var resultado = respuesta.Confirmar(Guid.NewGuid(), Ahora);

        resultado.Error.Should().Be(RespuestaSugerida.ErrorTransicionNoPermitida);
        respuesta.Estado.Should().Be(EstadoRespuestaSugerida.Propuesta);
        respuesta.RevisadaPorUsuarioId.Should().BeNull();
    }

    [Fact]
    public void Una_sin_revisar_no_se_inserta_dos_veces()
    {
        var respuesta = EnEstado(EstadoRespuestaSugerida.SinRevisar);

        respuesta.Insertar().Error.Should().Be(RespuestaSugerida.ErrorTransicionNoPermitida);
    }

    [Theory]
    [InlineData(EstadoRespuestaSugerida.Revisada)]
    [InlineData(EstadoRespuestaSugerida.Descartada)]
    public void Nada_sale_de_un_estado_final(EstadoRespuestaSugerida estado)
    {
        var respuesta = EnEstado(estado);

        respuesta.Insertar().EsFallido.Should().BeTrue();
        respuesta.Confirmar(Guid.NewGuid(), Ahora.AddDays(1)).EsFallido.Should().BeTrue();
        respuesta.Descartar().EsFallido.Should().BeTrue();

        respuesta.Estado.Should().Be(estado);
    }
}
