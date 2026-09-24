using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

public class CalculadoraEstadoDocumentoTests
{
    private static readonly DateOnly Hoy = new(2026, 7, 15);
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;

    private static EstadoDocumento Calcular(VigenciaDocumento vigencia) =>
        CalculadoraEstadoDocumento.Calcular(vigencia, Hoy, UmbralAmbarDias, UmbralRojoDias);

    [Fact]
    public void Retorna_SinCaducidad_solo_cuando_se_confirmo_que_no_caduca()
    {
        Calcular(VigenciaDocumento.NoCaduca).Should().Be(EstadoDocumento.SinCaducidad);
    }

    [Fact]
    public void Retorna_SinConfirmar_cuando_nadie_ha_anotado_el_vencimiento()
    {
        // Los dos sin fecha, y estados distintos: la ausencia de fecha no
        // se lee como «no caduca».
        Calcular(VigenciaDocumento.SinConfirmar).Should().Be(EstadoDocumento.SinConfirmar);
        Calcular(default).Should().Be(EstadoDocumento.SinConfirmar, "el valor por defecto no afirma nada");
    }

    [Fact]
    public void Retorna_Vencido_cuando_la_fecha_de_vencimiento_ya_paso()
    {
        Calcular(VigenciaDocumento.VenceEl(Hoy.AddDays(-1))).Should().Be(EstadoDocumento.Vencido);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    public void Retorna_Urgente_dentro_del_umbral_rojo(int diasRestantes)
    {
        Calcular(VigenciaDocumento.VenceEl(Hoy.AddDays(diasRestantes))).Should().Be(EstadoDocumento.Urgente);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(30)]
    public void Retorna_Proximo_entre_el_umbral_rojo_y_el_ambar(int diasRestantes)
    {
        Calcular(VigenciaDocumento.VenceEl(Hoy.AddDays(diasRestantes))).Should().Be(EstadoDocumento.Proximo);
    }

    [Fact]
    public void Retorna_Vigente_por_encima_del_umbral_ambar()
    {
        Calcular(VigenciaDocumento.VenceEl(Hoy.AddDays(31))).Should().Be(EstadoDocumento.Vigente);
    }

    [Theory]
    [InlineData(EstadoVigenciaDocumento.SinConfirmar, EstadoDocumento.SinConfirmar)]
    [InlineData(EstadoVigenciaDocumento.NoCaduca, EstadoDocumento.SinCaducidad)]
    public void La_sobrecarga_por_columnas_distingue_los_dos_sin_fecha(EstadoVigenciaDocumento estado, EstadoDocumento esperado)
    {
        CalculadoraEstadoDocumento.Calcular(estado, null, Hoy, UmbralAmbarDias, UmbralRojoDias).Should().Be(esperado);
    }

    [Fact]
    public void La_sobrecarga_por_columnas_revienta_ante_una_fila_incoherente()
    {
        var calcular = () => CalculadoraEstadoDocumento.Calcular(
            EstadoVigenciaDocumento.SinConfirmar, Hoy.AddDays(10), Hoy, UmbralAmbarDias, UmbralRojoDias);

        calcular.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Calcula_fecha_de_vencimiento_sumando_los_meses_de_vigencia()
    {
        var vencimiento = CalculadoraEstadoDocumento.CalcularFechaVencimiento(new DateOnly(2026, 3, 3), 12);

        vencimiento.Should().Be(new DateOnly(2027, 3, 3));
    }

    [Fact]
    public void No_calcula_fecha_de_vencimiento_cuando_no_hay_vigencia_en_meses()
    {
        var vencimiento = CalculadoraEstadoDocumento.CalcularFechaVencimiento(new DateOnly(2026, 3, 3), null);

        vencimiento.Should().BeNull();
    }

    // --- ResolverVigencia: la única regla de todos los productores ---

    [Fact]
    public void Resolver_con_vencimiento_automatico_suma_la_vigencia_e_ignora_lo_manual()
    {
        var vigencia = CalculadoraEstadoDocumento.ResolverVigencia(
            aplicaVencimientoAutomatico: true, vigenciaMeses: 12, fechaEmision: new DateOnly(2026, 3, 3),
            fechaVencimientoManual: new DateOnly(2030, 1, 1), noCaducaConfirmado: false);

        vigencia.Should().Be(VigenciaDocumento.VenceEl(new DateOnly(2027, 3, 3)));
    }

    [Fact]
    public void Resolver_automatico_sin_meses_queda_sin_confirmar_aunque_se_marque_no_caduca()
    {
        var vigencia = CalculadoraEstadoDocumento.ResolverVigencia(
            aplicaVencimientoAutomatico: true, vigenciaMeses: null, fechaEmision: Hoy,
            fechaVencimientoManual: null, noCaducaConfirmado: true);

        vigencia.Should().Be(VigenciaDocumento.SinConfirmar);
    }

    [Fact]
    public void Resolver_manual_con_fecha_vence_esa_fecha()
    {
        var vigencia = CalculadoraEstadoDocumento.ResolverVigencia(
            aplicaVencimientoAutomatico: false, vigenciaMeses: 12, fechaEmision: Hoy,
            fechaVencimientoManual: Hoy.AddDays(90), noCaducaConfirmado: false);

        vigencia.Should().Be(VigenciaDocumento.VenceEl(Hoy.AddDays(90)));
    }

    [Fact]
    public void Resolver_manual_sin_fecha_y_confirmado_no_caduca()
    {
        var vigencia = CalculadoraEstadoDocumento.ResolverVigencia(
            aplicaVencimientoAutomatico: false, vigenciaMeses: null, fechaEmision: Hoy,
            fechaVencimientoManual: null, noCaducaConfirmado: true);

        vigencia.Should().Be(VigenciaDocumento.NoCaduca);
    }

    [Fact]
    public void Resolver_manual_sin_fecha_ni_confirmacion_queda_sin_confirmar_y_nunca_no_caduca()
    {
        var vigencia = CalculadoraEstadoDocumento.ResolverVigencia(
            aplicaVencimientoAutomatico: false, vigenciaMeses: null, fechaEmision: Hoy,
            fechaVencimientoManual: null, noCaducaConfirmado: false);

        vigencia.Should().Be(VigenciaDocumento.SinConfirmar);
    }

    [Fact]
    public void Resolver_rechaza_fecha_manual_y_no_caduca_a_la_vez()
    {
        var resolver = () => CalculadoraEstadoDocumento.ResolverVigencia(
            aplicaVencimientoAutomatico: false, vigenciaMeses: null, fechaEmision: Hoy,
            fechaVencimientoManual: Hoy.AddDays(90), noCaducaConfirmado: true);

        resolver.Should().Throw<ArgumentException>();
    }
}
