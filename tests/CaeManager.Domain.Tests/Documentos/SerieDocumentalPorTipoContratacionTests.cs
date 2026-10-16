using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// Serie documental por tipo de contratación (decisión de producto de 2026-10-16): un Tipo de documento de Ámbito
/// Trabajador se pide de serie solo a los tipos de contratación que marque el Tenant; nace pidiéndose a todos.
/// </summary>
public class SerieDocumentalPorTipoContratacionTests
{
    private static TipoDocumento Tipo(AmbitoAplicacion ambito = AmbitoAplicacion.Trabajador,
        RequisitoDocumental requerido = RequisitoDocumental.Si) =>
        new("Aptitud médica", 12, aplicaVencimientoAutomatico: true, orden: 1, ambito, requerido);

    [Fact]
    public void Un_tipo_nuevo_se_pide_de_serie_a_los_tres_tipos_de_contratacion()
    {
        var tipo = Tipo();

        tipo.TiposContratacionDeSerie.Should().Be(TiposContratacion.Todos);
        foreach (var contratacion in Enum.GetValues<TipoContratacion>())
            tipo.SePideDeSerieA(contratacion).Should().BeTrue();
    }

    [Fact]
    public void Una_serie_sin_voluntario_no_se_pide_al_voluntario_y_si_a_los_demas()
    {
        var tipo = Tipo();

        tipo.EstablecerTiposContratacionDeSerie(TiposContratacion.CuentaAjena | TiposContratacion.Autonomo);

        tipo.SePideDeSerieA(TipoContratacion.Voluntario).Should().BeFalse();
        tipo.SePideDeSerieA(TipoContratacion.CuentaAjena).Should().BeTrue();
        tipo.SePideDeSerieA(TipoContratacion.Autonomo).Should().BeTrue();
    }

    [Theory]
    [InlineData(RequisitoDocumental.No)]
    [InlineData(RequisitoDocumental.Condicional)]
    public void Un_tipo_que_no_cuenta_para_cumplimiento_no_se_pide_de_serie_a_nadie(RequisitoDocumental requerido)
    {
        var tipo = Tipo(requerido: requerido);

        foreach (var contratacion in Enum.GetValues<TipoContratacion>())
            tipo.SePideDeSerieA(contratacion).Should().BeFalse();
    }

    [Fact]
    public void Fuera_del_ambito_trabajador_la_serie_no_filtra()
    {
        var tipo = Tipo(AmbitoAplicacion.Empresa);

        tipo.EstablecerTiposContratacionDeSerie(TiposContratacion.CuentaAjena);

        tipo.SePideDeSerieA(TipoContratacion.Voluntario).Should().BeTrue();
    }

    [Fact]
    public void Una_serie_vacia_se_rechaza_y_conserva_la_anterior()
    {
        var tipo = Tipo();
        tipo.EstablecerTiposContratacionDeSerie(TiposContratacion.Voluntario);

        var accion = () => tipo.EstablecerTiposContratacionDeSerie(TiposContratacion.Ninguno);

        accion.Should().Throw<ArgumentException>();
        tipo.TiposContratacionDeSerie.Should().Be(TiposContratacion.Voluntario);
    }

    [Fact]
    public void Los_bits_desconocidos_se_descartan()
    {
        var tipo = Tipo();

        tipo.EstablecerTiposContratacionDeSerie((TiposContratacion)(8 | (int)TiposContratacion.Autonomo));

        tipo.TiposContratacionDeSerie.Should().Be(TiposContratacion.Autonomo);
    }

    [Theory]
    [InlineData(TipoContratacion.CuentaAjena, 1)]
    [InlineData(TipoContratacion.Autonomo, 2)]
    [InlineData(TipoContratacion.Voluntario, 4)]
    public void El_bit_de_cada_tipo_de_contratacion_es_estable_porque_se_persiste(TipoContratacion tipo, int bit)
    {
        ((int)tipo.ComoConjunto()).Should().Be(bit);
        ((int)TiposContratacion.Todos).Should().Be(7);
    }
}
