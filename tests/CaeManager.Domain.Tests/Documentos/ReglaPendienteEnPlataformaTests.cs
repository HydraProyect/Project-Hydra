using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// «Pendiente en la plataforma» (decisión del propietario, 2026-10-10): un documento que todavía vale <b>en ese Centro</b> y
/// que en la plataforma CAE de ese Centro está sin subir o subido sin validar. Rechazada no es Pendiente (sigue siendo causa
/// propia de Bloqueado, D-7); Validada y No requerida no son trabajo pendiente.
/// </summary>
public class ReglaPendienteEnPlataformaTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 3);

    private static ResultadoDeRequisito Cumple() => new(SituacionDeRequisitoBloqueante.Cumplido, null, null);

    private static CondicionesDeAccesoDelCentro Cond(int? periodicidadMeses = null, int toleranciaDias = 0) =>
        new(periodicidadMeses, toleranciaDias);

    private static DocumentoParaAcceso Documento(EstadoVigenciaDocumento vigencia, DateOnly? vence, DateOnly emision) =>
        new(VigenciaDocumento.Rehidratar(vigencia, vence), emision);

    [Theory]
    [InlineData(EstadoAcreditacion.PendienteDeSubir, true)]
    [InlineData(EstadoAcreditacion.Subida, true)]
    [InlineData(EstadoAcreditacion.Aceptada, false)]
    [InlineData(EstadoAcreditacion.Rechazada, false)]
    [InlineData(EstadoAcreditacion.NoRequerida, false)]
    public void Solo_sin_subir_y_subido_sin_validar_estan_pendientes(EstadoAcreditacion estado, bool pendiente)
    {
        ReglaPendienteEnPlataforma.EstaPendiente(estado).Should().Be(pendiente);
        ReglaPendienteEnPlataforma.CuentaEnElCentro(estado, Cumple()).Should().Be(pendiente);
    }

    [Fact]
    public void El_filtro_para_SQL_sale_de_la_misma_regla()
    {
        ReglaPendienteEnPlataforma.EstadosPendientes.Should().BeEquivalentTo(
            [EstadoAcreditacion.PendienteDeSubir, EstadoAcreditacion.Subida]);
    }

    [Theory]
    [InlineData(EstadoVigenciaDocumento.VenceEnFecha)]
    [InlineData(EstadoVigenciaDocumento.NoCaduca)]
    [InlineData(EstadoVigenciaDocumento.SinConfirmar)]
    public void Un_documento_que_vale_en_el_Centro_cuenta_como_Pendiente_incluido_el_sin_confirmar(EstadoVigenciaDocumento vigencia)
    {
        // «Sin confirmar» viaja en el paquete de acreditación (decisión del 2026-10-03): sin subir es trabajo pendiente.
        DateOnly? vence = vigencia == EstadoVigenciaDocumento.VenceEnFecha ? Hoy.AddMonths(6) : null;
        var requisito = ReglaBloqueoDeAcceso.Evaluar([Documento(vigencia, vence, Hoy.AddMonths(-2))], Cond(), Hoy);

        requisito.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Cumplido, "control: el documento vale en el Centro");
        ReglaPendienteEnPlataforma.CuentaEnElCentro(EstadoAcreditacion.PendienteDeSubir, requisito).Should().BeTrue();
        ReglaPendienteEnPlataforma.CuentaEnElCentro(EstadoAcreditacion.Subida, requisito).Should().BeTrue();
    }

    [Fact]
    public void Un_documento_vencido_o_ausente_en_el_Centro_no_es_Pendiente()
    {
        // Ya es causa más grave por sí mismo, y renovarlo reinicia la acreditación.
        var vencido = ReglaBloqueoDeAcceso.Evaluar(
            [Documento(EstadoVigenciaDocumento.VenceEnFecha, Hoy.AddDays(-1), Hoy.AddYears(-1))], Cond(), Hoy);
        var ausente = ReglaBloqueoDeAcceso.Evaluar([], Cond(), Hoy);

        vencido.Situacion.Should().NotBe(SituacionDeRequisitoBloqueante.Cumplido, "control: el vencido no cumple");
        ReglaPendienteEnPlataforma.CuentaEnElCentro(EstadoAcreditacion.PendienteDeSubir, vencido).Should().BeFalse();
        ReglaPendienteEnPlataforma.CuentaEnElCentro(EstadoAcreditacion.PendienteDeSubir, ausente).Should().BeFalse();
    }

    [Fact]
    public void Un_documento_que_solo_vale_por_la_tolerancia_del_Centro_no_es_Pendiente()
    {
        var documento = Documento(EstadoVigenciaDocumento.VenceEnFecha, Hoy.AddDays(-10), Hoy.AddYears(-1));

        var conTolerancia = ReglaBloqueoDeAcceso.Evaluar([documento], Cond(toleranciaDias: 15), Hoy);

        conTolerancia.EnToleranciaHasta.Should().NotBeNull("control: en este Centro todavía vale, pero solo por la tolerancia");
        ReglaPendienteEnPlataforma.CuentaEnElCentro(EstadoAcreditacion.PendienteDeSubir, conTolerancia).Should().BeFalse();
    }

    /// <summary>
    /// La vigencia que cuenta es la del requisito en ese Centro, no la fecha del documento: el mismo documento puede valer
    /// en un Centro y estar vencido en otro, y solo en el primero es un Pendiente.
    /// </summary>
    [Fact]
    public void La_vigencia_del_Pendiente_es_la_del_Centro_no_la_del_documento()
    {
        // Emitido hace 8 meses, vence dentro de 2 años: vigente por su fecha propia.
        var documento = Documento(EstadoVigenciaDocumento.VenceEnFecha, Hoy.AddYears(2), Hoy.AddMonths(-8));

        var centroSinPeriodicidad = ReglaBloqueoDeAcceso.Evaluar([documento], Cond(), Hoy);
        var centroQueRenuevaCada6Meses = ReglaBloqueoDeAcceso.Evaluar([documento], Cond(periodicidadMeses: 6), Hoy);

        ReglaPendienteEnPlataforma.CuentaEnElCentro(EstadoAcreditacion.PendienteDeSubir, centroSinPeriodicidad).Should().BeTrue();
        ReglaPendienteEnPlataforma.CuentaEnElCentro(EstadoAcreditacion.PendienteDeSubir, centroQueRenuevaCada6Meses)
            .Should().BeFalse("en un Centro con periodicidad de 6 meses el documento está vencido: es causa más grave, no un Pendiente");

        // Al revés: vencido por su fecha propia, pero este Centro acepta 36 meses desde la emisión.
        var vencidoPorFechaPropia = Documento(EstadoVigenciaDocumento.VenceEnFecha, Hoy.AddDays(-1), Hoy.AddMonths(-20));
        ReglaPendienteEnPlataforma.CuentaEnElCentro(
                EstadoAcreditacion.PendienteDeSubir,
                ReglaBloqueoDeAcceso.Evaluar([vencidoPorFechaPropia], Cond(periodicidadMeses: 36), Hoy))
            .Should().BeTrue("la periodicidad del Centro lo mantiene vigente allí");
    }

    /// <summary>
    /// Un llamador que construye un par y se olvida del Pendiente lo daría por «no pendiente» sin enterarse: es un
    /// parámetro obligatorio, así que omitirlo no compila. Este test lo ancla (también contra una vuelta a <c>= false</c>).
    /// </summary>
    [Fact]
    public void El_Pendiente_en_la_plataforma_de_un_par_es_un_parametro_obligatorio()
    {
        var parametro = typeof(ParDocumentalExigido).GetConstructors()
            .Single(c => c.GetParameters().Length == 8)
            .GetParameters().Single(p => p.Name == nameof(ParDocumentalExigido.PendienteEnPlataforma));

        parametro.HasDefaultValue.Should().BeFalse("quien construye un par decide siempre si está pendiente en la plataforma");
    }

    [Fact]
    public void Un_par_vigente_pendiente_en_la_plataforma_de_su_Centro_no_esta_al_dia_en_el_porcentaje_del_Centro()
    {
        var centro = Guid.NewGuid();
        var alDia = new ParDocumentalExigido(centro, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EstadoDocumento.Vigente, PendienteEnPlataforma: false, EsRenuncia: false);
        var pendiente = alDia with { TrabajadorId = Guid.NewGuid(), PendienteEnPlataforma = true };

        CumplimientoDocumental.EsConforme(alDia, ContextoCumplimiento.Centro).Should().BeTrue();
        CumplimientoDocumental.EsConforme(pendiente, ContextoCumplimiento.Centro).Should().BeFalse();
        CumplimientoDocumental.De(ContextoCumplimiento.Centro, centro, [alDia, pendiente])
            .Should().Be(new FraccionCumplimiento(1, 2), "el pendiente entra en el denominador y no en el numerador");
    }

    /// <summary>
    /// Decisión del propietario, 2026-10-10: los pares de Empresa entran en el denominador del Centro y su estado cuenta igual
    /// que el de un Trabajador (al día, vencido, faltante o pendiente en la plataforma).
    /// </summary>
    [Theory]
    [InlineData(EstadoDocumento.Vigente, false, 2, 2)]
    [InlineData(EstadoDocumento.Vencido, false, 1, 2)]
    [InlineData(EstadoDocumento.Faltante, false, 1, 2)]
    [InlineData(EstadoDocumento.Proximo, false, 2, 2)]
    [InlineData(EstadoDocumento.Vigente, true, 1, 2)]
    public void Un_par_de_Empresa_entra_en_el_porcentaje_del_Centro_con_el_mismo_criterio_que_uno_de_Trabajador(
        EstadoDocumento estado, bool pendiente, int alDia, int requeridos)
    {
        var centro = Guid.NewGuid();
        var deTrabajador = new ParDocumentalExigido(centro, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EstadoDocumento.Vigente, false, false);
        var deEmpresa = new ParDocumentalDeEmpresaExigido(centro, Guid.NewGuid(), Guid.NewGuid(), estado, pendiente, EsRenuncia: false);

        CumplimientoDocumental.PorCentro([deTrabajador], [deEmpresa])[centro].Should().Be(new FraccionCumplimiento(alDia, requeridos));
    }

    [Fact]
    public void Un_Centro_con_solo_pares_de_Empresa_tambien_tiene_porcentaje_y_los_otros_contextos_no_los_reciben()
    {
        var centro = Guid.NewGuid();
        var deEmpresa = new ParDocumentalDeEmpresaExigido(centro, Guid.NewGuid(), Guid.NewGuid(), EstadoDocumento.Vencido, false, false);

        CumplimientoDocumental.PorCentro([], [deEmpresa])[centro].Should().Be(new FraccionCumplimiento(0, 1));
        CumplimientoDocumental.PorCentro([], []).Should().BeEmpty();
        // Control: el contexto Empresa de los pares de Trabajador no cambia (el par de Empresa no es un ParDocumentalExigido).
        CumplimientoDocumental.PorContexto(ContextoCumplimiento.Empresa, Array.Empty<ParDocumentalExigido>()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(ContextoCumplimiento.Trabajador)]
    [InlineData(ContextoCumplimiento.Empresa)]
    [InlineData(ContextoCumplimiento.ClienteEmpresarial)]
    public void El_pendiente_en_la_plataforma_no_baja_el_porcentaje_de_los_demas_contextos(ContextoCumplimiento contexto)
    {
        // Cada porcentaje mide su contexto (decisión 2026-10-03): la acreditación en la plataforma es del Centro.
        var par = new ParDocumentalExigido(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            EstadoDocumento.Vigente, PendienteEnPlataforma: true, EsRenuncia: false);

        CumplimientoDocumental.EsConforme(par, contexto).Should().BeTrue();
        CumplimientoDocumental.EsConforme(par, ContextoCumplimiento.Centro).Should().BeFalse("control positivo");
    }
}
