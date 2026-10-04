using CaeManager.Application.Comunicaciones;
using FluentAssertions;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Sintaxis cerrada de huecos de una macro. Lo que fija: qué es un hueco (solo tres nombres exactos),
/// que un hueco sin elegir impide rellenar (nunca se infiere), que todo valor sale ESCAPADO y que una
/// macro sin huecos no se toca. No observa el sanitizador (lo aplica el componente) ni la pantalla.
/// </summary>
public class HuecosMacroTests
{
    private const string Cuerpo = "Solicito el alta de {{centro}} para {{trabajador}} con efecto desde {{fecha}}.";

    [Fact]
    public void Detecta_los_tres_huecos_en_orden_y_sin_repetir()
    {
        HuecosMacro.Detectar("{{fecha}} {{centro}} {{fecha}} {{trabajador}}")
            .Should().Equal(TipoHuecoMacro.Fecha, TipoHuecoMacro.Centro, TipoHuecoMacro.Trabajador);
    }

    [Theory]
    [InlineData("Hola, sin huecos.")]
    [InlineData("")]
    [InlineData("{{Centro}}")]            // mayúsculas: no es un hueco
    [InlineData("{{ centro }}")]          // espacios: no es un hueco
    [InlineData("{{usuario}}")]           // nombre fuera del conjunto cerrado
    [InlineData("{{centro|admin}}")]      // sin modificadores ni expresiones
    [InlineData("[CENTRO] y [FECHA]")]    // la sintaxis antigua escrita a mano sigue siendo texto
    public void Lo_que_no_es_exactamente_un_hueco_no_se_detecta(string cuerpo)
    {
        HuecosMacro.Detectar(cuerpo).Should().BeEmpty();
        HuecosMacro.TieneHuecos(cuerpo).Should().BeFalse();
    }

    [Fact]
    public void Con_todos_los_valores_rellena_y_formatea_la_fecha()
    {
        var r = HuecosMacro.Rellenar(Cuerpo, new ValoresHuecosMacro("Centro Norte", "M. Soto", new DateOnly(2026, 10, 12)));

        r.Pendientes.Should().BeEmpty();
        r.Texto.Should().Be("Solicito el alta de Centro Norte para M. Soto con efecto desde 12/10/2026.");
    }

    [Theory]
    [InlineData(null, "M. Soto", true, TipoHuecoMacro.Centro)]
    [InlineData("Centro Norte", "  ", true, TipoHuecoMacro.Trabajador)]
    [InlineData("Centro Norte", "M. Soto", false, TipoHuecoMacro.Fecha)]
    public void Si_falta_un_valor_no_rellena_ni_inventa_nada(string? centro, string? trabajador, bool hayFecha, TipoHuecoMacro falta)
    {
        var fecha = hayFecha ? new DateOnly(2026, 10, 12) : (DateOnly?)null;

        var r = HuecosMacro.Rellenar(Cuerpo, new ValoresHuecosMacro(centro, trabajador, fecha));

        r.Texto.Should().BeNull();
        r.Pendientes.Should().Equal(falta);
    }

    [Fact]
    public void Un_valor_con_marcado_sale_escapado_y_no_se_vuelve_a_expandir()
    {
        var hostil = "<img src=x onerror=alert(1)> {{fecha}} & \"comillas\"";

        var r = HuecosMacro.Rellenar(Cuerpo, new ValoresHuecosMacro(hostil, "<script>x</script>", new DateOnly(2026, 10, 12)));

        r.Texto.Should().NotContain("<img").And.NotContain("<script");
        r.Texto.Should().Contain("&lt;img src=x onerror=alert(1)&gt;");
        r.Texto.Should().Contain("&amp;");
        // El «{{fecha}}» que venía DENTRO del nombre del Centro se queda como texto: una sola pasada.
        r.Texto!.Split("{{fecha}}").Length.Should().Be(2);
        r.Texto.Should().Contain("efecto desde 12/10/2026.");
    }

    [Fact]
    public void Una_macro_sin_huecos_se_queda_igual_que_estaba()
    {
        const string CuerpoSinHuecos = "Buenos días,\n<b>gracias</b> & un saludo {{otra}}";

        HuecosMacro.Rellenar(CuerpoSinHuecos, new ValoresHuecosMacro(null, null, null)).Texto.Should().Be(CuerpoSinHuecos);
    }

    [Fact]
    public void La_vista_previa_marca_lo_que_falta_y_no_inventa_valores()
    {
        HuecosMacro.Previsualizar(Cuerpo, new ValoresHuecosMacro("Centro Norte", null, null))
            .Should().Be("Solicito el alta de Centro Norte para ‹trabajador› con efecto desde ‹fecha›.");
    }
}
