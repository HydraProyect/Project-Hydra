using System.Text.Json;
using CaeManager.Application.Inbound.Autorizacion;
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
        Dar(pilar, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        fila.Requisito.Should().Be(_formacion.Nombre);
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

    [Theory]
    [InlineData("2026-11-16", EstadoAptitudInbound.NoApto, "el documento venció ayer y el requisito no tiene tolerancia")]
    [InlineData("2026-11-17", EstadoAptitudInbound.Apto, "el documento vence hoy: todavía vale")]
    public async Task Con_Sin_vencimiento_cuenta_la_fecha_de_vencimiento_que_traiga_el_documento(
        string fechaDelDocumento, EstadoAptitudInbound esperado, string porQue)
    {
        var plantilla = PlantillaRequisitos.ParaCentro(Guid.NewGuid());
        var otro = ContratoNuevoEnOtroCentro(plantilla);
        Requisito(plantilla, _formacion, ModoVigenciaRequisito.SinVencimiento, null);
        var t = Guid.NewGuid();
        Dar(t, "Javier Lasheras Pueyo", _formacion, new DateOnly(2001, 1, 1), DateOnly.Parse(fechaDelDocumento), contrato: otro);

        var fila = (await Consultar(centro: plantilla.CentroId!.Value)).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(esperado, porQue);
        fila.FechaVencimiento.Should().Be(DateOnly.Parse(fechaDelDocumento));
        CausaNoAptitudInbound? causa = esperado == EstadoAptitudInbound.NoApto ? CausaNoAptitudInbound.Vencido : null;
        fila.Causa.Should().Be(causa);
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
    public async Task Un_Trabajador_en_dos_contratos_sale_una_vez_con_el_estado_menos_favorable()
    {
        // Decisión del 2026-11-17 (P3, punto 4): antes tomaba el más favorable.
        var segundo = NuevoContrato(Guid.NewGuid());
        _contratos.ListaContratos.Add(segundo);
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1)); // en el primero falta la aptitud
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1), contrato: segundo);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto, contrato: segundo);

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        fila.ContratoAccesoId.Should().Be(_contrato.Id);
        fila.EmpresaContratistaId.Should().Be(_contrato.EmpresaContratistaId);
        fila.EmpresasContratistaIds.Should().BeEquivalentTo([_contrato.EmpresaContratistaId, segundo.EmpresaContratistaId]);
    }

    [Fact]
    public async Task Un_Trabajador_en_dos_contratos_con_la_Empresa_de_uno_incumpliendo_D3_sale_una_sola_fila_NoApto_por_cualquiera_de_las_dos_Empresas()
    {
        // El contrato de la Empresa que incumple D3 (sin Seguro de responsabilidad civil) y el de otra Empresa al día.
        ConExigencias();
        var alDia = NuevoContrato(Guid.NewGuid());
        _contratos.ListaContratos.Add(alDia);
        DarEmpresa(_seguridadSocial, new DateOnly(2026, 11, 5)); // la de _contrato: sin seguro de RC
        Registrar(alDia, _seguroRc, null, "Montajes Moncayo S.L.", new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1), null);
        Registrar(alDia, _seguridadSocial, null, "Montajes Moncayo S.L.", new DateOnly(2026, 11, 5), null, null);
        var t = Guid.NewGuid();
        foreach (var contrato in new[] { _contrato, alDia })
        {
            Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1), contrato: contrato);
            Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto, contrato: contrato);
        }

        var completa = (await Consultar()).Should().ContainSingle("una fila por Trabajador").Subject;

        completa.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        completa.EmpresaContratistaId.Should().Be(_contrato.EmpresaContratistaId, "la fila cita la Empresa que da el estado");
        completa.SujetoDelRequisito.Should().Be(SujetoDelRequisitoInbound.EmpresaContratista);
        completa.EmpresasContratistaIds.Should().BeEquivalentTo([_contrato.EmpresaContratistaId, alDia.EmpresaContratistaId],
            "el filtro por Empresa lo encuentra por cualquiera de las dos");

        var restringida = (await Consultar(autorizacion: new AutorizacionSolo(CapacidadInbound.LeerInbound))).Should().ContainSingle().Subject;
        restringida.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        (restringida.Requisito, restringida.SujetoDelRequisito, restringida.Causa, restringida.FechaVencimiento)
            .Should().Be(((string?)null, (SujetoDelRequisitoInbound?)null, (CausaNoAptitudInbound?)null, (DateOnly?)null),
                "fuera del Gestor CAE no sale el motivo");
        restringida.EmpresasContratistaIds.Should().BeEquivalentTo([_contrato.EmpresaContratistaId, alDia.EmpresaContratistaId]);
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

    // Decisión del 2026-11-17 (P3, punto 2): un «No apto» vigente bloquea siempre; antes ganaba la mejor evidencia.
    [Fact]
    public async Task Un_apto_medico_antiguo_no_gana_a_un_No_apto_vigente_mas_reciente()
    {
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2025, 12, 1), conclusion: ConclusionAptitud.Apto); // vigente hasta 2026-12-01
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.NoApto);

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        (fila.Requisito, fila.Causa, fila.FechaVencimiento).Should().Be(
            ((string?)"Certificado de aptitud", (CausaNoAptitudInbound?)CausaNoAptitudInbound.NoAcreditado, (DateOnly?)null));
    }

    [Fact]
    public async Task Un_No_apto_vigente_bloquea_aunque_haya_un_apto_posterior_todavia_vigente()
    {
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 6, 1), conclusion: ConclusionAptitud.NoApto);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);

        (await Consultar()).Should().ContainSingle().Which.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        (await Consultar(autorizacion: new AutorizacionSolo(CapacidadInbound.LeerInbound))).Should().ContainSingle()
            .Which.Estado.Should().Be(EstadoAptitudInbound.NoApto);
    }

    [Theory]
    [InlineData("2025-11-17", EstadoAptitudInbound.NoApto, "su último día de vigencia es hoy: todavía bloquea")]
    [InlineData("2025-11-16", EstadoAptitudInbound.Apto, "caducó ayer: deja de bloquear y el apto vigente acredita")]
    public async Task Un_No_apto_caducado_deja_de_bloquear(string emisionDelNoApto, EstadoAptitudInbound esperado, string porQue)
    {
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, DateOnly.Parse(emisionDelNoApto), conclusion: ConclusionAptitud.NoApto);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);

        (await Consultar()).Should().ContainSingle().Which.Estado.Should().Be(esperado, porQue);
    }

    [Fact]
    public async Task Un_No_apto_Rechazado_no_bloquea_y_un_No_apto_caducado_sin_otra_evidencia_sigue_sin_acreditar()
    {
        var rechazado = Guid.NewGuid();
        Dar(rechazado, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(rechazado, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 6, 1), conclusion: ConclusionAptitud.NoApto)
            .Rechazar(CausaRechazoAcreditacion.DatosErroneos, "Mal transcrito", _usuario, Ahora.UtcDateTime);
        Dar(rechazado, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);
        var caducado = Guid.NewGuid();
        Dar(caducado, "Iván Bescós Lacasa", _formacion, new DateOnly(2026, 6, 1));
        Dar(caducado, "Iván Bescós Lacasa", _aptitud, new DateOnly(2024, 1, 1), conclusion: ConclusionAptitud.NoApto);

        var filas = await Consultar();

        filas.Single(f => f.TrabajadorId == rechazado).Estado.Should().Be(EstadoAptitudInbound.Apto);
        var sinAcreditar = filas.Single(f => f.TrabajadorId == caducado);
        sinAcreditar.Estado.Should().Be(EstadoAptitudInbound.NoApto, "un «No apto» caducado no acredita nada");
        sinAcreditar.Causa.Should().Be(CausaNoAptitudInbound.NoAcreditado);
    }

    // ---------------------------------------------------------------- Un apto anterior al «No apto» no revive (P3, revisión, punto 1)

    [Fact]
    public async Task Un_apto_anterior_al_No_apto_no_revive_cuando_el_No_apto_caduca()
    {
        // Fija 12: apto del 2026-03-01 (vence 2027-03-01) y «No apto» del 2026-09-01 cuyo documento vence el 2026-10-01.
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 3, 1), conclusion: ConclusionAptitud.Apto);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), conclusion: ConclusionAptitud.NoApto);

        var completa = (await Consultar()).Should().ContainSingle().Subject;
        var restringida = (await Consultar(autorizacion: new AutorizacionSolo(CapacidadInbound.LeerInbound))).Should().ContainSingle().Subject;

        completa.Estado.Should().Be(EstadoAptitudInbound.NoApto, "el «No apto» caducó el 2026-10-01 pero el apto es anterior: no acredita");
        completa.Requisito.Should().Be(_aptitud.Nombre);
        completa.Causa.Should().Be(CausaNoAptitudInbound.NoAcreditado);
        restringida.Estado.Should().Be(EstadoAptitudInbound.NoApto);
    }

    [Fact]
    public async Task Con_un_apto_posterior_al_No_apto_caducado_el_Trabajador_es_Apto()
    {
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 3, 1), conclusion: ConclusionAptitud.Apto);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), conclusion: ConclusionAptitud.NoApto);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 10, 15), conclusion: ConclusionAptitud.Apto);

        (await Consultar()).Should().ContainSingle().Which.Estado.Should().Be(EstadoAptitudInbound.Apto);
    }

    [Fact]
    public async Task Un_apto_emitido_el_mismo_dia_que_el_No_apto_caducado_no_acredita()
    {
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), conclusion: ConclusionAptitud.NoApto);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);

        (await Consultar()).Should().ContainSingle().Which.Estado.Should().Be(EstadoAptitudInbound.NoApto, "solo acredita uno posterior");
    }

    [Fact]
    public async Task Con_varios_No_apto_caducados_solo_acredita_un_apto_posterior_al_mas_reciente()
    {
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), conclusion: ConclusionAptitud.NoApto);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 5, 1), conclusion: ConclusionAptitud.Apto); // posterior al primero, no al segundo
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), conclusion: ConclusionAptitud.NoApto);

        (await Consultar()).Should().ContainSingle().Which.Estado.Should().Be(EstadoAptitudInbound.NoApto);
    }

    // ---------------------------------------------------------------- El «No apto» se lee de todos los contratos del Centro (P3, revisión, punto 2)

    [Theory]
    [InlineData("suspendido")]
    [InlineData("finalizado")]
    [InlineData("cancelado")]
    [InlineData("no_empezado")]
    public async Task Un_No_apto_de_un_contrato_que_no_cuenta_bloquea_al_Trabajador_en_el_contrato_que_cuenta(string estado)
    {
        var ajeno = ContratoQueNoCuenta(estado);
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 3, 1), conclusion: ConclusionAptitud.Apto); // anterior, en el contrato que cuenta
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.NoApto, contrato: ajeno);

        var fila = (await Consultar()).Should().ContainSingle("el contrato que no cuenta no crea fila").Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        fila.Causa.Should().Be(CausaNoAptitudInbound.NoAcreditado);
        fila.ContratoAccesoId.Should().Be(_contrato.Id);
        fila.EmpresasContratistaIds.Should().BeEquivalentTo([_contrato.EmpresaContratistaId], "la Empresa del contrato excluido no sale");
    }

    [Fact]
    public async Task Un_No_apto_caducado_de_un_contrato_que_no_cuenta_tampoco_deja_revivir_un_apto_anterior()
    {
        var suspendido = ContratoQueNoCuenta("suspendido");
        var anterior = Guid.NewGuid();
        var posterior = Guid.NewGuid();
        foreach (var t in new[] { anterior, posterior })
        {
            Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
            Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), conclusion: ConclusionAptitud.NoApto, contrato: suspendido);
        }

        Dar(anterior, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 3, 1), conclusion: ConclusionAptitud.Apto);
        Dar(posterior, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 10, 15), conclusion: ConclusionAptitud.Apto);

        var filas = await Consultar();

        filas.Single(f => f.TrabajadorId == anterior).Estado.Should().Be(EstadoAptitudInbound.NoApto);
        filas.Single(f => f.TrabajadorId == posterior).Estado.Should().Be(EstadoAptitudInbound.Apto);
    }

    [Fact]
    public async Task Un_apto_de_un_contrato_que_no_cuenta_no_acredita_en_el_que_cuenta()
    {
        var suspendido = ContratoQueNoCuenta("suspendido");
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto, contrato: suspendido);

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.NoApto, "solo el «No apto» cruza contratos, nunca la acreditación positiva");
        fila.Requisito.Should().Be(_aptitud.Nombre);
    }

    [Fact]
    public async Task Un_No_apto_de_un_contrato_que_el_actor_no_ve_no_se_lee()
    {
        var deOtroCliente = ContratoAcceso.Crear(Guid.NewGuid(), _centro, Guid.NewGuid(), new DateOnly(2026, 1, 1), null, null);
        deOtroCliente.VincularTenantContratista(_tenantContratista);
        deOtroCliente.Suspender();
        _contratos.ListaContratos.Add(deOtroCliente);
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 2), conclusion: ConclusionAptitud.NoApto, contrato: deOtroCliente);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [_cliente], centroIdsVisibles: [_centro]);

        (await Consultar(alcance: alcance)).Should().ContainSingle().Which.Estado.Should().Be(
            EstadoAptitudInbound.Apto, "la autorización no cambia: no se leen evidencias de un contrato que la Asignación de Cartera no deja ver");
    }

    [Fact]
    public async Task Un_No_apto_de_un_contrato_que_cuenta_pero_el_actor_no_ve_no_bloquea_ni_suma_su_Empresa()
    {
        // Mismo Centro, Activo, vinculado y vigente (cuenta), pero de otro Cliente empresarial: la Asignación de Cartera no lo deja ver.
        var oculto = NuevoContratoDeOtroCliente();
        _contratos.ListaContratos.Add(oculto);
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 2), conclusion: ConclusionAptitud.NoApto, contrato: oculto);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [_cliente], centroIdsVisibles: [_centro]);

        var fila = (await Consultar(alcance: alcance)).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.Apto, "no se leen evidencias de un contrato que la Asignación de Cartera no deja ver");
        fila.ContratoAccesoId.Should().Be(_contrato.Id);
        fila.EmpresasContratistaIds.Should().Equal(_contrato.EmpresaContratistaId);
    }

    // ---------------------------------------------------------------- «No apto» sin fecha que lo caduque = vigente

    [Theory]
    [InlineData(false, "lo pone el documento y no trae fecha")]
    [InlineData(true, "sin vencimiento y el documento no trae fecha")]
    public async Task Un_No_apto_sin_fecha_que_lo_caduque_se_da_por_vigente_y_bloquea_aunque_haya_un_apto_posterior(bool sinVencimiento, string porQue)
    {
        var plantilla = PlantillaRequisitos.ParaCentro(Guid.NewGuid());
        var otro = ContratoNuevoEnOtroCentro(plantilla);
        Requisito(plantilla, _aptitud, sinVencimiento ? ModoVigenciaRequisito.SinVencimiento : ModoVigenciaRequisito.LaPoneElDocumento, null);
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 6, 1), conclusion: ConclusionAptitud.NoApto, contrato: otro);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), sinVencimiento ? null : new DateOnly(2027, 9, 1),
            conclusion: ConclusionAptitud.Apto, contrato: otro);

        var fila = (await Consultar(centro: plantilla.CentroId!.Value)).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.NoApto, porQue);
        fila.Causa.Should().Be(CausaNoAptitudInbound.NoAcreditado);
    }

    // ---------------------------------------------------------------- Varios contratos: desempate y Empresas (P3, revisión, punto 4)

    [Fact]
    public async Task Con_el_mismo_estado_en_dos_contratos_la_fila_cita_el_mas_reciente()
    {
        var reciente = ContratoDesde(new DateOnly(2026, 6, 1));
        var t = Guid.NewGuid();
        foreach (var contrato in new[] { _contrato, reciente }) // la aptitud falta en los dos: NoApto en los dos
            Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1), contrato: contrato);

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        fila.ContratoAccesoId.Should().Be(reciente.Id, "con el mismo estado se queda el contrato de inicio más reciente");
        fila.EmpresaContratistaId.Should().Be(reciente.EmpresaContratistaId);
    }

    [Fact]
    public async Task EmpresasContratistaIds_no_recoge_la_Empresa_de_un_contrato_que_no_cuenta()
    {
        var excluido = ContratoQueNoCuenta("suspendido");
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1), contrato: excluido);

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.EmpresasContratistaIds.Should().Equal([_contrato.EmpresaContratistaId]);
        fila.EmpresasContratistaIds.Should().NotContain(excluido.EmpresaContratistaId);
    }

    // ---------------------------------------------------------------- Proyección restringida (P3, revisión, punto 5)

    [Fact]
    public async Task La_proyeccion_restringida_cita_el_contrato_y_la_Empresa_del_contrato_mas_reciente_no_los_del_menos_favorable()
    {
        var reciente = ContratoDesde(new DateOnly(2026, 6, 1));
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1)); // el antiguo (_contrato): falta la aptitud, NoApto
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1), contrato: reciente);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto, contrato: reciente);

        var completa = (await Consultar()).Should().ContainSingle().Subject;
        var restringida = (await Consultar(autorizacion: new AutorizacionSolo(CapacidadInbound.LeerInbound))).Should().ContainSingle().Subject;

        completa.ContratoAccesoId.Should().Be(_contrato.Id, "quien valida ve el contrato que da el estado");
        restringida.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        restringida.ContratoAccesoId.Should().Be(reciente.Id, "citar el menos favorable delataría dónde está el motivo");
        restringida.EmpresaContratistaId.Should().Be(reciente.EmpresaContratistaId);
        restringida.EmpresasContratistaIds.Should().BeEquivalentTo([_contrato.EmpresaContratistaId, reciente.EmpresaContratistaId]);
    }

    [Fact]
    public async Task En_la_proyeccion_restringida_el_orden_no_depende_del_contrato_menos_favorable()
    {
        // Dos Trabajadores en el mismo estado restringido: el orden es por nombre, sea cual sea el contrato que da el estado.
        var reciente = ContratoDesde(new DateOnly(2026, 6, 1));
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Dar(a, "Zacarías Bescós Lacasa", _formacion, new DateOnly(2026, 6, 1)); // NoApto por el antiguo
        Dar(a, "Zacarías Bescós Lacasa", _formacion, new DateOnly(2026, 6, 1), contrato: reciente);
        Dar(a, "Zacarías Bescós Lacasa", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto, contrato: reciente);
        Dar(b, "Ana Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1), contrato: reciente); // NoApto por el reciente

        var filas = await Consultar(autorizacion: new AutorizacionSolo(CapacidadInbound.LeerInbound));

        filas.Select(f => f.TrabajadorId).Should().Equal([b, a]);
        filas.Should().OnlyContain(f => f.EmpresaContratistaId == reciente.EmpresaContratistaId);
    }

    [Fact]
    public async Task En_la_proyeccion_restringida_el_nombre_y_el_DNI_parcial_son_los_del_contrato_mas_reciente()
    {
        // El reciente (A) es Apto y trae el DNI; el antiguo (_contrato, B) está en tolerancia y trae el NIE: el estado menos
        // favorable es el de B, pero la fila restringida cita A y muestra la identidad de A, no la de B.
        var a = ContratoDesde(new DateOnly(2026, 6, 1));
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1), contrato: a, documento: "12345678Z");
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto, contrato: a, documento: "12345678Z");
        Dar(t, "P. Sanjuán Aznar (antiguo)", _formacion, new DateOnly(2025, 11, 16), documento: "X1234567L"); // venció ayer: en tolerancia
        Dar(t, "P. Sanjuán Aznar (antiguo)", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto, documento: "X1234567L");

        var completa = (await Consultar()).Should().ContainSingle().Subject;
        var restringida = (await Consultar(autorizacion: new AutorizacionSolo(CapacidadInbound.LeerInbound))).Should().ContainSingle().Subject;

        completa.Estado.Should().Be(EstadoAptitudInbound.EnTolerancia);
        completa.ContratoAccesoId.Should().Be(_contrato.Id, "quien valida ve el contrato que da el estado");
        restringida.Estado.Should().Be(EstadoAptitudInbound.Apto);
        restringida.ContratoAccesoId.Should().Be(a.Id);
        restringida.Nombre.Should().Be("Pilar Sanjuán Aznar");
        restringida.DocumentoIdentidad.Should().Be("****678Z", "la identidad no sale del contrato menos favorable");
    }

    [Fact]
    public async Task Un_NoApto_de_un_contrato_que_cuenta_tambien_evalua_a_los_demas_contratos_y_la_fila_cita_el_que_lo_tiene()
    {
        // A (inicio 2026-06-01, Empresa X): todo al día y el apto vigente. B (_contrato, inicio 2026-01-01, Empresa Y): un
        // «No apto» Aceptado y vigente. A sale NoApto por el «No apto» de B, pero gana B, que lo tiene entre sus propias
        // evidencias: la fila no puede citar a A ni a su Empresa.
        var a = ContratoDesde(new DateOnly(2026, 6, 1));
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1), contrato: a);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto, contrato: a);
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 10, 1), conclusion: ConclusionAptitud.NoApto);

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        fila.ContratoAccesoId.Should().Be(_contrato.Id, "el «No apto» es de B");
        fila.EmpresaContratistaId.Should().Be(_contrato.EmpresaContratistaId);
        fila.Requisito.Should().Be(_aptitud.Nombre);
        fila.EmpresasContratistaIds.Should().BeEquivalentTo([a.EmpresaContratistaId, _contrato.EmpresaContratistaId]);
    }

    [Fact]
    public async Task Un_apto_anterior_a_un_No_apto_de_otro_contrato_que_cuenta_no_revive_y_la_fila_cita_el_contrato_del_No_apto()
    {
        // Fija 12, hoy 2026-11-17. A (reciente, Empresa X): formación al día y un apto del 2026-03-01. B (_contrato, antiguo,
        // Empresa Y): formación al día, un «No apto» del 2026-09-01 que caduca el 2026-10-01 y un apto del 2026-10-15. B, por
        // sí solo, es Apto; A acredita con un apto anterior al «No apto» de B, que no revive: el Trabajador es NoApto.
        var a = ContratoDesde(new DateOnly(2026, 6, 1));
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1), contrato: a);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 3, 1), conclusion: ConclusionAptitud.Apto, contrato: a);
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), conclusion: ConclusionAptitud.NoApto);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 10, 15), conclusion: ConclusionAptitud.Apto);

        var completa = (await Consultar()).Should().ContainSingle().Subject;
        var restringida = (await Consultar(autorizacion: new AutorizacionSolo(CapacidadInbound.LeerInbound))).Should().ContainSingle().Subject;

        completa.Estado.Should().Be(EstadoAptitudInbound.NoApto, "el apto de A es anterior al «No apto» de B, aunque este ya caducó");
        completa.Requisito.Should().Be(_aptitud.Nombre);
        completa.Causa.Should().Be(CausaNoAptitudInbound.NoAcreditado);
        completa.ContratoAccesoId.Should().Be(_contrato.Id, "B tiene el «No apto» que provoca el corte");
        completa.EmpresaContratistaId.Should().Be(_contrato.EmpresaContratistaId);
        completa.EmpresasContratistaIds.Should().BeEquivalentTo([a.EmpresaContratistaId, _contrato.EmpresaContratistaId]);
        restringida.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        restringida.ContratoAccesoId.Should().Be(a.Id, "la proyección restringida cita siempre el contrato más reciente");
        restringida.EmpresaContratistaId.Should().Be(a.EmpresaContratistaId);
    }

    [Fact]
    public async Task Con_el_mismo_estado_gana_la_fila_cuyo_estado_procede_de_sus_propias_evidencias_y_no_de_un_No_apto_ajeno()
    {
        // El «No apto» vigente es de un contrato Suspendido: no se puede citar. A (reciente) sale NoApto solo por él; B
        // (antiguo) también, pero por lo suyo (le falta la formación). Con el mismo estado gana B, no el más reciente.
        var suspendido = ContratoQueNoCuenta("suspendido");
        var a = ContratoDesde(new DateOnly(2026, 6, 1));
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1), contrato: a);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto, contrato: a);
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto); // B: sin formación
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 10, 1), conclusion: ConclusionAptitud.NoApto, contrato: suspendido);

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        fila.ContratoAccesoId.Should().Be(_contrato.Id, "el estado de B procede de lo propio; el de A, del «No apto» ajeno");
        fila.EmpresaContratistaId.Should().Be(_contrato.EmpresaContratistaId);
        fila.EmpresasContratistaIds.Should().BeEquivalentTo([a.EmpresaContratistaId, _contrato.EmpresaContratistaId]);
    }

    [Fact]
    public async Task En_un_requisito_que_revela_salud_cualquier_causa_sale_igual_y_sin_fecha()
    {
        // Cuatro Trabajadores con la formación al día y el certificado de aptitud sin acreditar por cuatro causas distintas.
        var sinDocumento = Guid.NewGuid();
        var vencido = Guid.NewGuid();
        var noApto = Guid.NewGuid();
        var rechazado = Guid.NewGuid();
        foreach (var t in new[] { sinDocumento, vencido, noApto, rechazado })
            Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(vencido, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2025, 9, 1), conclusion: ConclusionAptitud.Apto);
        Dar(noApto, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.NoApto);
        Dar(rechazado, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto)
            .Rechazar(CausaRechazoAcreditacion.DatosErroneos, "No es legible", _usuario, Ahora.UtcDateTime);

        var filas = await Consultar();

        filas.Should().HaveCount(4);
        filas.Select(f => (f.Estado, f.Requisito, f.SujetoDelRequisito, f.Causa, f.FechaVencimiento)).Distinct().Should().ContainSingle()
            .Which.Should().Be((EstadoAptitudInbound.NoApto, (string?)"Certificado de aptitud", (SujetoDelRequisitoInbound?)SujetoDelRequisitoInbound.Trabajador,
                (CausaNoAptitudInbound?)CausaNoAptitudInbound.NoAcreditado, (DateOnly?)null),
                "sin documento, vencido, «No apto» o rechazado no se distinguen: la causa delataría la conclusión médica");
    }

    [Fact]
    public async Task La_marca_de_solo_conclusion_de_la_evidencia_basta_aunque_el_tipo_del_Tenant_TCE_no_revele_salud()
    {
        // El tipo del Tenant TCE está mal clasificado (datos personales, grupo con tolerancia), pero las evidencias llegaron como
        // solo conclusión de aptitud: ni la causa, ni la fecha, ni la tolerancia (R-6) pueden distinguir «vencido» de «No apto».
        var reconocimiento = Tipo("Reconocimiento médico", AmbitoAplicacion.Trabajador, GrupoTipoDocumento.HabilitacionesYOficios,
            SensibilidadDocumental.DatosPersonales);
        var plantilla = PlantillaRequisitos.ParaCentro(Guid.NewGuid());
        var otro = ContratoNuevoEnOtroCentro(plantilla);
        Requisito(plantilla, reconocimiento, ModoVigenciaRequisito.Fija, 12, tolerancia: 15);
        var vencidoAyer = Guid.NewGuid();
        var vencidoHaceUnMes = Guid.NewGuid();
        var noApto = Guid.NewGuid();
        Dar(vencidoAyer, "Pilar Sanjuán Aznar", reconocimiento, new DateOnly(2025, 11, 16), conclusion: ConclusionAptitud.Apto, contrato: otro);
        Dar(vencidoHaceUnMes, "Iván Bescós Lacasa", reconocimiento, new DateOnly(2025, 10, 17), conclusion: ConclusionAptitud.Apto, contrato: otro);
        Dar(noApto, "Javier Lasheras Pueyo", reconocimiento, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.NoApto, contrato: otro);

        var filas = await Consultar(centro: plantilla.CentroId!.Value);

        filas.Should().HaveCount(3);
        filas.Select(f => (f.Estado, f.Causa, f.FechaVencimiento)).Distinct().Should().ContainSingle()
            .Which.Should().Be((EstadoAptitudInbound.NoApto, (CausaNoAptitudInbound?)CausaNoAptitudInbound.NoAcreditado, (DateOnly?)null));
    }

    [Fact]
    public async Task Un_requisito_cuyo_tipo_no_se_encuentra_se_trata_como_si_revelara_salud()
    {
        var huerfano = new TipoDocumento("Tipo retirado", 12, aplicaVencimientoAutomatico: true, orden: 1, AmbitoAplicacion.Trabajador,
            sensibilidad: SensibilidadDocumental.SinDatosPersonales);
        var plantilla = PlantillaRequisitos.ParaCentro(Guid.NewGuid());
        var otro = ContratoNuevoEnOtroCentro(plantilla);
        Requisito(plantilla, huerfano, ModoVigenciaRequisito.Fija, 12);
        Dar(Guid.NewGuid(), "Pilar Sanjuán Aznar", huerfano, new DateOnly(2025, 1, 1), contrato: otro);

        var fila = (await Consultar(centro: plantilla.CentroId!.Value)).Should().ContainSingle().Subject;

        fila.Requisito.Should().Be("Requisito sin nombre");
        fila.Causa.Should().Be(CausaNoAptitudInbound.NoAcreditado, "falla cerrado: sin el tipo no se sabe si revela salud");
        fila.FechaVencimiento.Should().BeNull();
    }

    [Fact]
    public async Task Un_requisito_que_no_revela_salud_sigue_diciendo_su_causa()
    {
        var t = Guid.NewGuid();
        Dar(t, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2025, 1, 1));
        Dar(t, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);

        var fila = (await Consultar()).Should().ContainSingle().Subject;

        fila.Causa.Should().Be(CausaNoAptitudInbound.Vencido);
        fila.FechaVencimiento.Should().Be(new DateOnly(2026, 1, 1));
    }

    // ---------------------------------------------------------------- Proyección completa y restringida (duda 6)

    [Fact]
    public async Task Con_ValidarInbound_el_Gestor_CAE_recibe_la_proyeccion_completa()
    {
        var t = Guid.NewGuid();
        Dar(t, "Iván Bescós Lacasa", _formacion, new DateOnly(2025, 11, 10));
        Dar(t, "Iván Bescós Lacasa", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);

        var fila = (await Consultar(autorizacion: new AutorizacionSolo(CapacidadInbound.LeerInbound, CapacidadInbound.ValidarInbound)))
            .Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoAptitudInbound.EnTolerancia);
        fila.Requisito.Should().Be(_formacion.Nombre);
        fila.SujetoDelRequisito.Should().Be(SujetoDelRequisitoInbound.Trabajador);
        fila.FechaVencimiento.Should().Be(new DateOnly(2026, 11, 10));
        fila.DocumentoIdentidad.Should().Be("12345678Z", "la pantalla lo enmascara para todos");
        fila.ProyeccionCompleta.Should().BeTrue();
        fila.TipoContrato.Should().Be(_contrato.Tipo);
    }

    [Fact]
    public async Task Sin_ValidarInbound_solo_sale_nombre_DNI_parcial_Empresa_y_apto_o_no_apto()
    {
        // El de la tolerancia va después del apto por nombre: si se ordenara antes de restringir, saldría antes que él.
        var tolerancia = Guid.NewGuid();
        Dar(tolerancia, "Sonia Ferrer Lahoz", _formacion, new DateOnly(2025, 11, 10));
        Dar(tolerancia, "Sonia Ferrer Lahoz", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);
        var noApto = Guid.NewGuid();
        Dar(noApto, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        Dar(noApto, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.NoApto);
        var apto = Guid.NewGuid();
        Dar(apto, "Javier Lasheras Pueyo", _formacion, new DateOnly(2026, 6, 1));
        Dar(apto, "Javier Lasheras Pueyo", _aptitud, new DateOnly(2026, 9, 1), conclusion: ConclusionAptitud.Apto);

        var filas = await Consultar(autorizacion: new AutorizacionSolo(CapacidadInbound.LeerInbound));

        filas.Should().HaveCount(3);
        filas.Should().OnlyContain(f => f.Requisito == null && f.SujetoDelRequisito == null && f.Causa == null && f.FechaVencimiento == null);
        filas.Should().OnlyContain(f => f.TipoContrato == null && !f.ProyeccionCompleta);
        filas.Should().OnlyContain(f => f.DocumentoIdentidad == "****678Z");
        filas.Should().OnlyContain(f => f.EmpresaContratistaId == _contrato.EmpresaContratistaId);
        filas.Single(f => f.TrabajadorId == noApto).Estado.Should().Be(EstadoAptitudInbound.NoApto);
        filas.Single(f => f.TrabajadorId == tolerancia).Estado.Should().Be(EstadoAptitudInbound.Apto, "en tolerancia cuenta como apto (Q-10)");
        filas.Single(f => f.TrabajadorId == apto).Estado.Should().Be(EstadoAptitudInbound.Apto);
        filas.Select(f => f.TrabajadorId).Should().Equal([noApto, apto, tolerancia], "el orden tampoco delata el «en tolerancia»");
    }

    [Fact]
    public async Task Sin_LeerInbound_no_sale_nada_aunque_tenga_ValidarInbound()
    {
        Dar(Guid.NewGuid(), "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));

        (await Consultar(autorizacion: new AutorizacionSolo(CapacidadInbound.ValidarInbound))).Should().BeEmpty();
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

    private ContratoAcceso NuevoContratoDeOtroCliente()
    {
        var contrato = ContratoAcceso.Crear(Guid.NewGuid(), _centro, Guid.NewGuid(), new DateOnly(2026, 1, 1), null, null);
        contrato.VincularTenantContratista(_tenantContratista);
        return contrato;
    }

    private ContratoAcceso ContratoDesde(DateOnly inicio)
    {
        var contrato = ContratoAcceso.Crear(_cliente, _centro, Guid.NewGuid(), inicio, null, null);
        contrato.VincularTenantContratista(_tenantContratista);
        _contratos.ListaContratos.Add(contrato);
        return contrato;
    }

    // Un contrato del Centro del mismo Cliente empresarial que no cuenta hoy en «Aptos hoy», por su estado o por su vigencia.
    private ContratoAcceso ContratoQueNoCuenta(string estado)
    {
        var inicio = estado is "cancelado" or "no_empezado" ? new DateOnly(2026, 12, 1) : new DateOnly(2026, 1, 1);
        var contrato = ContratoDesde(inicio);
        switch (estado)
        {
            case "suspendido": contrato.Suspender(); break;
            case "finalizado": contrato.Finalizar(new DateOnly(2026, 6, 30)); break;
            case "cancelado": contrato.Cancelar(new DateOnly(2026, 11, 17), tieneActividad: false); break;
            case "no_empezado": break;
            default: throw new ArgumentOutOfRangeException(nameof(estado));
        }

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
        ConclusionAptitud? conclusion = null, ContratoAcceso? contrato = null, string? documento = null) =>
        Registrar(contrato ?? _contrato, tipo, trabajador, nombre, emision, venc, conclusion, documento);

    private EvidenciaInbound DarEmpresa(TipoDocumento tipo, DateOnly emision, DateOnly? venc = null) =>
        Registrar(_contrato, tipo, null, "Montajes Moncayo S.L.", emision, venc, null);

    private EvidenciaInbound Registrar(
        ContratoAcceso contrato, TipoDocumento tipo, Guid? trabajador, string nombre, DateOnly emision, DateOnly? venc, ConclusionAptitud? conclusion,
        string? documento = null)
    {
        var evidencia = EvidenciaInbound.Registrar(
            new EvidenciaInbound.Procedencia(contrato.Id, _tenantContratista, Guid.NewGuid(), Guid.NewGuid()),
            tipo.Id,
            new EvidenciaInbound.Sujeto(trabajador, nombre, trabajador is null ? null : documento ?? "12345678Z"),
            emision, venc, soloConclusionAptitud: conclusion is not null, conclusion,
            conclusion is null ? new EvidenciaInbound.ArchivoCopiado($"{Guid.NewGuid():N}/copia.pdf", new string('a', 64)) : null,
            _usuario, Ahora.UtcDateTime);
        _evidencias.Add(evidencia);
        return evidencia;
    }

    private async Task<IReadOnlyList<AptoHoyDto>> Consultar(
        AlcanceDatosServiceFalso? alcance = null, Guid? centro = null, DateTimeOffset? ahora = null, IAutorizacionInbound? autorizacion = null)
    {
        using var reloj = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(ahora ?? Ahora));
        var handler = new ObtenerAptosHoyDelCentroQueryHandler(
            _contratos, new EvidenciasQueryContextFalso(_evidencias), new PlantillasRequisitosQueryContextFalso(_plantillas), _tipos,
            alcance ?? new AlcanceDatosServiceFalso(), autorizacion ?? new AutorizacionInboundQuePermite());
        return await handler.Handle(new ObtenerAptosHoyDelCentroQuery(centro ?? _centro), CancellationToken.None);
    }

    private sealed class AutorizacionSolo(params CapacidadInbound[] capacidades) : IAutorizacionInbound
    {
        public Task<bool> TieneAsync(CapacidadInbound capacidad, CancellationToken cancellationToken) =>
            Task.FromResult(capacidades.Contains(capacidad));
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
