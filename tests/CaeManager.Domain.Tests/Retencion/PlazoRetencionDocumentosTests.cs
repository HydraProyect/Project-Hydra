using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Retencion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Retencion;

/// <summary>
/// Plazo de conservación de los Documentos (decisión del 2026-11-18): el del Tenant propietario va de 1 a 5 años enteros
/// y nace en 5 (R1, R2); el de un tipo de documento, en el mismo rango o nulo, que hereda el del Tenant propietario.
/// </summary>
public class PlazoRetencionDocumentosTests
{
    [Fact]
    public void El_Tenant_propietario_nace_con_cinco_anios()
    {
        new ParametroSistema(30, 15).AniosRetencionDocumentos.Should().Be(5);
        PlazoRetencionDocumentos.PorDefecto.Should().Be(5);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void El_plazo_del_Tenant_propietario_admite_los_limites(int anios)
    {
        var parametros = new ParametroSistema(30, 15);

        parametros.FijarPlazoRetencionDocumentos(anios);

        parametros.AniosRetencionDocumentos.Should().Be(anios);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void El_plazo_del_Tenant_propietario_rechaza_lo_que_queda_fuera_y_no_cambia(int anios)
    {
        var parametros = new ParametroSistema(30, 15);

        var accion = () => parametros.FijarPlazoRetencionDocumentos(anios);

        accion.Should().Throw<ArgumentOutOfRangeException>();
        parametros.AniosRetencionDocumentos.Should().Be(5);
    }

    [Fact]
    public void Un_tipo_de_documento_nace_sin_plazo_propio()
    {
        NuevoTipo().AniosRetencionDocumentos.Should().BeNull();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Un_tipo_de_documento_admite_los_limites_y_vuelve_a_heredar_con_nulo(int anios)
    {
        var tipo = NuevoTipo();

        tipo.FijarPlazoRetencionDocumentos(anios);
        tipo.AniosRetencionDocumentos.Should().Be(anios);

        tipo.FijarPlazoRetencionDocumentos(null);
        tipo.AniosRetencionDocumentos.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Un_tipo_de_documento_rechaza_lo_que_queda_fuera_y_no_cambia(int anios)
    {
        var tipo = NuevoTipo();
        tipo.FijarPlazoRetencionDocumentos(3);

        var accion = () => tipo.FijarPlazoRetencionDocumentos(anios);

        accion.Should().Throw<ArgumentOutOfRangeException>();
        tipo.AniosRetencionDocumentos.Should().Be(3);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void EsValido_acota_de_uno_a_cinco(int anios, bool valido)
    {
        PlazoRetencionDocumentos.EsValido(anios).Should().Be(valido);
    }

    /// <summary>R4: la solicitud de Documentos guarda la lista exacta de lo propuesto, sin repetidos, y cuenta eso.</summary>
    [Fact]
    public void Una_solicitud_de_Documentos_lleva_la_lista_exacta_de_lo_propuesto()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var (solicitud, propuestos) = SolicitudPurga.DeDocumentos([a, b, a], new DateOnly(2026, 11, 18));

        solicitud.TipoDato.Should().Be(TipoDatoPurgable.Documentos);
        solicitud.RegistrosAfectados.Should().Be(2);
        propuestos.Select(p => p.DocumentoId).Should().BeEquivalentTo([a, b]);
        propuestos.Should().OnlyContain(p => p.SolicitudPurgaId == solicitud.Id);
    }

    [Fact]
    public void Una_solicitud_de_Documentos_sin_ninguno_no_se_crea()
    {
        var accion = () => SolicitudPurga.DeDocumentos([], new DateOnly(2026, 11, 18));

        accion.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static TipoDocumento NuevoTipo() =>
        new("Reconocimiento médico", vigenciaMeses: 12, aplicaVencimientoAutomatico: true, orden: 1, AmbitoAplicacion.Trabajador);
}
