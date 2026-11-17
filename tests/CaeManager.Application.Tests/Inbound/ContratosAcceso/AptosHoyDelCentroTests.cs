using System.Text.Json;
using CaeManager.Application.Inbound.ContratosAcceso.AptosHoy;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Inbound.Requisitos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Domain.Inbound.EvidenciasInbound;
using CaeManager.Domain.Inbound.Requisitos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Inbound.ContratosAcceso;

/// <summary>
/// «Aptos hoy» del Centro (demo Inbound, pieza P3): Apto, EnTolerancia o NoApto en el día de negocio de hoy a partir de las
/// evidencias Inbound del Tenant TCE y de los requisitos que bloquean (plantilla del Centro y exigencias del Cliente
/// empresarial). Cubre la regla de la pieza (D2 i, D3, D4, Q-10), el alcance de la Asignación de Cartera y que no salga ningún
/// dato clínico. Los datos son ficticios: Logística Delta Sur S.A. (Tenant TCE) y Montajes Moncayo S.L. (Tenant TEC).
/// </summary>
public class AptosHoyDelCentroTests
{
    // Miércoles 17/11/2026, 11:00 en Madrid (10:00 UTC).
    private static readonly DateTimeOffset Ahora = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    private readonly Guid _cliente = Guid.NewGuid();
    private readonly Guid _centro = Guid.NewGuid();
    private readonly Guid _tenantContratista = Guid.NewGuid();
    private readonly ContratosAccesoQueryContextFalso _contratos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly PlantillaRequisitosRepositorioFalso _plantillas = new();
    private readonly List<EvidenciaInbound> _evidencias = [];
    private readonly ContratoAcceso _contrato;
    private readonly PlantillaRequisitos _delCentro;
    private readonly TipoDocumento _formacion;
    private readonly TipoDocumento _aptitud;
    private readonly TipoDocumento _seguroRc;
    private readonly TipoDocumento _seguridadSocial;
    private readonly TipoDocumento _hacienda;
    private readonly Guid _usuario = Guid.NewGuid();

    public AptosHoyDelCentroTests()
    {
        _contrato = NuevoContrato(Guid.NewGuid());
        _contratos.ListaContratos.Add(_contrato);

        _formacion = Tipo("Carné de operador de plataforma elevadora", AmbitoAplicacion.Trabajador,
            GrupoTipoDocumento.HabilitacionesYOficios, SensibilidadDocumental.DatosPersonales);
        _aptitud = Tipo("Certificado de aptitud", AmbitoAplicacion.Trabajador, GrupoTipoDocumento.VigilanciaDeLaSalud,
            SensibilidadDocumental.CategoriaEspecialSalud);
        _aptitud.EstablecerSoloConclusionAptitud(true);
        _seguroRc = Tipo("Seguro de responsabilidad civil", AmbitoAplicacion.Empresa, GrupoTipoDocumento.SegurosYMutua,
            SensibilidadDocumental.SinDatosPersonales);
        _seguridadSocial = Tipo("Corriente con la Seguridad Social", AmbitoAplicacion.Empresa,
            GrupoTipoDocumento.SeguridadSocialYHacienda, SensibilidadDocumental.SinDatosPersonales);
        _hacienda = Tipo("Corriente con Hacienda", AmbitoAplicacion.Empresa, GrupoTipoDocumento.SeguridadSocialYHacienda,
            SensibilidadDocumental.SinDatosPersonales);

        _delCentro = PlantillaRequisitos.ParaCentro(_centro);
        Requisito(_delCentro, _formacion, ModoVigenciaRequisito.Fija, 12, tolerancia: 15);
        Requisito(_delCentro, _aptitud, ModoVigenciaRequisito.Fija, 12);
        _plantillas.Plantillas.Add(_delCentro);
    }

    // ---------------------------------------------------------------- Los tres estados y la tolerancia (Q-10)

