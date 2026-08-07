using CaeManager.Application.Documentos.ValidacionOficial.Parsers;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos.ValidacionOficial;

/// <summary>
/// Los parsers se prueban con plantillas sintéticas de la redacción pública
/// conocida de cada documento. La calibración contra PDFs reales (plan,
/// PR-6) ajustará las anclas — estos tests fijan la mecánica: extracción,
/// negativo detectado antes que positivo, y campos faltantes como camino a
/// revisión, nunca a auto-validación.
/// </summary>
public class ParsersDocumentoOficialTests
{
    private const string PlantillaTgssPositiva =
        "TESORERÍA GENERAL DE LA SEGURIDAD SOCIAL CERTIFICACIÓN Razón social: CONSTRUCCIONES IBERTEC S.A. " +
        "C.I.F.: B12345678 no tiene pendiente de ingreso ninguna reclamación por deudas ya vencidas con la Seguridad Social " +
        "a 4 de agosto de 2026 Código Electrónico de Autenticidad: ABC123DEF456GHI7";

    [Fact]
    public void Tgss_positivo_extrae_todos_los_campos()
    {
        var extraido = new ParserCorrienteTgss().Extraer(PlantillaTgssPositiva);

        extraido.CamposObligatoriosFaltantes.Should().BeEmpty();
        extraido.ResultadoPositivo.Should().BeTrue();
        extraido.Cif.Should().Be("B12345678");
        extraido.CodigoVerificacion.Should().Be("ABC123DEF456GHI7");
        extraido.FechaEmision.Should().Be(new DateOnly(2026, 8, 4));
    }

    [Fact]
    public void Tgss_en_negativo_jamas_da_positivo()
    {
        var texto = PlantillaTgssPositiva.Replace(
            "no tiene pendiente de ingreso ninguna reclamación",
            "tiene pendiente de ingreso reclamaciones");

        var extraido = new ParserCorrienteTgss().Extraer(texto);

        extraido.ResultadoPositivo.Should().BeFalse();
    }

    [Fact]
    public void Tgss_sin_cea_lo_declara_faltante()
    {
        var texto = PlantillaTgssPositiva.Replace("Código Electrónico de Autenticidad: ABC123DEF456GHI7", "");

        var extraido = new ParserCorrienteTgss().Extraer(texto);

        extraido.CamposObligatoriosFaltantes.Should().Contain("código de verificación");
    }

    [Fact]
    public void Aeat_positivo_extrae_csv_y_resultado()
    {
        var texto =
            "AGENCIA TRIBUTARIA CERTIFICADO NIF: A87654321 la entidad se encuentra al corriente de sus obligaciones tributarias " +
            "fecha: 15/07/2026 Código Seguro de Verificación: XYZ98765ABCD1234";

        var extraido = new ParserCorrienteAeat().Extraer(texto);

        extraido.CamposObligatoriosFaltantes.Should().BeEmpty();
        extraido.ResultadoPositivo.Should().BeTrue();
        extraido.Cif.Should().Be("A87654321");
        extraido.CodigoVerificacion.Should().Be("XYZ98765ABCD1234");
        extraido.FechaEmision.Should().Be(new DateOnly(2026, 7, 15));
    }

    [Fact]
    public void Rnt_extrae_huella_y_periodo_normalizado()
    {
        var texto =
            "RELACIÓN NOMINAL DE TRABAJADORES C.I.F.: B12345678 Período de liquidación: 07/2026 " +
            "Huella electrónica: 1A2B3C4D5E6F7G8H9I0J";

        var extraido = new ParserRnt().Extraer(texto);

        extraido.CamposObligatoriosFaltantes.Should().BeEmpty();
        extraido.Periodo.Should().Be("2026-07");
        extraido.CodigoVerificacion.Should().Be("1A2B3C4D5E6F7G8H9I0J");
        extraido.ResultadoPositivo.Should().BeNull("el RNT no declara resultado positivo/negativo");
    }

