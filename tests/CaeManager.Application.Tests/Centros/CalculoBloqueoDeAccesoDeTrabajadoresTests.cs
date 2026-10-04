using CaeManager.Application.Centros;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// El bloqueo de acceso POR CENTRO (decisión del propietario del producto, 2026-10-03, y su corrección de la tarde) sobre la
/// función pura que lo aplica, sin base de datos: quién queda bloqueado, en qué Centros y por qué requisito, con la vigencia
/// propia y la tolerancia de cada Centro. El aislamiento entre Tenants y la carga real de datos los prueba
/// <c>CoherenciaDelBloqueoDeAccesoEntreSuperficiesTests</c> (Integración, Postgres con RLS).
/// </summary>
public class CalculoBloqueoDeAccesoDeTrabajadoresTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 3);

    private static readonly Guid CentroA = Guid.NewGuid();
    private static readonly Guid CentroB = Guid.NewGuid();
    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid Beto = Guid.NewGuid();
    private static readonly Guid EmpresaX = Guid.NewGuid();
    private static readonly Guid EmpresaY = Guid.NewGuid();
    private static readonly Guid TipoPss = Guid.NewGuid();
    private static readonly Guid TipoApto = Guid.NewGuid();
    private static readonly Guid TipoCertificadoSs = Guid.NewGuid();

    private static RequisitoBloqueanteDelCentro DeTrabajadorEn(Guid centro, Guid tipo, int toleranciaDias = 0, int? periodicidadMeses = null) =>
        new(centro, tipo, AmbitoAplicacion.Trabajador, new CondicionesDeAccesoDelCentro(periodicidadMeses, toleranciaDias));

    private static RequisitoBloqueanteDelCentro DeEmpresaEn(Guid centro, Guid tipo, int toleranciaDias = 0, int? periodicidadMeses = null) =>
        new(centro, tipo, AmbitoAplicacion.Empresa, new CondicionesDeAccesoDelCentro(periodicidadMeses, toleranciaDias));

    private static DocumentoParaAcceso Doc(VigenciaDocumento vigencia, DateOnly? emision = null) => new(vigencia, emision ?? Hoy.AddYears(-1));

    private static DocumentoParaBloqueo DeTrabajador(Guid trabajador, Guid tipo, VigenciaDocumento vigencia, DateOnly? emision = null) =>
        new(trabajador, null, tipo, Doc(vigencia, emision));

    private static DocumentoParaBloqueo DeEmpresa(Guid empresa, Guid tipo, VigenciaDocumento vigencia, DateOnly? emision = null) =>
        new(null, empresa, tipo, Doc(vigencia, emision));

    private static IReadOnlyList<BloqueoDeAccesoDeTrabajador> Calcular(
        IReadOnlyCollection<AsignacionParaBloqueo> asignaciones,
        IReadOnlyCollection<RequisitoBloqueanteDelCentro> requisitos,
        params DocumentoParaBloqueo[] documentos) =>
        CalculoBloqueoDeAccesoDeTrabajadores.Calcular(asignaciones, requisitos, documentos, Hoy);

    // ---------- R1: documento bloqueante de Trabajador, por Centro ----------

    [Fact]
    public void R1_un_bloqueante_de_Trabajador_ausente_bloquea_a_ese_Trabajador_en_ese_Centro()
    {
        var bloqueos = Calcular([new(CentroA, Ana, EmpresaX)], [DeTrabajadorEn(CentroA, TipoPss)]);

        var bloqueo = bloqueos.Should().ContainSingle().Subject;
        bloqueo.TrabajadorId.Should().Be(Ana);
        bloqueo.CentroId.Should().Be(CentroA);
        bloqueo.Ambito.Should().Be(AmbitoAplicacion.Trabajador);
        bloqueo.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Ausente);
    }

    [Fact]
    public void Una_alta_nueva_sin_ninguna_documentacion_esta_bloqueada_sin_excepcion()
    {
        // Sustituye a la advertencia de «alta nueva» del 2026-08-16: sin documentos, bloqueado, por cada requisito del Centro.
        var bloqueos = Calcular([new(CentroA, Ana, EmpresaX)], [DeTrabajadorEn(CentroA, TipoPss), DeTrabajadorEn(CentroA, TipoApto)]);

        bloqueos.Should().HaveCount(2);
        bloqueos.Should().OnlyContain(b => b.Situacion == SituacionDeRequisitoBloqueante.Ausente);
    }

    [Fact]
    public void R1_un_bloqueante_de_Trabajador_vencido_bloquea_igual_que_el_ausente()
    {
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX)], [DeTrabajadorEn(CentroA, TipoPss)],
            DeTrabajador(Ana, TipoPss, VigenciaDocumento.VenceEl(Hoy.AddDays(-1))));

        var bloqueo = bloqueos.Should().ContainSingle().Subject;
        bloqueo.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Vencido);
        bloqueo.VencimientoEfectivo.Should().Be(Hoy.AddDays(-1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(400)]
    public void R1_Proximo_Urgente_o_Vigente_validos_hoy_no_bloquean(int diasHastaVencer)
    {
        Calcular(
            [new(CentroA, Ana, EmpresaX)], [DeTrabajadorEn(CentroA, TipoPss)],
            DeTrabajador(Ana, TipoPss, VigenciaDocumento.VenceEl(Hoy.AddDays(diasHastaVencer))))
            .Should().BeEmpty();
    }

    [Fact]
    public void R1_Sin_confirmar_y_No_caduca_no_bloquean_se_conserva_lo_que_hacia_Mi_trabajo()
    {
        Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroA, Beto, EmpresaX)], [DeTrabajadorEn(CentroA, TipoPss)],
            DeTrabajador(Ana, TipoPss, VigenciaDocumento.SinConfirmar),
            DeTrabajador(Beto, TipoPss, VigenciaDocumento.NoCaduca))
            .Should().BeEmpty();
    }

    [Fact]
    public void R1_el_vencido_con_su_renovacion_valida_no_bloquea()
    {
        Calcular(
            [new(CentroA, Ana, EmpresaX)], [DeTrabajadorEn(CentroA, TipoPss)],
            DeTrabajador(Ana, TipoPss, VigenciaDocumento.VenceEl(Hoy.AddDays(-30))),
            DeTrabajador(Ana, TipoPss, VigenciaDocumento.VenceEl(Hoy.AddDays(335))))
            .Should().BeEmpty();
    }

    [Fact]
    public void R1_solo_el_Trabajador_sin_el_documento_queda_bloqueado_su_companero_no()
    {
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroA, Beto, EmpresaX)], [DeTrabajadorEn(CentroA, TipoPss)],
            DeTrabajador(Beto, TipoPss, VigenciaDocumento.VenceEl(Hoy.AddDays(100))));

        bloqueos.Should().ContainSingle().Which.TrabajadorId.Should().Be(Ana);
    }

    [Fact]
    public void R1_el_requisito_del_Trabajador_es_el_que_marca_la_fila_de_su_Centro()
    {
        // PSS es bloqueante solo en el Centro A: en el B, Ana no esta bloqueada por no tenerlo.
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroB, Ana, EmpresaX)], [DeTrabajadorEn(CentroA, TipoPss)]);

        bloqueos.Should().ContainSingle().Which.CentroId.Should().Be(CentroA);
    }

    [Fact]
    public void R1_el_mismo_documento_vale_en_un_Centro_con_tolerancia_y_bloquea_en_otro_sin_ella()
    {
        // PSS vencido hace 10 dias: el Centro A concede 15 dias, el B ninguno. Ana, asignada a los dos, solo esta bloqueada en B.
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroB, Ana, EmpresaX)],
            [DeTrabajadorEn(CentroA, TipoPss, toleranciaDias: 15), DeTrabajadorEn(CentroB, TipoPss, toleranciaDias: 0)],
            DeTrabajador(Ana, TipoPss, VigenciaDocumento.VenceEl(Hoy.AddDays(-10))));

        var bloqueo = bloqueos.Should().ContainSingle().Subject;
        bloqueo.CentroId.Should().Be(CentroB);
        bloqueo.ToleranciaDias.Should().Be(0);
    }

    [Fact]
    public void R1_agotada_la_tolerancia_bloquea_y_lo_dice()
    {
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX)], [DeTrabajadorEn(CentroA, TipoPss, toleranciaDias: 15)],
            DeTrabajador(Ana, TipoPss, VigenciaDocumento.VenceEl(Hoy.AddDays(-16))));

        var bloqueo = bloqueos.Should().ContainSingle().Subject;
        bloqueo.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Vencido);
        bloqueo.ToleranciaDias.Should().Be(15, "con tolerancia mayor que 0, el vencido es un fin de tolerancia");
    }

    [Fact]
    public void R1_la_periodicidad_especial_del_Centro_nunca_alarga_la_vigencia_propia_del_documento()
    {
        // Contrato nuevo (2026-10-04): un Centro puede ser MAS estricto que la vigencia propia, nunca menos. Un documento ya vencido
        // por su cuenta lo esta en todos los Centros, tengan la periodicidad que tengan.
        var emision = Hoy.AddYears(-2);
        var vencidoPorSuCuenta = VigenciaDocumento.VenceEl(Hoy.AddDays(-30));

        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroB, Ana, EmpresaX)],
            [DeTrabajadorEn(CentroA, TipoPss, periodicidadMeses: 36), DeTrabajadorEn(CentroB, TipoPss, periodicidadMeses: 12)],
            DeTrabajador(Ana, TipoPss, vencidoPorSuCuenta, emision));

        bloqueos.Select(b => b.CentroId).Should().BeEquivalentTo([CentroA, CentroB]);
    }

    [Fact]
    public void R1_la_periodicidad_del_Centro_solo_bloquea_en_el_Centro_que_la_exige_y_la_presentacion_en_ese_Centro_lo_desbloquea()
    {
        // Emitido hace 13 meses, vigente 3 anos mas. CentroA exige presentarlo cada 12 meses; CentroB no impone nada.
        var emision = Hoy.AddMonths(-13);
        var vigencia = VigenciaDocumento.VenceEl(Hoy.AddYears(3));
        var requisitos = new[] { DeTrabajadorEn(CentroA, TipoPss, periodicidadMeses: 12), DeTrabajadorEn(CentroB, TipoPss) };
        var asignaciones = new AsignacionParaBloqueo[] { new(CentroA, Ana, EmpresaX), new(CentroB, Ana, EmpresaX) };

        var sinPresentar = Calcular(asignaciones, requisitos, DeTrabajador(Ana, TipoPss, vigencia, emision));
        sinPresentar.Should().ContainSingle().Which.CentroId.Should().Be(CentroA, "solo el Centro con periodicidad la da por vencida");

        // Presentado hoy en CentroA: lo que cuenta es la presentacion EN ESE Centro. Una presentacion en CentroB no lo desbloquea.
        var enA = DeTrabajador(Ana, TipoPss, vigencia, emision) with
        {
            UltimaPresentacionPorCentro = new Dictionary<Guid, DateOnly> { [CentroA] = Hoy },
        };
        Calcular(asignaciones, requisitos, enA).Should().BeEmpty();

        var soloEnB = DeTrabajador(Ana, TipoPss, vigencia, emision) with
        {
            UltimaPresentacionPorCentro = new Dictionary<Guid, DateOnly> { [CentroB] = Hoy },
        };
        Calcular(asignaciones, requisitos, soloEnB).Should().ContainSingle().Which.CentroId.Should().Be(CentroA);
    }

    [Fact]
    public void R1_una_presentacion_sin_periodicidad_en_el_Centro_no_cambia_nada()
    {
        var vigencia = VigenciaDocumento.VenceEl(Hoy.AddDays(-1));
        var presentado = DeTrabajador(Ana, TipoPss, vigencia) with
        {
            UltimaPresentacionPorCentro = new Dictionary<Guid, DateOnly> { [CentroA] = Hoy },
        };

        Calcular([new(CentroA, Ana, EmpresaX)], [DeTrabajadorEn(CentroA, TipoPss)], presentado)
            .Should().ContainSingle("presentar un documento vencido por su cuenta no lo devuelve a la vida");
    }

    // ---------- R2: documento bloqueante de Empresa, por Centro ----------

    [Fact]
    public void R2_un_bloqueante_de_Empresa_ausente_bloquea_a_todos_sus_Trabajadores_del_Centro_que_lo_exige_aunque_su_documentacion_personal_este_completa()
    {
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroA, Beto, EmpresaX)],
            [DeTrabajadorEn(CentroA, TipoPss), DeEmpresaEn(CentroA, TipoCertificadoSs)],
            DeTrabajador(Ana, TipoPss, VigenciaDocumento.VenceEl(Hoy.AddDays(200))),
            DeTrabajador(Beto, TipoPss, VigenciaDocumento.NoCaduca));

        bloqueos.Should().HaveCount(2);
        bloqueos.Select(b => b.TrabajadorId).Should().BeEquivalentTo([Ana, Beto]);
        bloqueos.Should().OnlyContain(b =>
            b.Ambito == AmbitoAplicacion.Empresa
            && b.EmpresaId == EmpresaX
            && b.TipoDocumentoId == TipoCertificadoSs
            && b.Situacion == SituacionDeRequisitoBloqueante.Ausente);
    }

    [Fact]
    public void R2_un_bloqueante_de_Empresa_vencido_bloquea_igual_que_el_ausente()
    {
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX)], [DeEmpresaEn(CentroA, TipoCertificadoSs)],
            DeEmpresa(EmpresaX, TipoCertificadoSs, VigenciaDocumento.VenceEl(Hoy.AddDays(-1))));

        var bloqueo = bloqueos.Should().ContainSingle().Subject;
        bloqueo.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Vencido);
        bloqueo.Ambito.Should().Be(AmbitoAplicacion.Empresa);
    }

    [Fact]
    public void R2_solo_bloquea_en_los_Centros_que_exigen_el_documento_de_Empresa()
    {
        // Corrige la R2 de #1069 («bloquea en TODOS los Centros del Tenant»): el Centro B no lo exige, Ana no esta bloqueada alli.
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroB, Ana, EmpresaX)], [DeEmpresaEn(CentroA, TipoCertificadoSs)]);

        bloqueos.Should().ContainSingle().Which.CentroId.Should().Be(CentroA);
    }

    [Fact]
    public void R2_la_misma_Empresa_con_tolerancia_distinta_en_dos_Centros_esta_bloqueada_en_uno_y_no_en_el_otro()
    {
        // Certificado de la Seguridad Social vencido hace 10 dias: el Centro A da 15 dias de tolerancia, el B ninguna.
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroB, Beto, EmpresaX)],
            [DeEmpresaEn(CentroA, TipoCertificadoSs, toleranciaDias: 15), DeEmpresaEn(CentroB, TipoCertificadoSs, toleranciaDias: 0)],
            DeEmpresa(EmpresaX, TipoCertificadoSs, VigenciaDocumento.VenceEl(Hoy.AddDays(-10))));

        var bloqueo = bloqueos.Should().ContainSingle().Subject;
        bloqueo.CentroId.Should().Be(CentroB);
        bloqueo.TrabajadorId.Should().Be(Beto);
    }

    [Fact]
    public void R2_agotada_la_tolerancia_se_bloquea_a_todos_los_Trabajadores_de_la_Empresa_en_ese_Centro()
    {
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroA, Beto, EmpresaX), new(CentroB, Ana, EmpresaX)],
            [DeEmpresaEn(CentroA, TipoCertificadoSs, toleranciaDias: 15), DeEmpresaEn(CentroB, TipoCertificadoSs, toleranciaDias: 30)],
            DeEmpresa(EmpresaX, TipoCertificadoSs, VigenciaDocumento.VenceEl(Hoy.AddDays(-16))));

        bloqueos.Select(b => (b.CentroId, b.TrabajadorId)).Should().BeEquivalentTo([(CentroA, Ana), (CentroA, Beto)]);
    }

    [Fact]
    public void R2_un_bloqueante_de_Empresa_valido_hoy_no_bloquea_a_nadie()
    {
        Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroB, Beto, EmpresaX)],
            [DeEmpresaEn(CentroA, TipoCertificadoSs), DeEmpresaEn(CentroB, TipoCertificadoSs)],
            DeEmpresa(EmpresaX, TipoCertificadoSs, VigenciaDocumento.VenceEl(Hoy)))
            .Should().BeEmpty();
    }

    [Fact]
    public void R2_el_documento_de_una_Empresa_no_cumple_el_requisito_de_otra()
    {
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroA, Beto, EmpresaY)], [DeEmpresaEn(CentroA, TipoCertificadoSs)],
            DeEmpresa(EmpresaX, TipoCertificadoSs, VigenciaDocumento.NoCaduca));

        var bloqueo = bloqueos.Should().ContainSingle().Subject;
        bloqueo.TrabajadorId.Should().Be(Beto);
        bloqueo.EmpresaId.Should().Be(EmpresaY);
    }

    [Fact]
    public void R2_un_documento_de_Trabajador_no_cumple_un_requisito_de_Empresa_aunque_sea_del_mismo_tipo()
    {
        Calcular(
                [new(CentroA, Ana, EmpresaX)], [DeEmpresaEn(CentroA, TipoCertificadoSs)],
                DeTrabajador(Ana, TipoCertificadoSs, VigenciaDocumento.NoCaduca))
            .Should().ContainSingle().Which.Ambito.Should().Be(AmbitoAplicacion.Empresa);
    }

    [Fact]
    public void R2_un_Trabajador_sin_Empresa_conocida_no_queda_bloqueado_por_un_requisito_de_Empresa()
    {
        Calcular([new(CentroA, Ana, null)], [DeEmpresaEn(CentroA, TipoCertificadoSs)]).Should().BeEmpty();
    }

    [Fact]
    public void R1_y_R2_se_suman_el_Trabajador_aparece_por_cada_requisito_que_le_bloquea()
    {
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX)], [DeTrabajadorEn(CentroA, TipoPss), DeEmpresaEn(CentroA, TipoCertificadoSs)]);

        bloqueos.Select(b => (b.TipoDocumentoId, b.Ambito)).Should().BeEquivalentTo(
        [
            (TipoPss, AmbitoAplicacion.Trabajador),
            (TipoCertificadoSs, AmbitoAplicacion.Empresa)
        ]);
    }

    [Fact]
    public void Un_requisito_de_ambito_sin_sujeto_no_bloquea_a_nadie()
    {
        Calcular(
                [new(CentroA, Ana, EmpresaX)],
                [new RequisitoBloqueanteDelCentro(CentroA, TipoPss, AmbitoAplicacion.Vehiculo, new CondicionesDeAccesoDelCentro(null, 0))])
            .Should().BeEmpty();
    }

    [Fact]
    public void Sin_requisitos_bloqueantes_nadie_queda_bloqueado()
    {
        Calcular([new(CentroA, Ana, EmpresaX)], []).Should().BeEmpty();
    }

    // ---------- Evaluar: el detalle que enseñan las vistas por Trabajador ----------

    [Fact]
    public void Evaluar_devuelve_tambien_lo_que_no_bloquea_y_dice_hasta_cuando_dura_la_tolerancia()
    {
        var evaluados = CalculoBloqueoDeAccesoDeTrabajadores.Evaluar(
            [new(CentroA, Ana, EmpresaX)],
            [DeTrabajadorEn(CentroA, TipoPss, toleranciaDias: 15), DeTrabajadorEn(CentroA, TipoApto)],
            [
                DeTrabajador(Ana, TipoPss, VigenciaDocumento.VenceEl(Hoy.AddDays(-10))),
                DeTrabajador(Ana, TipoApto, VigenciaDocumento.VenceEl(Hoy.AddDays(100)))
            ],
            Hoy);

        evaluados.Should().HaveCount(2);
        var enTolerancia = evaluados.Single(e => e.TipoDocumentoId == TipoPss);
        enTolerancia.Resultado.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Cumplido);
        enTolerancia.Resultado.EnToleranciaHasta.Should().Be(Hoy.AddDays(5));
        enTolerancia.ToleranciaDias.Should().Be(15);
        evaluados.Single(e => e.TipoDocumentoId == TipoApto).Resultado.EnToleranciaHasta.Should().BeNull();
    }
}
