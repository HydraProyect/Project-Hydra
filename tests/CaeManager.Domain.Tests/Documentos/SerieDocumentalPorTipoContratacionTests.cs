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
    public void Un_tipo_nuevo_se_pide_de_serie_a_todos_los_tipos_de_contratacion_tambien_al_Extranjero()
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

        tipo.EstablecerTiposContratacionDeSerie((TiposContratacion)(16 | (int)TiposContratacion.Autonomo));

        tipo.TiposContratacionDeSerie.Should().Be(TiposContratacion.Autonomo);
    }

    [Fact]
    public void La_normalizacion_conserva_el_bit_Extranjero()
    {
        var tipo = Tipo();

        tipo.EstablecerTiposContratacionDeSerie(TiposContratacion.Extranjero | (TiposContratacion)32);

        tipo.TiposContratacionDeSerie.Should().Be(TiposContratacion.Extranjero);
    }

    [Fact]
    public void Una_serie_solo_de_Extranjero_se_pide_al_Extranjero_y_no_a_cuenta_ajena_ni_a_autonomo()
    {
        var tipo = Tipo();

        tipo.EstablecerTiposContratacionDeSerie(TiposContratacion.Extranjero);

        tipo.SePideDeSerieA(TipoContratacion.Extranjero).Should().BeTrue();
        tipo.SePideDeSerieA(TipoContratacion.CuentaAjena).Should().BeFalse();
        tipo.SePideDeSerieA(TipoContratacion.Autonomo).Should().BeFalse();
        tipo.SePideDeSerieA(TipoContratacion.Voluntario).Should().BeFalse();
    }

    [Fact]
    public void Una_serie_de_los_tres_anteriores_no_se_pide_al_Extranjero()
    {
        // El 7 que una base tenga después de la migración es una serie acotada a propósito, no «todos».
        var tipo = Tipo();

        tipo.EstablecerTiposContratacionDeSerie(TiposContratacion.CuentaAjena | TiposContratacion.Autonomo | TiposContratacion.Voluntario);

        tipo.SePideDeSerieA(TipoContratacion.Extranjero).Should().BeFalse();
        tipo.SePideDeSerieA(TipoContratacion.CuentaAjena).Should().BeTrue();
    }

    [Theory]
    [InlineData(TipoContratacion.CuentaAjena, 0, 1)]
    [InlineData(TipoContratacion.Autonomo, 1, 2)]
    [InlineData(TipoContratacion.Voluntario, 2, 4)]
    [InlineData(TipoContratacion.Extranjero, 3, 8)]
    public void El_ordinal_y_el_bit_de_cada_tipo_de_contratacion_son_estables_porque_se_persisten(
        TipoContratacion tipo, int ordinal, int bit)
    {
        ((int)tipo).Should().Be(ordinal);
        ((int)tipo.ComoConjunto()).Should().Be(bit);
    }

    [Fact]
    public void Todos_es_15_y_contiene_cada_tipo_de_contratacion()
    {
        ((int)TiposContratacion.Todos).Should().Be(15);
        Enum.GetValues<TipoContratacion>().Should().HaveCount(4);
        foreach (var contratacion in Enum.GetValues<TipoContratacion>())
            TiposContratacion.Todos.Incluye(contratacion).Should().BeTrue();
    }
}
