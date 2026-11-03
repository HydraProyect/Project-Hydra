using System.Security.Cryptography;
using System.Text;
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

    [Theory]
    [InlineData("<p>Hola</p>  <p>Ana</p>", "Hola Ana")]
    [InlineData("Hola\r\n\tAna", "Hola Ana")]
    [InlineData("a &lt; b &amp; c", "a < b & c")]
    [InlineData("<br/>", "")]
    [InlineData("5<10", "5<10")]
    [InlineData("5<10 y 7>2", "5<10 y 7>2")]
    [InlineData("a < b", "a < b")]
    [InlineData("<sin cierre", "<sin cierre")]
    [InlineData("<!-- nota -->Hola", "Hola")]
    [InlineData("Hola</p>", "Hola")]
    public void Normaliza_sin_html_y_con_los_espacios_colapsados(string texto, string esperado) =>
        RespuestaSugerida.NormalizarTexto(texto).Should().Be(esperado);

    [Theory]
    [InlineData("re<b>ci</b>bido")]
    [InlineData("re<B>ci</B>bido")]
    [InlineData("re<i>ci</i>bido")]
    [InlineData("re<em>ci</em>bido")]
    [InlineData("re<strong>ci</strong>bido")]
    [InlineData("re<u>ci</u>bido")]
    [InlineData("re<span style=\"color:red\">ci</span>bido")]
    [InlineData("re<a href=\"https://example.test\">ci</a>bido")]
    [InlineData("re<font color=\"red\">ci</font>bido")]
    [InlineData("re<mark>ci</mark>bido")]
    [InlineData("re<small>ci</small>bido")]
    [InlineData("re<sub>ci</sub>bido")]
    [InlineData("re<sup>ci</sup>bido")]
    public void Una_etiqueta_en_linea_no_parte_la_palabra(string texto) =>
        RespuestaSugerida.NormalizarTexto(texto).Should().Be("recibido");

    [Theory]
    [InlineData("uno<p>dos", "uno dos")]
    [InlineData("uno<br>dos", "uno dos")]
    [InlineData("uno<div>dos</div>", "uno dos")]
    [InlineData("uno<strike>dos", "uno dos")]
    [InlineData("uno<b1>dos", "uno dos")]
    [InlineData("uno<!-- b -->dos", "uno dos")]
    public void Cualquier_otra_etiqueta_sigue_separando(string texto, string esperado) =>
        RespuestaSugerida.NormalizarTexto(texto).Should().Be(esperado);

    [Fact]
    public void El_hash_de_lo_generado_y_de_lo_enviado_coinciden_con_una_negrita_dentro_de_una_palabra()
    {
        var sugerencia = RespuestaSugerida.DeVega(Guid.NewGuid(), Guid.NewGuid(), "<p>Recibido</p>", Guid.NewGuid(), DateTime.UtcNow);

        RespuestaSugerida.CalcularTextoHash("<p>Reci<b>bi</b>do</p>").Should().Be(sugerencia.TextoHash);
    }

    [Fact]
    public void Muchas_etiquetas_sin_cerrar_se_normalizan_en_tiempo_lineal()
    {
        var texto = string.Concat(Enumerable.Repeat("<b", 200_000)) + ">";

        RespuestaSugerida.NormalizarTexto(texto).Should().BeEmpty();

        var sinCierre = string.Concat(Enumerable.Repeat("<b", 200_000));
        RespuestaSugerida.NormalizarTexto(sinCierre).Should().Be(sinCierre);
    }

    [Fact]
    public void El_hash_es_el_sha256_del_texto_normalizado_y_se_fija_al_generar()
    {
        var sugerencia = RespuestaSugerida.DeVega(Guid.NewGuid(), Guid.NewGuid(), "<p>Recibido,</p>\n<p>gracias</p>", Guid.NewGuid(), DateTime.UtcNow);

        sugerencia.TextoHash.Should().Be(RespuestaSugerida.CalcularTextoHash("Recibido, gracias"));
        sugerencia.TextoHash.Should().Be(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("Recibido, gracias"))));
        sugerencia.TextoHash.Should().HaveLength(RespuestaSugerida.LongitudTextoHash);
    }

    [Fact]
    public void Un_menor_que_escapado_y_uno_escrito_dan_el_mismo_hash() =>
        RespuestaSugerida.CalcularTextoHash("<p>5&lt;10</p>").Should().Be(RespuestaSugerida.CalcularTextoHash("5<10"));
}
