using CaeManager.Application.Documentos.ValidacionOficial.Parsers;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos.ValidacionOficial;

/// <summary>
/// Los parsers se prueban con plantillas sintéticas calibradas contra
/// documentos reales de gestoría (plan, PR-6) y confirmación directa del
/// usuario sobre el texto exacto del certificado TGSS: el literal de
/// resultado es "El presente certificado tiene carácter POSITIVO/NEGATIVO",
/// la fecha va tras "Información obtenida a", y el CIF aparece como "Código
/// de Empresario" con un prefijo numérico pegado delante (confirmado para
/// TGSS/ITA/RLC/RNT) — <see cref="ParserDocumentoOficialBase.RegexCifComun"/>
/// lo admite y lo descarta. Estos tests fijan la mecánica: extracción,
/// negativo detectado antes que positivo, y campos faltantes como camino a
/// revisión, nunca a auto-validación.
/// </summary>
public class ParsersDocumentoOficialTests
{
    private const string PlantillaTgssPositiva =
        "TESORERÍA GENERAL DE LA SEGURIDAD SOCIAL CERTIFICACIÓN Razón social: CONSTRUCCIONES IBERTEC S.A. " +
        "Código de Empresario: 0B12345678 " +
        "El presente certificado tiene carácter POSITIVO " +
        "Información obtenida a 4 de agosto de 2026 " +
        "Código Electrónico de Autenticidad: ABC123DEF456GHI7";

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
            "tiene carácter POSITIVO", "tiene carácter NEGATIVO");

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
    public void Tgss_admite_fecha_numerica_ademas_de_literal()
    {
        var texto = PlantillaTgssPositiva.Replace(
            "Información obtenida a 4 de agosto de 2026", "Información obtenida a 04/08/2026");

        var extraido = new ParserCorrienteTgss().Extraer(texto);

        extraido.FechaEmision.Should().Be(new DateOnly(2026, 8, 4));
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
    public void Rnt_extrae_huella_periodo_y_cif_con_prefijo()
    {
        var texto =
            "RELACIÓN NOMINAL DE TRABAJADORES Código de Empresario: 90B12345678 Período de liquidación: 07/2026 " +
            "Huella electrónica: 1A2B3C4D5E6F7G8H9I0J";

        var extraido = new ParserRnt().Extraer(texto);

        extraido.CamposObligatoriosFaltantes.Should().BeEmpty();
        extraido.Periodo.Should().Be("2026-07");
        extraido.CodigoVerificacion.Should().Be("1A2B3C4D5E6F7G8H9I0J");
        // El prefijo "90" nunca se cuela en el CIF extraído.
        extraido.Cif.Should().Be("B12345678");
        extraido.ResultadoPositivo.Should().BeNull("el RNT no declara resultado positivo/negativo");
        // Confirmado por el usuario: el RNT no imprime fecha propia — es el
        // día 1 del mes del periodo de liquidación.
        extraido.FechaEmision.Should().Be(new DateOnly(2026, 7, 1));
    }

    /// <summary>
    /// La forma real de un RNT/RLC de gestoría (calibración con muestras):
    /// documento tabular — etiquetas agrupadas, valores en otra zona, sin
    /// tildes (el extractor las sustituye por otros caracteres) — y el CIF
    /// va con el prefijo numérico pegado, sin ninguna etiqueta cerca. El
    /// periodo sale por forma del valor; el CIF, por forma con prefijo.
    /// </summary>
    [Fact]
    public void Rnt_tabular_sin_tildes_extrae_periodo_y_cif_por_forma()
    {
        var texto =
            "Raz!n social C!digo cuenta cotizaci!n Periodo de liquidaci!n Calificador de la liquidaci!n " +
            "EMPRESA EJEMPLO SL 90B12345678 06/2026 Ordinaria " +
            "Referencia Fecha Hora Huella 999 15/07/2026 08:30";

        var extraido = new ParserRnt().Extraer(texto);

        extraido.CamposObligatoriosFaltantes.Should().BeEmpty();
        extraido.Periodo.Should().Be("2026-06");
        extraido.Cif.Should().Be("B12345678");
        extraido.FechaEmision.Should().Be(new DateOnly(2026, 6, 1));
    }

    [Fact]
    public void El_periodo_por_forma_no_pesca_dentro_de_una_fecha_completa()
    {
        // Solo hay una fecha dd/MM/yyyy — ningún MM/yyyy suelto.
        var texto = "Código de Empresario: 90B12345678 Fecha de control 15/07/2026 sin periodo suelto";

        var extraido = new ParserRlc().Extraer(texto);

        extraido.Periodo.Should().BeNull();
        extraido.CamposObligatoriosFaltantes.Should().Contain("periodo de liquidación");
    }

    [Fact]
    public void Rlc_extrae_los_mismos_campos_que_el_rnt()
    {
        var texto =
            "RECIBO DE LIQUIDACIÓN DE COTIZACIONES Código de Empresario: 90B12345678 Período de liquidación: 06/2026 " +
            "Huella electrónica: FFEEDDCCBBAA99887766";

        var extraido = new ParserRlc().Extraer(texto);

        extraido.CamposObligatoriosFaltantes.Should().BeEmpty();
        extraido.Periodo.Should().Be("2026-06");
        extraido.Cif.Should().Be("B12345678");
    }

    [Fact]
    public void Ita_extrae_cif_con_prefijo_y_la_fecha_del_literal_confirmado()
    {
        var extraido = new ParserIta().Extraer(
            "INFORME DE TRABAJADORES EN ALTA a fecha 04 08 2026 Código de Empresario: 90B12345678");

        extraido.CamposObligatoriosFaltantes.Should().BeEmpty();
        extraido.Cif.Should().Be("B12345678");
        extraido.FechaEmision.Should().Be(new DateOnly(2026, 8, 4));
    }

    [Fact]
    public void Ita_tambien_admite_la_fecha_con_barras()
    {
        var extraido = new ParserIta().Extraer(
            "Informe de Trabajadores en Alta a fecha: 04/08/2026 Código de Empresario: 90B12345678");

        extraido.FechaEmision.Should().Be(new DateOnly(2026, 8, 4));
    }

    [Fact]
    public void Ita_sin_cif_ni_fecha_reconocibles_los_declara_faltantes()
    {
        var extraido = new ParserIta().Extraer("INFORME DE TRABAJADORES EN ALTA sin ningún identificador reconocible");

        extraido.CamposObligatoriosFaltantes.Should().Contain("CIF");
        extraido.CamposObligatoriosFaltantes.Should().Contain("fecha de emisión");
        extraido.Cif.Should().BeNull();
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
