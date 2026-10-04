using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// Vencimiento de un Documento EN UN CENTRO con periodicidad propia (decisión del propietario del producto, 2026-10-04):
/// <c>vence en el Centro = min(última presentación en el Centro + meses, vigencia propia del Documento)</c>, y sin ninguna
/// presentación en el Centro el ancla es la fecha de emisión. El estado de contexto (Vigente, Próximo, Urgente, En tolerancia,
/// Vencido) lo decide la misma regla única. Cada fila es una entrada de la tabla; el día de negocio es fijo.
/// </summary>
public class VencimientoPorPresentacionEnCentroTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 3);
    private const int Ambar = 30;
    private const int Rojo = 15;

    private static CondicionesDeAccesoDelCentro Cond(int? periodicidadMeses = null, int toleranciaDias = 0) => new(periodicidadMeses, toleranciaDias);

    // La formación del artículo 19 del ejemplo del propietario: emitida el 01/10/2026 con vigencia de cuatro años (hasta 01/10/2030).
    private static DocumentoParaAcceso Formacion(DateOnly? ultimaPresentacion = null) =>
        new(VigenciaDocumento.VenceEl(new DateOnly(2030, 10, 1)), new DateOnly(2026, 10, 1), ultimaPresentacion);

    [Fact]
    public void Ejemplo_del_propietario_sin_presentacion_el_ancla_es_la_emision()
    {
        ReglaBloqueoDeAcceso.VencimientoEfectivo(Formacion(), 12).Should().Be(new DateOnly(2027, 10, 1));
    }

    [Theory]
    [InlineData("2027-10-01", "2028-10-01")]
    [InlineData("2028-10-01", "2029-10-01")]
    [InlineData("2029-10-01", "2030-10-01")]
    public void Ejemplo_del_propietario_presentar_el_mismo_documento_cada_ano_reinicia_el_plazo_del_Centro(string presentada, string venceEnElCentro)
    {
        ReglaBloqueoDeAcceso.VencimientoEfectivo(Formacion(DateOnly.Parse(presentada)), 12).Should().Be(DateOnly.Parse(venceEnElCentro));
    }

    [Fact]
    public void Ejemplo_del_propietario_en_2030_vence_el_documento_y_el_minimo_con_la_vigencia_propia_manda()
    {
        // Presentado por ultima vez en diciembre de 2029: ancla + 12 meses seria 12/2030, pero el documento vence el 01/10/2030
        // y hace falta uno nuevo. Ningun Centro puede alargar la vigencia propia.
        var presentadaTarde = Formacion(new DateOnly(2029, 12, 1));

        ReglaBloqueoDeAcceso.VencimientoEfectivo(presentadaTarde, 12).Should().Be(new DateOnly(2030, 10, 1));
        ReglaBloqueoDeAcceso.VencimientoEfectivo(presentadaTarde, null).Should().Be(new DateOnly(2030, 10, 1), "en los demas Centros vale por su vigencia propia");
    }

    [Fact]
    public void Quitar_el_minimo_con_la_vigencia_propia_seria_observable()
    {
        // Ancla de 2029-12-01 + 12 meses = 2030-12-01 > 2030-10-01: si el resultado fuese solo ancla + meses, esto fallaria.
        ReglaBloqueoDeAcceso.VencimientoEfectivo(Formacion(new DateOnly(2029, 12, 1)), 12).Should().BeBefore(new DateOnly(2030, 12, 1));
    }

    [Fact]
    public void La_ultima_presentacion_es_el_ancla_no_la_emision()
    {
        // Presentada seis meses despues de emitirse: vence 12 meses despues de la presentacion, no de la emision.
        ReglaBloqueoDeAcceso.VencimientoEfectivo(Formacion(new DateOnly(2027, 4, 1)), 12).Should().Be(new DateOnly(2028, 4, 1));
        ReglaBloqueoDeAcceso.VencimientoEfectivo(Formacion(), 12).Should().Be(new DateOnly(2027, 10, 1));
    }

    [Fact]
    public void Sin_periodicidad_en_el_Centro_las_presentaciones_no_cambian_nada()
    {
        ReglaBloqueoDeAcceso.VencimientoEfectivo(Formacion(new DateOnly(2027, 4, 1)), null)
            .Should().Be(ReglaBloqueoDeAcceso.VencimientoEfectivo(Formacion(), null));
    }

    [Fact]
    public void No_caduca_sigue_sin_vencer_nunca_aunque_el_Centro_tenga_periodicidad()
    {
        // Contrato vigente con tests, declarado como hueco (GAP) en la entrega: no se cambia en este incremento.
        var documento = new DocumentoParaAcceso(VigenciaDocumento.NoCaduca, Hoy.AddYears(-30), Hoy.AddYears(-5));

        ReglaBloqueoDeAcceso.VencimientoEfectivo(documento, 12).Should().BeNull();
        ReglaBloqueoDeAcceso.EstadoEnElCentro(documento, Cond(12), Hoy, Ambar, Rojo).Should().Be(
            new EstadoDeDocumentoEnElCentro(EstadoDocumento.SinCaducidad, null, null));
    }

    [Fact]
    public void Sin_confirmar_con_periodicidad_vence_en_el_ancla_mas_meses()
    {
        var documento = new DocumentoParaAcceso(VigenciaDocumento.SinConfirmar, Hoy.AddMonths(-13), Hoy.AddMonths(-2));

        ReglaBloqueoDeAcceso.VencimientoEfectivo(documento, 12).Should().Be(Hoy.AddMonths(-2).AddMonths(12));
    }

    // -------- Estado en el Centro --------

    [Theory]
    [InlineData("Vigente a mas de 30 dias", 60, 0, EstadoDocumento.Vigente)]
    [InlineData("Proximo: 20 dias", 20, 0, EstadoDocumento.Proximo)]
    [InlineData("Urgente: 10 dias", 10, 0, EstadoDocumento.Urgente)]
    [InlineData("Vence hoy: Urgente", 0, 0, EstadoDocumento.Urgente)]
    [InlineData("Vencido ayer sin tolerancia", -1, 0, EstadoDocumento.Vencido)]
    [InlineData("Vencido ayer con 15 dias: en tolerancia", -1, 15, EstadoDocumento.EnTolerancia)]
    [InlineData("Vencido hace 15 con 15 dias: ultimo dia de tolerancia", -15, 15, EstadoDocumento.EnTolerancia)]
    [InlineData("Vencido hace 16 con 15 dias: la tolerancia se agoto", -16, 15, EstadoDocumento.Vencido)]
    public void El_estado_en_el_Centro_se_calcula_sobre_el_vencimiento_efectivo_en_el_Centro_y_la_tolerancia_se_suma_a_ese_vencimiento(
        string caso, int diasHastaVencerEnElCentro, int toleranciaDias, EstadoDocumento esperado)
    {
        // El documento sigue vigente 4 anos por su fecha propia: el estado lo manda lo que dice el Centro.
        var venceEnElCentro = Hoy.AddDays(diasHastaVencerEnElCentro);
        var documento = new DocumentoParaAcceso(VigenciaDocumento.VenceEl(Hoy.AddYears(4)), Hoy.AddYears(-1), venceEnElCentro.AddMonths(-12));

        var resultado = ReglaBloqueoDeAcceso.EstadoEnElCentro(documento, Cond(12, toleranciaDias), Hoy, Ambar, Rojo);

        resultado.Estado.Should().Be(esperado, caso);
        resultado.VenceEnElCentro.Should().Be(venceEnElCentro, caso);
        (resultado.EnToleranciaHasta is not null).Should().Be(esperado == EstadoDocumento.EnTolerancia);
        if (esperado == EstadoDocumento.EnTolerancia)
            resultado.EnToleranciaHasta.Should().Be(venceEnElCentro.AddDays(toleranciaDias), "la tolerancia se cuenta desde el vencimiento EN el Centro");
    }

    [Fact]
    public void Un_documento_vigente_por_su_fecha_puede_estar_vencido_en_el_Centro_y_en_los_demas_no()
    {
        // Emitido hace 13 meses, vigente 4 anos; este Centro pide presentarlo cada 12 meses y nunca se presento aqui.
        var documento = new DocumentoParaAcceso(VigenciaDocumento.VenceEl(Hoy.AddYears(3)), Hoy.AddMonths(-13));

        ReglaBloqueoDeAcceso.EstadoEnElCentro(documento, Cond(12), Hoy, Ambar, Rojo).Estado.Should().Be(EstadoDocumento.Vencido);
        ReglaBloqueoDeAcceso.EstadoEnElCentro(documento, Cond(), Hoy, Ambar, Rojo).Estado.Should().Be(EstadoDocumento.Vigente);
        ReglaBloqueoDeAcceso.EstadoEnElCentro(documento, Cond(), Hoy, Ambar, Rojo).VenceEnElCentro
            .Should().BeNull("sin periodicidad en el Centro no hay una segunda fecha que mostrar");
    }

    [Fact]
    public void Volver_a_presentar_en_el_Centro_devuelve_el_estado_Vigente()
    {
        var sinPresentar = new DocumentoParaAcceso(VigenciaDocumento.VenceEl(Hoy.AddYears(3)), Hoy.AddMonths(-13));
        var presentadoHoy = sinPresentar with { UltimaPresentacionEnElCentro = Hoy };

        ReglaBloqueoDeAcceso.EstadoEnElCentro(presentadoHoy, Cond(12), Hoy, Ambar, Rojo)
            .Should().Be(new EstadoDeDocumentoEnElCentro(EstadoDocumento.Vigente, Hoy.AddMonths(12), null));
    }

    [Fact]
    public void Sin_periodicidad_el_estado_en_el_Centro_es_el_estado_de_la_calculadora_mas_la_tolerancia()
    {
        // No regresion: sin periodicidad especial, EstadoEnElCentro == CalculadoraEstadoDocumento (+ En tolerancia).
        foreach (var dias in new[] { -400, -16, -15, -1, 0, 1, 10, 15, 16, 29, 30, 31, 400 })
        {
            foreach (var tolerancia in new[] { 0, 15 })
            {
                var vigencia = VigenciaDocumento.VenceEl(Hoy.AddDays(dias));
                var propio = CalculadoraEstadoDocumento.Calcular(vigencia, Hoy, Ambar, Rojo);
                var documento = new DocumentoParaAcceso(vigencia, Hoy.AddYears(-1), Hoy.AddMonths(-3));

                var enElCentro = ReglaBloqueoDeAcceso.EstadoEnElCentro(documento, Cond(null, tolerancia), Hoy, Ambar, Rojo).Estado;

                var esperado = propio == EstadoDocumento.Vencido && dias + tolerancia >= 0 ? EstadoDocumento.EnTolerancia : propio;
                enElCentro.Should().Be(esperado, $"dias={dias}, tolerancia={tolerancia}");
            }
        }

        foreach (var vigencia in new[] { VigenciaDocumento.NoCaduca, VigenciaDocumento.SinConfirmar })
        {
            ReglaBloqueoDeAcceso.EstadoEnElCentro(new DocumentoParaAcceso(vigencia, Hoy.AddYears(-1)), Cond(null, 15), Hoy, Ambar, Rojo).Estado
                .Should().Be(CalculadoraEstadoDocumento.Calcular(vigencia, Hoy, Ambar, Rojo));
        }
    }

    // -------- Volver a presentar --------

    [Theory]
    [InlineData("Vigente por su fecha, con periodicidad: se ofrece", 400, 12, true, true)]
    [InlineData("Vence hoy por su fecha: todavia vigente", 0, 12, true, true)]
    [InlineData("Vencido por su fecha: se renueva, no se vuelve a presentar", -1, 12, true, false)]
    [InlineData("Sin periodicidad en el Centro", 400, null, true, false)]
    [InlineData("Documento en el historial (sustituido)", 400, 12, false, false)]
    public void Volver_a_presentar_solo_si_el_Centro_exige_periodicidad_el_documento_es_operativo_y_sigue_vigente_por_su_fecha(
        string caso, int diasHastaVencerPorSuFecha, int? periodicidad, bool operativo, bool esperado)
    {
        var documento = new DocumentoParaAcceso(VigenciaDocumento.VenceEl(Hoy.AddDays(diasHastaVencerPorSuFecha)), Hoy.AddYears(-1));

        ReglaBloqueoDeAcceso.PuedeVolverAPresentar(documento, Cond(periodicidad), Hoy, operativo).Should().Be(esperado, caso);
    }

    [Fact]
    public void Volver_a_presentar_un_documento_que_no_caduca_no_aplica_y_uno_sin_confirmar_si()
    {
        ReglaBloqueoDeAcceso.PuedeVolverAPresentar(new DocumentoParaAcceso(VigenciaDocumento.NoCaduca, Hoy.AddYears(-3)), Cond(12), Hoy, true)
            .Should().BeFalse("no vence: no hay plazo propio que acotar ni nada que reiniciar");
        ReglaBloqueoDeAcceso.PuedeVolverAPresentar(new DocumentoParaAcceso(VigenciaDocumento.SinConfirmar, Hoy.AddYears(-3)), Cond(12), Hoy, true)
            .Should().BeTrue("no esta vencido y la periodicidad la define el Centro");
    }

    // -------- Tope de la periodicidad --------

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(120)]
    public void La_periodicidad_admite_de_1_a_120_meses(int meses)
    {
        new TipoDocumentoCentro(Guid.NewGuid(), Guid.NewGuid(), periodicidadEspecialMeses: meses).PeriodicidadEspecialMeses.Should().Be(meses);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(121)]
    [InlineData(int.MaxValue)]
    public void La_periodicidad_fuera_de_1_a_120_meses_se_rechaza_al_crear_y_al_actualizar(int meses)
    {
        FluentActions.Invoking(() => new TipoDocumentoCentro(Guid.NewGuid(), Guid.NewGuid(), periodicidadEspecialMeses: meses))
            .Should().Throw<ArgumentException>().WithMessage("*entre 1 y 120*");

        var fila = new TipoDocumentoCentro(Guid.NewGuid(), Guid.NewGuid(), periodicidadEspecialMeses: 12);
        FluentActions.Invoking(() => fila.Actualizar(true, meses, false, null, null, null)).Should().Throw<ArgumentException>();
        fila.PeriodicidadEspecialMeses.Should().Be(12, "una actualizacion rechazada no cambia la fila");
    }

    [Fact]
    public void Sin_periodicidad_es_valido()
    {
        new TipoDocumentoCentro(Guid.NewGuid(), Guid.NewGuid()).PeriodicidadEspecialMeses.Should().BeNull();
        TipoDocumentoCentro.PeriodicidadEspecialMinimaMeses.Should().Be(1);
        TipoDocumentoCentro.PeriodicidadEspecialMaximaMeses.Should().Be(120);
    }

    // -------- La entidad --------

    [Fact]
    public void Una_presentacion_guarda_su_documento_su_centro_su_dia_y_su_origen()
    {
        var documentoId = Guid.NewGuid();
        var centroId = Guid.NewGuid();
        var ahora = DateTime.UtcNow;

        var presentacion = new PresentacionDocumentoEnCentro(documentoId, centroId, Hoy, OrigenPresentacionDocumentoEnCentro.VolverAPresentar, ahora);

        presentacion.DocumentoId.Should().Be(documentoId);
        presentacion.CentroId.Should().Be(centroId);
        presentacion.FechaPresentacion.Should().Be(Hoy);
        presentacion.Origen.Should().Be(OrigenPresentacionDocumentoEnCentro.VolverAPresentar);
        presentacion.RegistradaEnUtc.Should().Be(ahora);
    }

    [Fact]
    public void Una_presentacion_exige_documento_centro_y_origen_validos()
    {
        FluentActions.Invoking(() => new PresentacionDocumentoEnCentro(Guid.Empty, Guid.NewGuid(), Hoy, OrigenPresentacionDocumentoEnCentro.EnvioPorCorreo, DateTime.UtcNow))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new PresentacionDocumentoEnCentro(Guid.NewGuid(), Guid.Empty, Hoy, OrigenPresentacionDocumentoEnCentro.EnvioPorCorreo, DateTime.UtcNow))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new PresentacionDocumentoEnCentro(Guid.NewGuid(), Guid.NewGuid(), Hoy, (OrigenPresentacionDocumentoEnCentro)99, DateTime.UtcNow))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Los_ordinales_del_origen_estan_congelados_porque_se_persisten_como_entero()
    {
        ((int)OrigenPresentacionDocumentoEnCentro.SubidaAPlataforma).Should().Be(0);
        ((int)OrigenPresentacionDocumentoEnCentro.AceptadaEnPlataforma).Should().Be(1);
        ((int)OrigenPresentacionDocumentoEnCentro.VolverAPresentar).Should().Be(2);
        ((int)OrigenPresentacionDocumentoEnCentro.EnvioPorCorreo).Should().Be(3);
    }
}
