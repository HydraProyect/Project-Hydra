using CaeManager.Application.AsistenteIa.Preparacion;
using FluentAssertions;

namespace CaeManager.Application.Tests.AsistenteIa.Preparacion;

/// <summary>
/// Las validaciones del plan del asistente. Lo que se fija es que avisan y proponen,
/// y que la propuesta nunca es otro error. Todos los datos son inventados.
/// </summary>
public class ValidacionesDeterministasOrdenTests
{
    private static readonly DateOnly Hoy = new(2026, 9, 19);

    [Fact]
    public void El_DNI_del_ejemplo_del_propietario_deberia_llevar_la_E()
    {
        // «DNI 1233443F», normalizado por el enmascarador con el cero de delante.
        var avisos = ValidacionesDeterministasOrden.ValidarDocumentoPersona("dni", "01233443F");

        avisos.Should().ContainSingle().Which.Should().BeEquivalentTo(new AvisoOrden(
            "dni", GravedadAvisoOrden.Bloqueante,
            "La letra del documento 01233443F debería ser E, no F.", "01233443E"));
    }

    [Theory]
    [InlineData("12345678Z")]
    [InlineData("X1234567L")]
    [InlineData("PAA123456")]
    [InlineData("7K4410392")]
    [InlineData("")]
    [InlineData(null)]
    public void Un_documento_correcto_o_sin_digito_de_control_no_avisa(string? documento)
    {
        ValidacionesDeterministasOrden.ValidarDocumentoPersona("dni", documento).Should().BeEmpty();
    }

    [Theory]
    [InlineData("X1234567A", "X1234567L")]
    [InlineData("Y1234567A", "Y1234567X")]
    public void Un_NIE_con_la_letra_mal_propone_la_suya(string documento, string propuesta)
    {
        ValidacionesDeterministasOrden.ValidarDocumentoPersona("nie", documento)
            .Should().ContainSingle().Which.Propuesta.Should().Be(propuesta);
    }

    [Fact]
    public void A_un_DNI_sin_letra_se_le_propone_la_que_le_toca()
    {
        ValidacionesDeterministasOrden.ValidarDocumentoPersona("dni", "12345678")
            .Should().ContainSingle().Which.Should().BeEquivalentTo(new AvisoOrden(
                "dni", GravedadAvisoOrden.Bloqueante,
                "Al documento 12345678 le falta la letra: debería ser Z.", "12345678Z"));
    }

    [Fact]
    public void El_periodo_del_ejemplo_avisa_de_la_fecha_pasada_sin_proponer_un_inicio_posterior_al_fin()
    {
        var avisos = ValidacionesDeterministasOrden.ValidarPeriodo(
            new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 30), Hoy);

        avisos.Should().ContainSingle().Which.Should().BeEquivalentTo(new AvisoOrden(
            "desde", GravedadAvisoOrden.Advertencia, "El 18/09/2026 ya ha pasado.", null));
    }

    [Fact]
    public void Una_fecha_pasada_sin_fin_propone_el_mismo_dia_del_mes_siguiente()
    {
        ValidacionesDeterministasOrden.ValidarPeriodo(new DateOnly(2026, 9, 18), null, Hoy)
            .Should().ContainSingle().Which.Should().BeEquivalentTo(new AvisoOrden(
                "desde", GravedadAvisoOrden.Advertencia,
                "El 18/09/2026 ya ha pasado: ¿quieres decir el 18/10/2026?", "18/10/2026"));
    }

    [Fact]
    public void Un_fin_anterior_al_inicio_bloquea_y_no_se_avisa_de_nada_mas()
    {
        ValidacionesDeterministasOrden.ValidarPeriodo(new DateOnly(2026, 10, 30), new DateOnly(2026, 10, 18), Hoy)
            .Should().ContainSingle().Which.Gravedad.Should().Be(GravedadAvisoOrden.Bloqueante);
    }

    [Fact]
    public void Si_todo_el_periodo_ha_pasado_se_avisa_del_inicio_y_del_fin()
    {
        var avisos = ValidacionesDeterministasOrden.ValidarPeriodo(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 5), Hoy);

        avisos.Select(a => a.Campo).Should().Equal("desde", "hasta");
        avisos.Should().OnlyContain(a => a.Gravedad == GravedadAvisoOrden.Advertencia && a.Propuesta == null);
    }

    [Fact]
    public void Un_periodo_futuro_o_que_empieza_hoy_no_avisa()
    {
        ValidacionesDeterministasOrden.ValidarPeriodo(Hoy, new DateOnly(2026, 9, 30), Hoy).Should().BeEmpty();
        ValidacionesDeterministasOrden.ValidarPeriodo(null, null, Hoy).Should().BeEmpty();
    }
}
