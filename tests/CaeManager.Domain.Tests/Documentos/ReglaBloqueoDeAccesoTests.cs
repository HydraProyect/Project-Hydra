using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// La regla única de bloqueo de acceso, evaluada POR CENTRO (decisión del propietario del producto, 2026-10-03, y su
/// corrección de la tarde): un documento bloqueante AUSENTE, o que ya no vale con las condiciones de ese Centro (vigencia
/// propia y tolerancia), bloquea por igual; «No caduca» y «Sin confirmar» (sin periodicidad del Centro) no bloquean.
/// Cada fila es una entrada de la tabla; el día de negocio es fijo para que los límites (vence hoy, último día de
/// tolerancia) sean exactos.
/// </summary>
public class ReglaBloqueoDeAccesoTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 3);

    private static CondicionesDeAccesoDelCentro Cond(int? periodicidadMeses = null, int toleranciaDias = 0) => new(periodicidadMeses, toleranciaDias);

    private static DocumentoParaAcceso VenceHaceDias(int diasDesdeQueVencio, DateOnly? emision = null) =>
        new(VigenciaDocumento.VenceEl(Hoy.AddDays(-diasDesdeQueVencio)), emision ?? Hoy.AddYears(-1));

    [Theory]
    [InlineData("Vencido ayer", -1, false)]
    [InlineData("Vencido hace un ano", -365, false)]
    [InlineData("Vence hoy (Urgente, valido hoy)", 0, true)]
    [InlineData("Vence manana (Urgente)", 1, true)]
    [InlineData("Vence en 20 dias (Proximo)", 20, true)]
    [InlineData("Vence en un ano (Vigente)", 365, true)]
    public void Con_tolerancia_0_solo_un_documento_con_fecha_anterior_a_hoy_deja_de_ser_valido(string caso, int diasHastaVencer, bool esperado)
    {
        var documento = new DocumentoParaAcceso(VigenciaDocumento.VenceEl(Hoy.AddDays(diasHastaVencer)), Hoy.AddYears(-2));

        ReglaBloqueoDeAcceso.ValidoParaAcceder(documento, Cond(), Hoy).Should().Be(esperado, caso);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(14, 15, true)]
    [InlineData(15, 15, true)]
    [InlineData(16, 15, false)]
    [InlineData(20, 15, false)]
    [InlineData(5, 5, true)]
    [InlineData(6, 5, false)]
    public void La_tolerancia_alarga_el_acceso_hasta_el_ultimo_dia_inclusive(int diasDesdeQueVencio, int toleranciaDias, bool valeParaAcceder)
    {
        ReglaBloqueoDeAcceso.ValidoParaAcceder(VenceHaceDias(diasDesdeQueVencio), Cond(toleranciaDias: toleranciaDias), Hoy)
            .Should().Be(valeParaAcceder);
    }

    [Fact]
    public void El_mismo_documento_vale_en_un_centro_y_no_en_otro_segun_su_tolerancia()
    {
        var documento = VenceHaceDias(10);

        ReglaBloqueoDeAcceso.ValidoParaAcceder(documento, Cond(toleranciaDias: 15), Hoy).Should().BeTrue("este Centro concede 15 dias");
        ReglaBloqueoDeAcceso.ValidoParaAcceder(documento, Cond(toleranciaDias: 0), Hoy).Should().BeFalse("este Centro no concede tolerancia");
    }

    [Fact]
    public void La_tolerancia_no_cambia_el_vencimiento_efectivo_solo_hasta_cuando_vale_para_acceder()
    {
        var documento = VenceHaceDias(10);

        ReglaBloqueoDeAcceso.VencimientoEfectivo(documento, null).Should().Be(Hoy.AddDays(-10));
        ReglaBloqueoDeAcceso.ValidoParaAccederHasta(documento, Cond(toleranciaDias: 15)).Should().Be(Hoy.AddDays(5));
    }

    [Fact]
    public void Una_periodicidad_especial_del_centro_cuenta_desde_la_fecha_de_emision_y_sustituye_al_vencimiento_del_documento()
    {
        // El documento trae un vencimiento propio ya pasado (12 meses), pero este Centro admite 36 meses desde la emision.
        var emision = new DateOnly(2025, 10, 3).AddYears(-1).AddDays(1);
        var documento = new DocumentoParaAcceso(VigenciaDocumento.VenceEl(Hoy.AddDays(-1)), emision);

        ReglaBloqueoDeAcceso.VencimientoEfectivo(documento, 36).Should().Be(emision.AddMonths(36));
        ReglaBloqueoDeAcceso.ValidoParaAcceder(documento, Cond(), Hoy).Should().BeFalse("con el vencimiento propio estaria vencido");
        ReglaBloqueoDeAcceso.ValidoParaAcceder(documento, Cond(periodicidadMeses: 36), Hoy).Should().BeTrue("este Centro acepta 36 meses");
    }

    [Fact]
    public void Una_periodicidad_especial_mas_corta_que_el_vencimiento_del_documento_tambien_manda()
    {
        var documento = new DocumentoParaAcceso(VigenciaDocumento.VenceEl(Hoy.AddYears(3)), Hoy.AddMonths(-13));

        ReglaBloqueoDeAcceso.ValidoParaAcceder(documento, Cond(), Hoy).Should().BeTrue();
        ReglaBloqueoDeAcceso.ValidoParaAcceder(documento, Cond(periodicidadMeses: 12), Hoy).Should().BeFalse("este Centro exige renovar cada 12 meses");
    }

    [Theory]
    [InlineData(0, 12, false)]
    [InlineData(15, 12, true)]
    public void La_tolerancia_se_suma_al_vencimiento_efectivo_con_periodicidad_especial(int toleranciaDias, int periodicidadMeses, bool valeParaAcceder)
    {
        // Emitido hace 12 meses y 10 dias: con 12 meses de periodicidad vencio hace 10 dias.
        var documento = new DocumentoParaAcceso(VigenciaDocumento.NoCaduca, Hoy.AddMonths(-12).AddDays(-10));

        // NoCaduca nunca vence, tenga el Centro la periodicidad que tenga.
        ReglaBloqueoDeAcceso.ValidoParaAcceder(documento, Cond(periodicidadMeses, toleranciaDias), Hoy).Should().BeTrue();

        var conFecha = new DocumentoParaAcceso(VigenciaDocumento.VenceEl(Hoy.AddYears(5)), Hoy.AddMonths(-12).AddDays(-10));
        ReglaBloqueoDeAcceso.ValidoParaAcceder(conFecha, Cond(periodicidadMeses, toleranciaDias), Hoy).Should().Be(valeParaAcceder);
    }

    [Fact]
    public void No_caduca_nunca_vence_ni_con_periodicidad_ni_con_tolerancia_0()
    {
        var documento = new DocumentoParaAcceso(VigenciaDocumento.NoCaduca, Hoy.AddYears(-30));

        ReglaBloqueoDeAcceso.VencimientoEfectivo(documento, 12).Should().BeNull();
        ReglaBloqueoDeAcceso.ValidoParaAcceder(documento, Cond(periodicidadMeses: 12), Hoy).Should().BeTrue();
        ReglaBloqueoDeAcceso.ValidoParaAcceder(documento, Cond(), Hoy).Should().BeTrue();
    }

    [Fact]
    public void Sin_confirmar_no_bloquea_si_el_centro_no_impone_periodicidad()
    {
        var documento = new DocumentoParaAcceso(VigenciaDocumento.SinConfirmar, Hoy.AddYears(-5));

        ReglaBloqueoDeAcceso.ValidoParaAcceder(documento, Cond(), Hoy).Should().BeTrue(
            "«Sin confirmar» no se decidio para el bloqueo: se conserva lo que ya hacia Mi trabajo");
        ReglaBloqueoDeAcceso.ValidoParaAcceder(new DocumentoParaAcceso(default, Hoy.AddYears(-5)), Cond(), Hoy).Should().BeTrue(
            "el valor por defecto del struct es «Sin confirmar»");
    }

    [Fact]
    public void Sin_confirmar_con_periodicidad_del_centro_vence_por_emision_mas_meses()
    {
        // Decision de la regla: la vigencia la define el Centro y no hace falta confirmar la del documento.
        var documento = new DocumentoParaAcceso(VigenciaDocumento.SinConfirmar, Hoy.AddMonths(-13));

        ReglaBloqueoDeAcceso.ValidoParaAcceder(documento, Cond(periodicidadMeses: 12), Hoy).Should().BeFalse();
        ReglaBloqueoDeAcceso.ValidoParaAcceder(documento, Cond(periodicidadMeses: 24), Hoy).Should().BeTrue();
    }

    [Theory]
    [InlineData(null, null, 0)]
    [InlineData(null, 10, 10)]
    [InlineData(5, 10, 5)]
    [InlineData(0, 10, 0)]
    [InlineData(20, null, 20)]
    public void La_tolerancia_que_rige_es_la_del_centro_si_la_personaliza_si_no_la_del_cliente_empresarial_si_no_0(
        int? delCentro, int? delClienteEmpresarial, int esperado)
    {
        ReglaBloqueoDeAcceso.ResolverToleranciaDias(delCentro, delClienteEmpresarial).Should().Be(esperado);
    }

    [Fact]
    public void Sin_ningun_documento_el_requisito_esta_ausente_y_bloquea()
    {
        var resultado = ReglaBloqueoDeAcceso.Evaluar([], Cond(toleranciaDias: 30), Hoy);

        resultado.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Ausente);
        resultado.VencimientoEfectivo.Should().BeNull();
        ReglaBloqueoDeAcceso.Bloquea(resultado.Situacion).Should().BeTrue("la tolerancia no cubre lo que nunca existio");
    }

    [Fact]
    public void Con_solo_documentos_vencidos_y_agotada_la_tolerancia_el_requisito_esta_vencido_y_bloquea_igual_que_el_ausente()
    {
        var resultado = ReglaBloqueoDeAcceso.Evaluar([VenceHaceDias(20), VenceHaceDias(400)], Cond(toleranciaDias: 15), Hoy);

        resultado.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Vencido);
        resultado.VencimientoEfectivo.Should().Be(Hoy.AddDays(-20), "el mas reciente de los vencidos");
        resultado.EnToleranciaHasta.Should().BeNull();
        ReglaBloqueoDeAcceso.Bloquea(resultado.Situacion).Should().BeTrue(
            "un vencido y un ausente bloquean de igual manera (decision del propietario del producto, 2026-10-03)");
    }

    [Fact]
    public void Un_vencido_dentro_de_la_tolerancia_cumple_el_requisito_y_expone_hasta_cuando()
    {
        var resultado = ReglaBloqueoDeAcceso.Evaluar([VenceHaceDias(10)], Cond(toleranciaDias: 15), Hoy);

        resultado.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Cumplido);
        resultado.EnToleranciaHasta.Should().Be(Hoy.AddDays(5));
        ReglaBloqueoDeAcceso.Bloquea(resultado.Situacion).Should().BeFalse();
    }

    /// <summary>
    /// «En tolerancia» (vista con contexto de Centro): vencido, pero con la tolerancia aún sin agotar. Es la misma comparación que
    /// la decisión de acceso, así que cada fila se contrasta con <see cref="ReglaBloqueoDeAcceso.Evaluar"/>.
    /// </summary>
    [Theory]
    [InlineData("Venció ayer, tolerancia 0", 1, 0, false)]
    [InlineData("Venció ayer, tolerancia 1: hoy es el último día", 1, 1, true)]
    [InlineData("Venció hace 5, tolerancia 10", 5, 10, true)]
    [InlineData("Venció hace 5, tolerancia 5: hoy es el último día", 5, 5, true)]
    [InlineData("Venció hace 5, tolerancia 4: ayer fue el último", 5, 4, false)]
    [InlineData("Venció hace un año, tolerancia 15", 365, 15, false)]
    public void Un_vencido_esta_en_tolerancia_hasta_el_ultimo_dia_en_que_vale_para_acceder(string caso, int diasDesdeQueVencio, int toleranciaDias, bool enTolerancia)
    {
        var documento = VenceHaceDias(diasDesdeQueVencio);
        var condiciones = Cond(toleranciaDias: toleranciaDias);

        var hasta = ReglaBloqueoDeAcceso.EnToleranciaHasta(documento, condiciones, Hoy);

        hasta.Should().Be(enTolerancia ? Hoy.AddDays(-diasDesdeQueVencio + toleranciaDias) : null, caso);
        (hasta is not null).Should().Be(ReglaBloqueoDeAcceso.ValidoParaAcceder(documento, condiciones, Hoy), "es la misma decisión que el acceso (documento ya vencido)");
        ReglaBloqueoDeAcceso.Evaluar([documento], condiciones, Hoy).EnToleranciaHasta.Should().Be(hasta, "una sola comparación para la vista y el acceso");
    }

    [Fact]
    public void Lo_que_no_ha_vencido_o_no_vence_nunca_esta_en_tolerancia()
    {
        var condiciones = Cond(toleranciaDias: 30);

        ReglaBloqueoDeAcceso.EnToleranciaHasta(new DocumentoParaAcceso(VigenciaDocumento.VenceEl(Hoy), Hoy.AddYears(-1)), condiciones, Hoy)
            .Should().BeNull("vence hoy: aún no ha vencido, vale por sí mismo");
        ReglaBloqueoDeAcceso.EnToleranciaHasta(new DocumentoParaAcceso(VigenciaDocumento.VenceEl(Hoy.AddDays(40)), Hoy.AddYears(-1)), condiciones, Hoy)
            .Should().BeNull();
        ReglaBloqueoDeAcceso.EnToleranciaHasta(new DocumentoParaAcceso(VigenciaDocumento.NoCaduca, Hoy.AddYears(-1)), condiciones, Hoy)
            .Should().BeNull("«No caduca» nunca vence");
        ReglaBloqueoDeAcceso.EnToleranciaHasta(new DocumentoParaAcceso(VigenciaDocumento.SinConfirmar, Hoy.AddYears(-1)), condiciones, Hoy)
            .Should().BeNull("sin vigencia anotada no hay vencimiento del que contar la tolerancia");
    }

    [Fact]
    public void La_tolerancia_se_cuenta_desde_el_vencimiento_efectivo_del_Centro_no_desde_el_del_documento()
    {
        // Emitido hace 14 meses; el documento dice que vence dentro de un año, pero el Centro impone 12 meses: venció hace ~2 meses.
        var documento = new DocumentoParaAcceso(VigenciaDocumento.VenceEl(Hoy.AddYears(1)), Hoy.AddMonths(-14));
        var vencioEn = Hoy.AddMonths(-2);

        ReglaBloqueoDeAcceso.EnToleranciaHasta(documento, Cond(periodicidadMeses: 12, toleranciaDias: 90), Hoy)
            .Should().Be(vencioEn.AddDays(90));
        ReglaBloqueoDeAcceso.EnToleranciaHasta(documento, Cond(periodicidadMeses: 12, toleranciaDias: 10), Hoy)
            .Should().BeNull("la tolerancia de 10 días ya se agotó");
    }

    [Fact]
    public void Un_vencido_con_su_renovacion_valida_cumple_el_requisito_sin_estar_en_tolerancia()
    {
        var renovacion = new DocumentoParaAcceso(VigenciaDocumento.VenceEl(Hoy.AddDays(335)), Hoy.AddDays(-30));
        var resultado = ReglaBloqueoDeAcceso.Evaluar([VenceHaceDias(30), renovacion], Cond(toleranciaDias: 15), Hoy);

        resultado.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Cumplido);
        resultado.EnToleranciaHasta.Should().BeNull("hay un documento valido sin necesidad de tolerancia");
    }

    [Fact]
    public void Una_fecha_centinela_o_una_periodicidad_desmesurada_no_lanzan_y_valen_para_siempre()
    {
        // Esta funcion corre en memoria sobre todos los documentos del Tenant: una fila rara no puede tirar Mi trabajo.
        var centinela = new DocumentoParaAcceso(VigenciaDocumento.VenceEl(DateOnly.MaxValue), Hoy.AddYears(-1));
        var emisionTardia = new DocumentoParaAcceso(VigenciaDocumento.VenceEl(Hoy), DateOnly.MaxValue);
        var normal = new DocumentoParaAcceso(VigenciaDocumento.VenceEl(Hoy), Hoy.AddYears(-1));

        ReglaBloqueoDeAcceso.ValidoParaAcceder(centinela, Cond(toleranciaDias: 15), Hoy).Should().BeTrue();
        ReglaBloqueoDeAcceso.ValidoParaAccederHasta(centinela, Cond(toleranciaDias: 365)).Should().Be(DateOnly.MaxValue);
        ReglaBloqueoDeAcceso.ValidoParaAcceder(normal, Cond(periodicidadMeses: int.MaxValue), Hoy).Should().BeTrue();
        ReglaBloqueoDeAcceso.VencimientoEfectivo(normal, int.MaxValue).Should().Be(DateOnly.MaxValue);
        ReglaBloqueoDeAcceso.ValidoParaAcceder(emisionTardia, Cond(periodicidadMeses: 12, toleranciaDias: 15), Hoy).Should().BeTrue();
        ReglaBloqueoDeAcceso.Evaluar([centinela, emisionTardia], Cond(periodicidadMeses: 12, toleranciaDias: 365), Hoy).Situacion
            .Should().Be(SituacionDeRequisitoBloqueante.Cumplido);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void Un_documento_que_vence_hoy_no_esta_en_tolerancia_aunque_el_Centro_la_tenga(int toleranciaDias)
    {
        // Vencer hoy es seguir vigente: la tolerancia solo empieza el dia SIGUIENTE al vencimiento efectivo.
        var resultado = ReglaBloqueoDeAcceso.Evaluar([new DocumentoParaAcceso(VigenciaDocumento.VenceEl(Hoy), Hoy.AddYears(-1))], Cond(toleranciaDias: toleranciaDias), Hoy);

        resultado.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Cumplido);
        resultado.EnToleranciaHasta.Should().BeNull();
        resultado.VencimientoEfectivo.Should().Be(Hoy);
    }

    [Theory]
    [InlineData("noCaduca")]
    [InlineData("sinConfirmar")]
    [InlineData("venceHoy")]
    public void Los_documentos_sin_fecha_o_que_vencen_hoy_cumplen_el_requisito(string caso)
    {
        VigenciaDocumento vigencia = caso switch
        {
            "noCaduca" => VigenciaDocumento.NoCaduca,
            "sinConfirmar" => VigenciaDocumento.SinConfirmar,
            _ => VigenciaDocumento.VenceEl(Hoy)
        };

        ReglaBloqueoDeAcceso.Evaluar([new DocumentoParaAcceso(vigencia, Hoy.AddYears(-1))], Cond(), Hoy).Situacion
            .Should().Be(SituacionDeRequisitoBloqueante.Cumplido);
    }

    [Theory]
    [InlineData(AmbitoAplicacion.Trabajador, true)]
    [InlineData(AmbitoAplicacion.Empresa, true)]
    [InlineData(AmbitoAplicacion.Cliente, false)]
    [InlineData(AmbitoAplicacion.Vehiculo, false)]
    [InlineData(AmbitoAplicacion.Proyecto, false)]
    public void Solo_los_tipos_de_Trabajador_y_de_Empresa_tienen_sujeto_del_bloqueo(AmbitoAplicacion ambito, bool puede)
    {
        ReglaBloqueoDeAcceso.AmbitoPuedeBloquear(ambito).Should().Be(puede);
    }

    [Fact]
    public void Cada_ambito_del_enum_esta_decidido_en_la_tabla()
    {
        // Un ambito nuevo hay que decidirlo en AmbitoPuedeBloquear Y en la tabla de arriba.
        Enum.GetValues<AmbitoAplicacion>().Should().BeEquivalentTo(
        [
            AmbitoAplicacion.Trabajador, AmbitoAplicacion.Cliente, AmbitoAplicacion.Empresa,
            AmbitoAplicacion.Vehiculo, AmbitoAplicacion.Proyecto
        ]);
    }
}