    [Fact]
    public async Task Un_Trabajador_con_todo_lo_que_bloquea_Aceptado_y_vigente_es_Apto_y_no_cita_ningun_requisito()
    {
        var pilar = Guid.NewGuid();
        Dar(pilar, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(pilar, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.TrabajadorId.Should().Be(pilar);
        fila.Nombre.Should().Be("Pilar Sanjuán Aznar");
        fila.Estado.Should().Be(EstadoAptitudInbound.Apto);
        fila.Requisito.Should().BeNull();
        fila.Causa.Should().BeNull();
        fila.FechaVencimiento.Should().Be(new DateOnly(2027, 6, 1), "el próximo vencimiento de lo que bloquea es el de la formación");
        (fila.ContratoAccesoId, fila.EmpresaContratistaId).Should().Be((_contrato.Id, _contrato.EmpresaContratistaId));
    }

    [Theory]
    [InlineData("2025-11-17", EstadoAptitudInbound.Apto, "vence hoy: todavía vale")]
    [InlineData("2025-11-16", EstadoAptitudInbound.EnTolerancia, "venció ayer: dentro de los 15 días")]
    [InlineData("2025-11-02", EstadoAptitudInbound.EnTolerancia, "venció hace 15 días: el último día de tolerancia")]
    [InlineData("2025-11-01", EstadoAptitudInbound.NoApto, "venció hace 16 días: fuera de tolerancia")]
    public async Task Los_bordes_de_vigencia_y_tolerancia_de_un_requisito_que_bloquea(string emision, EstadoAptitudInbound esperado, string porQue)
    {
        var ivan = Guid.NewGuid();
        Dar(ivan, "Iván Bescós Lacasa", _formacion, DateOnly.Parse(emision));
        Dar(ivan, "Iván Bescós Lacasa", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(esperado, porQue);
        if (esperado == EstadoAptitudInbound.Apto)
            fila.Requisito.Should().BeNull();
        else
        {
            fila.Requisito.Should().Be(_formacion.Nombre);
            fila.SujetoDelRequisito.Should().Be(SujetoDelRequisitoInbound.Trabajador);
            fila.FechaVencimiento.Should().Be(DateOnly.Parse(emision).AddYears(1));
            CausaNoAptitudInbound? causa = esperado == EstadoAptitudInbound.NoApto ? CausaNoAptitudInbound.Vencido : null;
            fila.Causa.Should().Be(causa);
        }
    }

    [Fact]
    public async Task Un_requisito_sin_documento_validado_es_NoApto_con_su_nombre()
    {
        var pilar = Guid.NewGuid();
        Dar(pilar, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        fila.Requisito.Should().Be("Certificado de aptitud");
        fila.Causa.Should().Be(CausaNoAptitudInbound.SinDocumentoValidado);
        fila.FechaVencimiento.Should().BeNull();
    }

    [Fact]
    public async Task Un_NoApto_pesa_mas_que_un_EnTolerancia_y_este_mas_que_un_Apto()
    {
        var pilar = Guid.NewGuid();
        Dar(pilar, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2025, 11, 10)); // vencida hace 7 días: en tolerancia
        // sin certificado de aptitud: NoApto
        var ivan = Guid.NewGuid();
        Dar(ivan, "Iván Bescós Lacasa", _formacion, new DateOnly(2025, 11, 10));
        Dar(ivan, "Iván Bescós Lacasa", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);
        var aldonza = Guid.NewGuid();
        Dar(aldonza, "Aldonza Pardo Ibáñez", _formacion, new DateOnly(2026, 6, 1));
        Dar(aldonza, "Aldonza Pardo Ibáñez", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);

        var filas = await Consultar();

        filas.Single(f => f.TrabajadorId == pilar).Estado.Should().Be(EstadoAptitudInbound.NoApto);
        filas.Single(f => f.TrabajadorId == aldonza).Estado.Should().Be(EstadoAptitudInbound.Apto);
        var enTolerancia = filas.Single(f => f.TrabajadorId == ivan);
        enTolerancia.Estado.Should().Be(EstadoAptitudInbound.EnTolerancia);
        enTolerancia.Requisito.Should().Be(_formacion.Nombre);
        filas.Select(f => f.Estado).Should().BeInDescendingOrder("primero lo que no deja entrar");
    }

    [Fact]
    public async Task Un_requisito_sin_tolerancia_configurada_no_da_ningun_dia_de_margen()
    {
        var plantilla = PlantillaRequisitos.ParaCentro(Guid.NewGuid());
        var otro = ContratoNuevoEnOtroCentro(plantilla);
        Requisito(plantilla, _formacion, ModoVigenciaRequisito.Fija, 12, tolerancia: null);
        var t = Guid.NewGuid();
        Dar(t, "Javier Lasheras Pueyo", _formacion, new DateOnly(2025, 11, 16), contrato: otro); // venció ayer

        var fila = (await Consultar(centro: plantilla.CentroId!.Value)).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        fila.Causa.Should().Be(CausaNoAptitudInbound.Vencido);
    }

    [Fact]
    public async Task La_tolerancia_efectiva_vuelve_a_aplicar_R6_si_el_tipo_cambio_de_grupo_despues_de_fijarla()
    {
        var t = Guid.NewGuid();
        Dar(t, "Iván Bescós Lacasa", _formacion, new DateOnly(2025, 11, 10)); // venció hace 7 días
        Dar(t, "Iván Bescós Lacasa", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);
        (await Consultar()).Single().Estado.Should().Be(EstadoAptitudInbound.EnTolerancia);

        _formacion.EstablecerGrupo(GrupoTipoDocumento.FormacionPrl); // la formación obligatoria nunca tiene tolerancia

        (await Consultar()).Single().Estado.Should().Be(EstadoAptitudInbound.NoApto);
    }

    [Fact]
    public async Task El_dia_de_negocio_es_el_de_Madrid_no_el_UTC()
    {
        var t = Guid.NewGuid();
        Dar(t, "Iván Bescós Lacasa", _formacion, new DateOnly(2025, 11, 17)); // vence el 17/11/2026
        Dar(t, "Iván Bescós Lacasa", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);

        var aLas2330Utc = new DateTimeOffset(2026, 11, 17, 23, 30, 0, TimeSpan.Zero); // ya es 18/11 en Madrid

        (await Consultar(ahora: Ahora)).Single().Estado.Should().Be(EstadoAptitudInbound.Apto);
        (await Consultar(ahora: aLas2330Utc)).Single().Estado.Should().Be(EstadoAptitudInbound.EnTolerancia);
    }

    // ---------------------------------------------------------------- D4: «Fija N meses»

    [Theory]
    [InlineData("2025-12-01", "2026-11-10", "2026-11-10", EstadoAptitudInbound.EnTolerancia, "gana la fecha del documento, que es anterior")]
    [InlineData("2025-12-01", "2027-03-01", "2026-12-01", EstadoAptitudInbound.Apto, "gana la emisión más 12 meses, anterior a la del documento")]
    [InlineData("2025-12-01", null, "2026-12-01", EstadoAptitudInbound.Apto, "sin fecha de documento: la emisión más 12 meses")]
    public async Task Con_vigencia_fija_el_vencimiento_es_la_fecha_mas_temprana(
        string emision, string? fechaDelDocumento, string vencimiento, EstadoAptitudInbound estado, string porQue)
    {
        var t = Guid.NewGuid();
        Dar(t, "Iván Bescós Lacasa", _formacion, DateOnly.Parse(emision), fechaDelDocumento is null ? null : DateOnly.Parse(fechaDelDocumento));
        Dar(t, "Iván Bescós Lacasa", _aptitud, new DateOnly(2026, 11, 1), conclusion: ConclusionAptitud.Apto);

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(estado, porQue);
        fila.FechaVencimiento.Should().Be(DateOnly.Parse(vencimiento), porQue);
    }

    [Fact]
    public async Task Si_el_requisito_toma_el_vencimiento_del_documento_y_este_no_lo_trae_no_se_da_por_vigente()
    {
        var plantilla = PlantillaRequisitos.ParaCentro(Guid.NewGuid());
        var otro = ContratoNuevoEnOtroCentro(plantilla);
        Requisito(plantilla, _formacion, ModoVigenciaRequisito.LaPoneElDocumento, null);
        var t = Guid.NewGuid();
        Dar(t, "Javier Lasheras Pueyo", _formacion, new DateOnly(2026, 6, 1), venc: null, contrato: otro);
        var conFecha = Guid.NewGuid();
        Dar(conFecha, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1), new DateOnly(2027, 1, 1), contrato: otro);

        var filas = await Consultar(centro: plantilla.CentroId!.Value);

        var sinFecha = filas.Single(f => f.TrabajadorId == t);
        sinFecha.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        sinFecha.Causa.Should().Be(CausaNoAptitudInbound.SinFechaDeVencimiento);
        filas.Single(f => f.TrabajadorId == conFecha).Estado.Should().Be(EstadoAptitudInbound.Apto);
    }

    [Fact]
    public async Task Un_requisito_sin_vencimiento_vale_siempre_que_haya_una_evidencia_Aceptada()
    {
        var plantilla = PlantillaRequisitos.ParaCentro(Guid.NewGuid());
        var otro = ContratoNuevoEnOtroCentro(plantilla);
        Requisito(plantilla, _formacion, ModoVigenciaRequisito.SinVencimiento, null);
        var t = Guid.NewGuid();
        Dar(t, "Javier Lasheras Pueyo", _formacion, new DateOnly(2001, 1, 1), contrato: otro);

        var fila = (await Consultar(centro: plantilla.CentroId!.Value)).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.Apto);
        fila.FechaVencimiento.Should().BeNull();
    }

    // ---------------------------------------------------------------- D3: requisitos de la Empresa contratista

    [Fact]
    public async Task Si_falla_un_requisito_de_Empresa_que_bloquea_todos_sus_Trabajadores_son_NoApto()
    {
        ConExigencias();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        foreach (var (id, nombre) in new[] { (a, "Pilar Sanjuán Aznar"), (b, "Iván Bescós Lacasa") })
        {
            Dar(id, nombre, _formacion, new DateOnly(2026, 6, 1));
            Dar(id, nombre, _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);
        }

        DarEmpresa(_seguridadSocial, new DateOnly(2026, 11, 5)); // vigente
        // sin Seguro de responsabilidad civil

        var filas = await Consultar();

        filas.Should().HaveCount(2).And.OnlyContain(f => f.Estado == EstadoAptitudInbound.NoApto);
        filas.Should().OnlyContain(f => f.Requisito == "Seguro de responsabilidad civil"
            && f.SujetoDelRequisito == SujetoDelRequisitoInbound.EmpresaContratista
            && f.Causa == CausaNoAptitudInbound.SinDocumentoValidado);
    }

    [Fact]
    public async Task Con_los_requisitos_de_Empresa_al_dia_cada_Trabajador_se_decide_por_lo_suyo()
    {
        ConExigencias();
        DarEmpresa(_seguroRc, new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));
        DarEmpresa(_seguridadSocial, new DateOnly(2026, 11, 5));
        var apto = Guid.NewGuid();
        Dar(apto, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(apto, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);
        var noApto = Guid.NewGuid();
        Dar(noApto, "Iván Bescós Lacasa", _formacion, new DateOnly(2026, 6, 1));

        var filas = await Consultar();

        filas.Single(f => f.TrabajadorId == apto).Estado.Should().Be(EstadoAptitudInbound.Apto);
        var propio = filas.Single(f => f.TrabajadorId == noApto);
        propio.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        propio.SujetoDelRequisito.Should().Be(SujetoDelRequisitoInbound.Trabajador);
    }

    [Fact]
    public async Task Un_requisito_de_Empresa_en_tolerancia_deja_a_sus_Trabajadores_como_mucho_EnTolerancia()
    {
        ConExigencias(toleranciaSeguridadSocial: 5);
        DarEmpresa(_seguroRc, new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));
        DarEmpresa(_seguridadSocial, new DateOnly(2026, 10, 12)); // Fija 1 mes: venció el 12/11, hace 5 días
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.EnTolerancia);
        fila.Requisito.Should().Be("Corriente con la Seguridad Social");
        fila.SujetoDelRequisito.Should().Be(SujetoDelRequisitoInbound.EmpresaContratista);
        fila.FechaVencimiento.Should().Be(new DateOnly(2026, 11, 12));
    }

    [Fact]
    public async Task Un_requisito_de_Empresa_que_no_bloquea_no_cambia_el_estado()
    {
        ConExigencias();
        DarEmpresa(_seguroRc, new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));
        DarEmpresa(_seguridadSocial, new DateOnly(2026, 11, 5));
        // La corriente con Hacienda no bloquea y no está entregada.
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);

        (await Consultar()).Single().Estado.Should().Be(EstadoAptitudInbound.Apto);
    }

    [Fact]
    public async Task Las_exigencias_de_otro_Cliente_empresarial_no_se_aplican()
    {
        var ajena = PlantillaRequisitos.ParaExigenciasDe(Guid.NewGuid());
        Requisito(ajena, _seguroRc, ModoVigenciaRequisito.LaPoneElDocumento, null);
        _plantillas.Plantillas.Add(ajena);
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);

        (await Consultar()).Single().Estado.Should().Be(EstadoAptitudInbound.Apto);
    }

    // ---------------------------------------------------------------- Qué evidencias y qué requisitos cuentan

    [Fact]
    public async Task Una_evidencia_Rechazada_no_acredita_y_si_se_vuelve_a_validar_si()
    {
        var t = Guid.NewGuid();
        var rechazada = Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);
        rechazada.Rechazar(CausaRechazoAcreditacion.Ilegible, "Escaneo ilegible", _usuario, Ahora.UtcDateTime);

        var fila = (await Consultar()).Should().ContainSingle("quien solo tiene evidencia rechazada sigue saliendo, como NoApto").Subject;
        fila.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        fila.Requisito.Should().Be(_formacion.Nombre);
        fila.Causa.Should().Be(CausaNoAptitudInbound.SinDocumentoValidado);

        rechazada.Validar(_usuario, Ahora.UtcDateTime);

        (await Consultar()).Single().Estado.Should().Be(EstadoAptitudInbound.Apto);
    }

    [Fact]
    public async Task Con_varias_evidencias_del_mismo_tipo_cuenta_la_mejor()
    {
        var t = Guid.NewGuid();
        Dar(t, "Iván Bescós Lacasa", _formacion, new DateOnly(2024, 1, 1));  // vencida hace años
        Dar(t, "Iván Bescós Lacasa", _formacion, new DateOnly(2026, 6, 1));  // vigente
        Dar(t, "Iván Bescós Lacasa", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);

        (await Consultar()).Single().Estado.Should().Be(EstadoAptitudInbound.Apto);
    }

    [Fact]
    public async Task Solo_cuentan_los_requisitos_activos_que_bloquean_de_Trabajador_o_Empresa()
    {
        var plantilla = PlantillaRequisitos.ParaCentro(Guid.NewGuid());
        var otro = ContratoNuevoEnOtroCentro(plantilla);
        var epi = Tipo("Entrega de EPI", AmbitoAplicacion.Trabajador, GrupoTipoDocumento.InformacionYEpi, SensibilidadDocumental.DatosPersonales);
        var ficha = Tipo("Ficha técnica", AmbitoAplicacion.Vehiculo, null, SensibilidadDocumental.SinDatosPersonales);
        var retirado = Tipo("Carné de soldador", AmbitoAplicacion.Trabajador, GrupoTipoDocumento.HabilitacionesYOficios, SensibilidadDocumental.DatosPersonales);
        Requisito(plantilla, epi, ModoVigenciaRequisito.SinVencimiento, null, bloquea: false);
        Requisito(plantilla, ficha, ModoVigenciaRequisito.LaPoneElDocumento, null);
        var carne = Requisito(plantilla, retirado, ModoVigenciaRequisito.LaPoneElDocumento, null);
        plantilla.RetirarRequisito(carne.Id);
        Requisito(plantilla, _formacion, ModoVigenciaRequisito.SinVencimiento, null);
        var t = Guid.NewGuid();
        Dar(t, "Javier Lasheras Pueyo", _formacion, new DateOnly(2026, 6, 1), contrato: otro);

        var fila = (await Consultar(centro: plantilla.CentroId!.Value)).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.Apto, "no bloquea el EPI, ni la ficha de un vehículo, ni un requisito retirado");
    }

    [Fact]
    public async Task Una_plantilla_retirada_no_se_aplica()
    {
        _delCentro.Retirar();
        var t = Guid.NewGuid();
        Dar(t, "Javier Lasheras Pueyo", _formacion, new DateOnly(2020, 1, 1));

        (await Consultar()).Single().Estado.Should().Be(EstadoAptitudInbound.Apto, "sin requisitos que bloqueen, nada bloquea");
    }

    [Fact]
    public async Task Quien_no_tiene_ninguna_evidencia_no_aparece_hueco_declarado_de_D2()
    {
        var conEvidencia = Guid.NewGuid();
        Dar(conEvidencia, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));

        var filas = await Consultar();

        filas.Select(f => f.TrabajadorId).Should().Equal(conEvidencia);
    }

    [Fact]
    public async Task Las_evidencias_de_una_Empresa_no_crean_filas_de_Trabajador()
    {
        ConExigencias();
        DarEmpresa(_seguroRc, new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));

        (await Consultar()).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Trabajador_en_dos_contratos_sale_una_vez_con_el_estado_mas_favorable()
    {
        var segundo = NuevoContrato(Guid.NewGuid());
        _contratos.ListaContratos.Add(segundo);
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1)); // en el primero falta la aptitud
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1), contrato: segundo);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto, contrato: segundo);

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.Apto);
        fila.ContratoAccesoId.Should().Be(segundo.Id);
        fila.EmpresaContratistaId.Should().Be(segundo.EmpresaContratistaId);
    }

    [Fact]
    public async Task Cada_Trabajador_lleva_la_Empresa_contratista_de_su_contrato()
    {
        var segundo = NuevoContrato(Guid.NewGuid());
        _contratos.ListaContratos.Add(segundo);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Dar(a, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(b, "Iván Bescós Lacasa", _formacion, new DateOnly(2026, 6, 1), contrato: segundo);

        var filas = await Consultar();

        filas.Single(f => f.TrabajadorId == a).EmpresaContratistaId.Should().Be(_contrato.EmpresaContratistaId);
        filas.Single(f => f.TrabajadorId == b).EmpresaContratistaId.Should().Be(segundo.EmpresaContratistaId);
    }

    // ---------------------------------------------------------------- Qué contratos cuentan

    [Fact]
    public async Task Solo_cuentan_los_contratos_Activos_vinculados_y_vigentes_hoy_del_Centro()
    {
        var pendiente = ContratoAcceso.Crear(_cliente, _centro, Guid.NewGuid(), new DateOnly(2026, 1, 1), null, null, pendienteDeConfirmar: true);
        var sinVincular = ContratoAcceso.Crear(_cliente, _centro, Guid.NewGuid(), new DateOnly(2026, 1, 1), null, null);
        var suspendido = NuevoContrato(Guid.NewGuid());
        suspendido.Suspender();
        var caducado = ContratoAcceso.Crear(_cliente, _centro, Guid.NewGuid(), new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 30), null);
        caducado.VincularTenantContratista(_tenantContratista);
        var futuro = ContratoAcceso.Crear(_cliente, _centro, Guid.NewGuid(), new DateOnly(2026, 12, 1), null, null);
        futuro.VincularTenantContratista(_tenantContratista);
        var otroCentro = ContratoAcceso.Crear(_cliente, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), null, null);
        otroCentro.VincularTenantContratista(_tenantContratista);
        _contratos.ListaContratos.AddRange([pendiente, sinVincular, suspendido, caducado, futuro, otroCentro]);
        foreach (var contrato in new[] { pendiente, sinVincular, suspendido, caducado, futuro, otroCentro })
            Dar(Guid.NewGuid(), "Trabajador de un contrato que no cuenta", _formacion, new DateOnly(2026, 6, 1), contrato: contrato);
        var cuenta = Guid.NewGuid();
        Dar(cuenta, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));

        var filas = await Consultar();

        filas.Select(f => f.TrabajadorId).Should().Equal(cuenta);
    }

    // ---------------------------------------------------------------- Asignación de Cartera y rol

    [Fact]
    public async Task Un_Gestor_CAE_con_el_Cliente_empresarial_en_su_cartera_ve_los_Aptos_hoy()
    {
        Dar(Guid.NewGuid(), "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [_cliente], centroIdsVisibles: [_centro]);

        (await Consultar(alcance: alcance)).Should().ContainSingle();
    }

    [Fact]
    public async Task Un_Gestor_CAE_fuera_de_la_Asignacion_de_Cartera_del_Cliente_no_ve_el_contrato()
    {
        Dar(Guid.NewGuid(), "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()], centroIdsVisibles: [_centro]);

        (await Consultar(alcance: alcance)).Should().BeEmpty("el contrato no se ve, y tampoco decir que existe");
    }

    [Fact]
    public async Task Un_Centro_fuera_del_alcance_del_actor_da_una_lista_vacia()
    {
        Dar(Guid.NewGuid(), "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, clienteIdsVisibles: [_cliente], centroIdsVisibles: [Guid.NewGuid()]);

        (await Consultar(alcance: alcance)).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_usuario_de_portal_no_ve_los_Aptos_hoy()
    {
        Dar(Guid.NewGuid(), "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));

        (await Consultar(alcance: new AlcanceDatosServiceFalso(ladoDeGestion: false))).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Ningún dato clínico

    [Fact]
    public async Task Una_conclusion_No_apto_transcrita_no_acredita_y_solo_sale_el_nombre_del_requisito()
    {
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.NoApto);

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        fila.Requisito.Should().Be("Certificado de aptitud");
        fila.Causa.Should().Be(CausaNoAptitudInbound.NoAcreditado);
        fila.FechaVencimiento.Should().BeNull("ni siquiera la fecha del certificado");
    }

    [Fact]
    public async Task Una_conclusion_con_restricciones_acredita_y_las_restricciones_no_salen()
    {
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.AptoConRestricciones);

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.Apto);
        JsonSerializer.Serialize(fila).Should().NotContainEquivalentOf("restric").And.NotContainEquivalentOf("conclusion");
    }

    [Fact]
    public async Task El_resultado_no_lleva_motivo_de_rechazo_ni_conclusion_ni_archivo()
    {
        var t = Guid.NewGuid();
        var rechazada = Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.NoApto);
        rechazada.Rechazar(CausaRechazoAcreditacion.DatosErroneos, "Informe médico: hipoacusia severa", _usuario, Ahora.UtcDateTime);
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));

        var json = JsonSerializer.Serialize(await Consultar());

        json.Should().NotContainEquivalentOf("hipoacusia").And.NotContainEquivalentOf("Informe médico");
        typeof(AptoHoyDto).GetProperties().Select(p => p.Name).Should().NotContain(n =>
            n.Contains("Motivo", StringComparison.OrdinalIgnoreCase) || n.Contains("Conclusion", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Archivo", StringComparison.OrdinalIgnoreCase) || n.Contains("Huella", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Salud", StringComparison.OrdinalIgnoreCase) || n.Contains("Medic", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Clinic", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- Soporte

    private ContratoAcceso NuevoContrato(Guid empresa)
    {
        var contrato = ContratoAcceso.Crear(_cliente, _centro, empresa, new DateOnly(2026, 1, 1), null, null);
        contrato.VincularTenantContratista(_tenantContratista);
        return contrato;
    }

    private ContratoAcceso ContratoNuevoEnOtroCentro(PlantillaRequisitos plantillaDelCentro)
    {
        var contrato = ContratoAcceso.Crear(
            _cliente, plantillaDelCentro.CentroId!.Value, Guid.NewGuid(), new DateOnly(2026, 1, 1), null, null);
        contrato.VincularTenantContratista(_tenantContratista);
        _contratos.ListaContratos.Add(contrato);
        _plantillas.Plantillas.Add(plantillaDelCentro);
        return contrato;
    }

    // Cada tipo creado queda en el catálogo del Tenant TCE que lee el handler.
    private TipoDocumento Tipo(string nombre, AmbitoAplicacion ambito, GrupoTipoDocumento? grupo, SensibilidadDocumental sensibilidad)
    {
        var tipo = new TipoDocumento(nombre, 12, aplicaVencimientoAutomatico: true, orden: 1, ambito, sensibilidad: sensibilidad);
        tipo.EstablecerGrupo(grupo);
        _tipos.ListaTiposDocumento.Add(tipo);
        return tipo;
    }

    private RequisitoPlantilla Requisito(
        PlantillaRequisitos plantilla, TipoDocumento tipo, ModoVigenciaRequisito modo, int? meses, bool bloquea = true, int? tolerancia = null) =>
        plantilla.AnadirRequisito(tipo, requisitoBase: null, new AtributosRequisito(
            plantilla.Tipo == TipoPlantillaRequisitos.ExigenciasClienteEmpresarial ? OrigenRequisito.ExigenciaN3 : OrigenRequisito.PropioTenant,
            modo, meses, tolerancia, bloquea, null, DestinatarioRequisito.Todos, TiposContratacion.Todos, [],
            AmbitoDocumentoRequisito.GeneralTec));

    private void ConExigencias(int? toleranciaSeguridadSocial = null)
    {
        var exigencias = PlantillaRequisitos.ParaExigenciasDe(_cliente);
        Requisito(exigencias, _seguroRc, ModoVigenciaRequisito.LaPoneElDocumento, null);
        Requisito(exigencias, _seguridadSocial, ModoVigenciaRequisito.Fija, 1, tolerancia: toleranciaSeguridadSocial);
        Requisito(exigencias, _hacienda, ModoVigenciaRequisito.Fija, 12, bloquea: false);
        _plantillas.Plantillas.Add(exigencias);
    }

    private EvidenciaInbound Dar(
        Guid trabajador, string nombre, TipoDocumento tipo, DateOnly emision, DateOnly? venc = null,
        ConclusionAptitud? conclusion = null, ContratoAcceso? contrato = null) =>
        Registrar(contrato ?? _contrato, tipo, trabajador, nombre, emision, venc, conclusion);

    private EvidenciaInbound DarEmpresa(TipoDocumento tipo, DateOnly emision, DateOnly? venc = null) =>
        Registrar(_contrato, tipo, null, "Montajes Moncayo S.L.", emision, venc, null);

    private EvidenciaInbound Registrar(
        ContratoAcceso contrato, TipoDocumento tipo, Guid? trabajador, string nombre, DateOnly emision, DateOnly? venc, ConclusionAptitud? conclusion)
    {
        var evidencia = EvidenciaInbound.Registrar(
            new EvidenciaInbound.Procedencia(contrato.Id, _tenantContratista, Guid.NewGuid(), Guid.NewGuid()),
            tipo.Id,
            new EvidenciaInbound.Sujeto(trabajador, nombre, trabajador is null ? null : "12345678Z"),
            emision, venc, soloConclusionAptitud: conclusion is not null, conclusion,
            conclusion is null ? new EvidenciaInbound.ArchivoCopiado($"{Guid.NewGuid():N}/copia.pdf", new string('a', 64)) : null,
            _usuario, Ahora.UtcDateTime);
        _evidencias.Add(evidencia);
        return evidencia;
    }

    private async Task<IReadOnlyList<AptoHoyDto>> Consultar(AlcanceDatosServiceFalso? alcance = null, Guid? centro = null, DateTimeOffset? ahora = null)
    {
        using var reloj = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(ahora ?? Ahora));
        var handler = new ObtenerAptosHoyDelCentroQueryHandler(
            _contratos, new EvidenciasQueryContextFalso(_evidencias), new PlantillasRequisitosQueryContextFalso(_plantillas), _tipos,
            alcance ?? new AlcanceDatosServiceFalso());
        return await handler.Handle(new ObtenerAptosHoyDelCentroQuery(centro ?? _centro), CancellationToken.None);
    }

    private sealed class EvidenciasQueryContextFalso(List<EvidenciaInbound> evidencias) : IEvidenciasInboundQueryContext
    {
        public IQueryable<EvidenciaInbound> EvidenciasInbound => new TestAsyncQueryable<EvidenciaInbound>(evidencias.AsQueryable());
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }
}
