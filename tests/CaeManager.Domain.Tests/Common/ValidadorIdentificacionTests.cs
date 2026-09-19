using CaeManager.Domain.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Common;

public class ValidadorIdentificacionTests
{
    [Theory]
    [InlineData("12345678Z")]
    [InlineData("77189989B")]
    public void Detecta_un_dni_valido(string dni)
    {
        var resultado = ValidadorIdentificacion.Analizar(dni);

        resultado.Tipo.Should().Be(TipoIdentificacion.Dni);
        resultado.EsValido.Should().BeTrue();
    }

    [Fact]
    public void Detecta_un_dni_con_digito_de_control_incorrecto()
    {
        var resultado = ValidadorIdentificacion.Analizar("12345678A");

        resultado.Tipo.Should().Be(TipoIdentificacion.Dni);
        resultado.EsValido.Should().BeFalse();
    }

    [Theory]
    [InlineData("X1234567L")]
    [InlineData("Y2345678Z")]
    public void Detecta_un_nie_valido(string nie)
    {
        var resultado = ValidadorIdentificacion.Analizar(nie);

        resultado.Tipo.Should().Be(TipoIdentificacion.Nie);
        resultado.EsValido.Should().BeTrue();
    }

    [Fact]
    public void Detecta_un_nie_con_digito_de_control_incorrecto()
    {
        var resultado = ValidadorIdentificacion.Analizar("X1234567A");

        resultado.Tipo.Should().Be(TipoIdentificacion.Nie);
        resultado.EsValido.Should().BeFalse();
    }

    [Theory]
    [InlineData("B12345674")] // letra de organización con dígito de control numérico
    [InlineData("P1234567D")] // letra de organización con dígito de control alfabético
    public void Detecta_un_cif_de_empresa_valido(string cif)
    {
        var resultado = ValidadorIdentificacion.Analizar(cif);

        resultado.Tipo.Should().Be(TipoIdentificacion.NifEmpresa);
        resultado.EsValido.Should().BeTrue();
    }

    [Fact]
    public void Detecta_un_cif_con_digito_de_control_incorrecto()
    {
        var resultado = ValidadorIdentificacion.Analizar("B12345671");

        resultado.Tipo.Should().Be(TipoIdentificacion.NifEmpresa);
        resultado.EsValido.Should().BeFalse();
    }

    [Fact]
    public void Detecta_un_numero_de_soporte_tie_sin_validacion_de_digito_de_control()
    {
        var resultado = ValidadorIdentificacion.Analizar("AAA123456");

        resultado.Tipo.Should().Be(TipoIdentificacion.TieSoporte);
        resultado.EsValido.Should().BeTrue();
    }

    [Theory]
    [InlineData("123456789")] // pasaporte numérico extranjero
    [InlineData("AB1C2D3E4")]
    public void Trata_formatos_no_espanoles_como_otros_sin_bloquear(string documento)
    {
        var resultado = ValidadorIdentificacion.Analizar(documento);

        resultado.Tipo.Should().Be(TipoIdentificacion.Otros);
    }

    [Fact]
    public void Normaliza_minusculas_y_espacios_antes_de_analizar()
    {
        var resultado = ValidadorIdentificacion.Analizar("  12345678z  ");

        resultado.Tipo.Should().Be(TipoIdentificacion.Dni);
        resultado.EsValido.Should().BeTrue();
    }

    [Theory]
    [InlineData("B12345674")] // CIF de empresa, dígito de control numérico
    [InlineData("P1234567D")] // CIF de empresa, dígito de control alfabético
    [InlineData("12345678Z")] // DNI — autónomo español
    [InlineData("77189989B")] // DNI — segundo caso, otra letra de control
    [InlineData("X1234567L")] // NIE — autónomo extranjero residente
    [InlineData("Y2345678Z")] // NIE — segunda letra inicial
    [InlineData("  b12345674  ")] // se normaliza antes de decidir
    public void Acepta_como_identificacion_fiscal_el_cif_el_dni_y_el_nie(string documento)
    {
        ValidadorIdentificacion.EsIdentificacionFiscalValida(documento).Should().BeTrue();
    }

    [Theory]
    [InlineData("B12345671")] // CIF con dígito de control incorrecto
    [InlineData("B12345670")] // CIF con dígito de control incorrecto
    [InlineData("12345678A")] // DNI con letra de control incorrecta
    [InlineData("77189989A")] // DNI con letra de control incorrecta — la buena es B
    [InlineData("X1234567A")] // NIE con letra de control incorrecta
    public void Rechaza_una_identificacion_fiscal_con_digito_de_control_incorrecto(string documento)
    {
        ValidadorIdentificacion.EsIdentificacionFiscalValida(documento).Should().BeFalse();
    }

    [Theory]
    [InlineData("AAA123456")] // número de soporte de TIE: no es un identificador fiscal y no lleva control calculable
    [InlineData("123456789")] // pasaporte numérico extranjero
    [InlineData("AB1C2D3E4")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rechaza_lo_que_no_es_una_identificacion_fiscal_espanola(string? documento)
    {
        ValidadorIdentificacion.EsIdentificacionFiscalValida(documento).Should().BeFalse();
    }

    /// <summary>
    /// El criterio nuevo es un superconjunto estricto del anterior ("solo NIF
    /// de empresa con control correcto"). Es la garantía de que relajar la
    /// validación no invalida ninguna fila ya guardada: lo que el criterio
    /// viejo aceptaba, el nuevo lo sigue aceptando. Sin esta propiedad habría
    /// que inventariar los datos de producción antes de desplegar.
    /// </summary>
    [Theory]
    [InlineData("B12345674")]
    [InlineData("P1234567D")]
    [InlineData("12345678Z")]
    [InlineData("X1234567L")]
    [InlineData("AAA123456")]
    [InlineData("B12345671")]
    [InlineData("123456789")]
    [InlineData("77189989B")]
    public void Todo_lo_que_aceptaba_el_criterio_anterior_lo_sigue_aceptando(string documento)
    {
        var resultado = ValidadorIdentificacion.Analizar(documento);
        var loAceptabaElCriterioAnterior =
            resultado.Tipo == TipoIdentificacion.NifEmpresa && resultado.EsValido;

        if (loAceptabaElCriterioAnterior)
            ValidadorIdentificacion.EsIdentificacionFiscalValida(documento).Should().BeTrue();
    }
}
