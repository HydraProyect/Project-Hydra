using System.Globalization;
using CaeManager.Application.Documentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// El texto de vigencia de la fila desplegada de Trabajadores y de Empresas, que comparten ayudante: la fecha
/// siempre en <c>dd/MM/yyyy</c> con cultura invariante, el contrato de trabajo «sin confirmar» pide la fecha de
/// fin y el resto la de vencimiento.
/// </summary>
public class DetalleVigenciaDocumentoUiTests
{
    [Theory]
    [InlineData(EstadoDocumento.Vencido, false, "Desde el 06/11/2026")]
    [InlineData(EstadoDocumento.Vencido, true, "Venció el 06/11/2026")]
    [InlineData(EstadoDocumento.Proximo, true, "Caduca el 06/11/2026")]
    [InlineData(EstadoDocumento.SinConfirmar, false, "Falta la fecha de fin")]
    [InlineData(EstadoDocumento.Faltante, false, "Falta la fecha de fin")]
    public void Las_fichas_comparten_el_texto_de_vigencia_sin_cambiar_el_de_los_listados(
        EstadoDocumento estado, bool deEmpresa, string esperado) =>
        DetalleVigenciaDocumentoUi.DeFicha360(estado, new(2026, 11, 6), "Contrato de trabajo", deEmpresa)
            .Should().Be(esperado);

    private static readonly DateOnly Hoy = new(2026, 10, 18);

    private static IncidenciaDocumentalDto Incidencia(EstadoDocumento estado, DateOnly? fecha, string tipo = "Aptitud médica") =>
        new(Guid.NewGuid(), Guid.NewGuid(), tipo, estado, fecha);

    [Theory]
    [InlineData(EstadoDocumento.Vencido, "Desde el 12/03/2026")]
    [InlineData(EstadoDocumento.EnTolerancia, "Desde el 12/03/2026")]
    [InlineData(EstadoDocumento.Urgente, "Caduca el 12/03/2026")]
    [InlineData(EstadoDocumento.Proximo, "Caduca el 12/03/2026")]
    [InlineData(EstadoDocumento.Vigente, "")]
    [InlineData(EstadoDocumento.SinCaducidad, "")]
    public void Trabajador_dice_la_fecha_absoluta_segun_el_estado(EstadoDocumento estado, string esperado) =>
        DetalleVigenciaDocumentoUi.DeTrabajador(Incidencia(estado, new DateOnly(2026, 3, 12))).Should().Be(esperado);

    [Theory]
    [InlineData(EstadoDocumento.Vencido, "Venció el 12/03/2026")]
    [InlineData(EstadoDocumento.EnTolerancia, "Venció el 12/03/2026")]
    [InlineData(EstadoDocumento.Vigente, "")]
    [InlineData(EstadoDocumento.SinCaducidad, "")]
    public void Empresa_dice_cuando_vencio_con_la_fecha_absoluta(EstadoDocumento estado, string esperado) =>
        DetalleVigenciaDocumentoUi.DeEmpresa(Incidencia(estado, new DateOnly(2026, 3, 12)), Hoy).Should().Be(esperado);

    [Fact]
    public void Empresa_dice_el_plazo_de_lo_que_esta_por_vencer()
    {
        DetalleVigenciaDocumentoUi.DeEmpresa(Incidencia(EstadoDocumento.Urgente, Hoy.AddDays(5)), Hoy).Should().Be("Caduca en 5 días");
        DetalleVigenciaDocumentoUi.DeEmpresa(Incidencia(EstadoDocumento.Proximo, Hoy.AddDays(1)), Hoy).Should().Be("Caduca en 1 día");
        DetalleVigenciaDocumentoUi.DeEmpresa(Incidencia(EstadoDocumento.Urgente, Hoy), Hoy).Should().Be("Caduca hoy");
    }

    [Fact]
    public void Sin_fecha_de_vencimiento_lo_vencido_no_inventa_una_fecha()
    {
        DetalleVigenciaDocumentoUi.DeTrabajador(Incidencia(EstadoDocumento.Vencido, null)).Should().BeEmpty();
        DetalleVigenciaDocumentoUi.DeEmpresa(Incidencia(EstadoDocumento.Vencido, null), Hoy).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Contrato de trabajo", "Falta la fecha de fin")]
    [InlineData("Contrato de Trabajo", "Falta la fecha de fin")]
    [InlineData("  contrato de trabajo ", "Falta la fecha de fin")]
    [InlineData("Formación PRL", "Falta la fecha de vencimiento")]
    [InlineData("Contrato de arrendamiento", "Falta la fecha de vencimiento")]
    public void Sin_confirmar_pide_la_fecha_de_fin_solo_al_contrato_de_trabajo(string tipo, string esperado)
    {
        var incidencia = Incidencia(EstadoDocumento.SinConfirmar, null, tipo);

        DetalleVigenciaDocumentoUi.DeTrabajador(incidencia).Should().Be(esperado);
        DetalleVigenciaDocumentoUi.DeEmpresa(incidencia, Hoy).Should().Be(esperado);
    }

    /// <summary>La fecha no depende de la cultura del hilo: con una cultura que separa con puntos sigue siendo dd/MM/yyyy.</summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void La_fecha_se_formatea_con_cultura_invariante(string cultura)
    {
        var anterior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultura);
            var incidencia = Incidencia(EstadoDocumento.Vencido, new DateOnly(2026, 3, 2));

            DetalleVigenciaDocumentoUi.DeTrabajador(incidencia).Should().Be("Desde el 02/03/2026");
            DetalleVigenciaDocumentoUi.DeEmpresa(incidencia, Hoy).Should().Be("Venció el 02/03/2026");
        }
        finally
        {
            CultureInfo.CurrentCulture = anterior;
        }
    }
}
