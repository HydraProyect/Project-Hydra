using CaeManager.Domain.Inbound.CatalogoBase;
using CaeManager.Domain.Inbound.Requisitos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Inbound.Requisitos;

/// <summary>
/// «¿Es alguno de estos?» antes de crear un requisito propio: la puntuación solo sugiere, pero tiene que reconocer lo
/// mismo escrito de otra forma (acentos, mayúsculas, plurales, una errata) y no confundir requisitos distintos.
/// </summary>
public class SimilitudNombresRequisitoTests
{
    [Theory]
    [InlineData("Formación PRL", "formacion prl")]
    [InlineData("Certificado de aptitud médica", "CERTIFICADO APTITUD MEDICA")]
    public void El_mismo_nombre_escrito_de_otra_forma_puntua_uno(string texto, string candidato)
    {
        SimilitudNombresRequisito.Puntuar(texto, candidato).Should().Be(1);
    }

    [Fact]
    public void Contener_al_otro_nombre_casi_es_lo_mismo()
    {
        SimilitudNombresRequisito.Puntuar("Seguro de responsabilidad civil", "Responsabilidad civil")
            .Should().BeGreaterThanOrEqualTo(SimilitudNombresRequisito.UmbralSugerencia);
    }

    [Theory]
    [InlineData("Certificados de formación en altura", "Certificado formacion trabajos en altura")]
    [InlineData("Reconocimiento medico", "Reconocimento médico")]
    public void Plurales_y_erratas_siguen_sugiriendo(string texto, string candidato)
    {
        SimilitudNombresRequisito.Puntuar(texto, candidato).Should().BeGreaterThanOrEqualTo(SimilitudNombresRequisito.UmbralSugerencia);
    }

    [Theory]
    [InlineData("Seguro de responsabilidad civil", "Formación PRL")]
    [InlineData("TC2", "ITV del vehículo")]
    [InlineData("", "Formación PRL")]
    [InlineData("de la", "Formación de la PRL")]
    public void Requisitos_distintos_no_se_sugieren(string texto, string candidato)
    {
        SimilitudNombresRequisito.Puntuar(texto, candidato).Should().BeLessThan(SimilitudNombresRequisito.UmbralSugerencia);
    }

    [Fact]
    public void D28_un_alias_cuenta_como_el_nombre()
    {
        SimilitudNombresRequisito.PuntuarContra("RNT", "Relación nominal de trabajadores", ["RNT", "TC2"]).Should().Be(1);
        SimilitudNombresRequisito.PuntuarContra("RNT", "Relación nominal de trabajadores", []).Should().BeLessThan(SimilitudNombresRequisito.UmbralSugerencia);
    }
}

/// <summary>R-14: A1 y puesta a disposición de ETT nacen inactivos hasta la revisión legal.</summary>
public class RequisitoBasePendienteRevisionLegalTests
{
    [Fact]
    public void Un_requisito_pendiente_de_revision_legal_nace_inactivo_y_no_se_reactiva()
    {
        var requisito = new RequisitoBase("a1-prueba", "A1 de prueba", "Fundamento", NivelExigencia.N1, pendienteRevisionLegal: true);

        requisito.Activo.Should().BeFalse();
        requisito.PendienteRevisionLegal.Should().BeTrue();
        FluentActions.Invoking(requisito.Reactivar).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Confirmar_la_revision_legal_no_lo_activa_por_si_sola()
    {
        var requisito = new RequisitoBase("a1-prueba", "A1 de prueba", "Fundamento", NivelExigencia.N1, pendienteRevisionLegal: true);

        requisito.ConfirmarRevisionLegal();

        requisito.PendienteRevisionLegal.Should().BeFalse();
        requisito.Activo.Should().BeFalse();
        requisito.Reactivar();
        requisito.Activo.Should().BeTrue();
    }

    [Fact]
    public void Sin_revision_pendiente_nace_activo()
    {
        new RequisitoBase("prueba", "Prueba", "Fundamento", NivelExigencia.N2).Activo.Should().BeTrue();
    }
}