    /// <summary>
    /// La forma real de un RNT/RLC de gestoría (calibración con muestras):
    /// documento tabular — etiquetas agrupadas, valores en otra zona — y sin
    /// tildes (el extractor las sustituye por otros caracteres). El periodo
    /// sale por forma del valor; el CIF no existe en el texto (identidad por
    /// CCC) y no puede bloquear la extracción.
    /// </summary>
    [Fact]
    public void Rnt_tabular_sin_tildes_extrae_el_periodo_por_forma()
    {
        var texto =
            "Raz!n social C!digo cuenta cotizaci!n Periodo de liquidaci!n Calificador de la liquidaci!n " +
            "EMPRESA EJEMPLO SL 28123456789 06/2026 Ordinaria " +
            "Referencia Fecha Hora Huella 999 15/07/2026 08:30";

        var extraido = new ParserRnt().Extraer(texto);

        extraido.CamposObligatoriosFaltantes.Should().BeEmpty();
        extraido.Periodo.Should().Be("2026-06");
        extraido.Cif.Should().BeNull("el RNT identifica por CCC, no trae CIF");
    }

    [Fact]
    public void El_periodo_por_forma_no_pesca_dentro_de_una_fecha_completa()
    {
        // Solo hay una fecha dd/MM/yyyy — ningún MM/yyyy suelto.
        var extraido = new ParserRlc().Extraer("Fecha de control 15/07/2026 sin periodo suelto");

        extraido.Periodo.Should().BeNull();
        extraido.CamposObligatoriosFaltantes.Should().Contain("periodo de liquidación");
    }

    [Fact]
    public void Rlc_extrae_los_mismos_campos_que_el_rnt()
    {
        var texto =
            "RECIBO DE LIQUIDACIÓN DE COTIZACIONES C.I.F.: B12345678 Período de liquidación: 06/2026 " +
            "Huella electrónica: FFEEDDCCBBAA99887766";

        var extraido = new ParserRlc().Extraer(texto);

        extraido.CamposObligatoriosFaltantes.Should().BeEmpty();
        extraido.Periodo.Should().Be("2026-06");
    }

    [Fact]
    public void Ita_extrae_cif_si_existe_y_no_bloquea_si_falta()
    {
        var conCif = new ParserIta().Extraer("INFORME DE TRABAJADORES EN ALTA C.I.F.: B12345678");
        conCif.CamposObligatoriosFaltantes.Should().BeEmpty();
        conCif.Cif.Should().Be("B12345678");

        // Calibración con muestras: el ITA real identifica por CCC y no trae
        // CIF en el texto — sin CIF no hay campos faltantes (el cotejo de
        // identidad lo resuelve el pipeline mandándolo a revisión).
        var sinCif = new ParserIta().Extraer("INFORME DE TRABAJADORES EN ALTA 28123456789");
        sinCif.CamposObligatoriosFaltantes.Should().BeEmpty();
        sinCif.Cif.Should().BeNull();
    }

    [Theory]
    [InlineData("B-12.345.678", "B12345678")]
    [InlineData("b12345678", "B12345678")]
    [InlineData(" B 12345678 ", "B12345678")]
    public void El_cif_se_normaliza_quitando_separadores(string bruto, string esperado) =>
        ParserDocumentoOficialBase.NormalizarCif(bruto).Should().Be(esperado);

    [Theory]
    [InlineData("04/08/2026", 2026, 8, 4)]
    [InlineData("4-8-2026", 2026, 8, 4)]
    [InlineData("4 de agosto de 2026", 2026, 8, 4)]
    public void Las_fechas_es_es_se_normalizan(string bruto, int anio, int mes, int dia) =>
        ParserDocumentoOficialBase.NormalizarFecha(bruto).Should().Be(new DateOnly(anio, mes, dia));

    [Fact]
    public void Un_texto_ajeno_al_perfil_declara_faltantes_y_nunca_extrae_de_mas()
    {
        var extraido = new ParserCorrienteTgss().Extraer("Factura de suministros de julio, total 1.200 EUR");

        extraido.CamposObligatoriosFaltantes.Should().NotBeEmpty();
        extraido.ResultadoPositivo.Should().BeNull();
        extraido.Cif.Should().BeNull();
    }
}
