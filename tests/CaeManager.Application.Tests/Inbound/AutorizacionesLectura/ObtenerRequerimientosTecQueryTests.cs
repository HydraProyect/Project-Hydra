using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Inbound.AutorizacionesLectura.Queries.ObtenerRequerimientosTec;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Inbound.AutorizacionesLectura;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Inbound.AutorizacionesLectura;

/// <summary>
/// Requerimientos del Tenant TEC (demo Inbound, P1): requisito de la lectura inversa X-2 × sujeto asignado × documento
/// vigente propio. Lo que de verdad aísla el Tenant TCE es la función <c>app_inbound_tec_requerimientos()</c> bajo
/// <c>cae_app_runtime</c> (integración, solo en CI); aquí se prueba el cruce que hace el handler con los datos propios.
/// </summary>
public class ObtenerRequerimientosTecQueryTests
{
    private const string Administrador = "Administrador";

    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();
    private readonly Guid _contrato = Guid.NewGuid();
    private readonly AutorizacionLecturaRepositorioFalso _autorizaciones = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly ConfiguracionQueryContextFalso _configuracion = new();
    private readonly List<RequisitoTecDto> _requisitos = [];

    private readonly Empresa _propia = new("Montajes Moncayo S.L.");
    private readonly Trabajador _asignado;
    private readonly Trabajador _noAsignado;
    private readonly TipoDocumento _formacion = new("Formación en altura", null, false, 1, AmbitoAplicacion.Trabajador,
        sensibilidad: SensibilidadDocumental.DatosPersonales);
    private readonly TipoDocumento _seguro = new("Seguro de responsabilidad civil", null, false, 2, AmbitoAplicacion.Empresa,
        sensibilidad: SensibilidadDocumental.SinDatosPersonales);

    public ObtenerRequerimientosTecQueryTests()
    {
        _asignado = Trabajador.DeEmpresa(_propia.Id, "Luis", "Gómez Ara", "12345678Z");
        _noAsignado = Trabajador.DeEmpresa(_propia.Id, "Ana", "Pérez", "00000000T");
        _empresas.ListaEmpresas.Add(_propia);
        _trabajadores.ListaTrabajadores.AddRange([_asignado, _noAsignado]);
        _tipos.ListaTiposDocumento.AddRange([_formacion, _seguro]);
        _configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));
    }

    private sealed class LectorFalso(IReadOnlyList<RequisitoTecDto> filas) : ILectorRequerimientosTec
    {
        public int Llamadas { get; private set; }

        public Task<IReadOnlyList<RequisitoTecDto>> RequisitosAsync(CancellationToken cancellationToken = default)
        {
            Llamadas++;
            return Task.FromResult(filas);
        }
    }

    private sealed class PuertaQueNiega : IAutorizacionInbound
    {
        public Task<bool> TieneAsync(CapacidadInbound capacidad, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private RequisitoTecDto Requisito(
        string tipo, string entidad = "Trabajador", Guid? contrato = null, string ambito = "GeneralTec", string capa = "DeCentro",
        TiposContratacion contrataciones = TiposContratacion.Todos, string sePideA = "Todos") =>
        new(contrato ?? _contrato, "Logística Delta Sur", Guid.NewGuid(), "Planta Zaragoza Norte", "Logística Delta Sur S.A.",
            Guid.NewGuid(), Guid.NewGuid(), tipo, entidad, "Certificado vigente", "Fija", 12, 0, true, ambito, capa, sePideA,
            (int)contrataciones);

    private AutorizacionLecturaDocumental Autorizar(
        Guid? contrato = null, bool incluyeEmpresa = false, bool revocada = false, DateOnly? desde = null, params Trabajador[] trabajadores)
    {
        var autorizacion = AutorizacionLecturaDocumental.Conceder(
            Guid.NewGuid(), contrato ?? _contrato, incluyeEmpresa, desde ?? _hoy.AddDays(-30), null, Guid.NewGuid(), DateTime.UtcNow);
        _autorizaciones.Agregar(autorizacion);
        foreach (var t in trabajadores)
            _autorizaciones.AgregarTrabajador(autorizacion.AsignarTrabajador(t.Id));
        if (revocada)
            autorizacion.Revocar(Guid.NewGuid(), DateTime.UtcNow);
        return autorizacion;
    }

    private Documento DocumentoDe(Trabajador trabajador, TipoDocumento tipo, VigenciaDocumento vigencia)
    {
        var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, _hoy.AddMonths(-2), vigencia);
        _documentos.ListaDocumentos.Add(documento);
        return documento;
    }

    private ObtenerRequerimientosTecQueryHandler Handler(
        LectorFalso? lector = null, string? rol = Administrador, IAutorizacionInbound? puerta = null) =>
        new(lector ?? new LectorFalso(_requisitos), new AutorizacionesLecturaQueryContextFalso(_autorizaciones), _trabajadores, _empresas,
            _tipos, _documentos, _configuracion, new CurrentUserServiceFalso(Guid.NewGuid(), rol),
            puerta ?? new AutorizacionInboundQuePermite());

    private Task<IReadOnlyList<RequerimientoTecDto>> Leer(ObtenerRequerimientosTecQueryHandler? handler = null) =>
        (handler ?? Handler()).Handle(new ObtenerRequerimientosTecQuery(), CancellationToken.None);

    [Fact]
    public async Task Cruza_cada_requisito_con_cada_Trabajador_asignado_y_su_documento_vigente_propio()
    {
        _requisitos.Add(Requisito("Formación en altura"));
        var otro = Trabajador.DeEmpresa(_propia.Id, "Marta", "Ruiz", "11111111H");
        _trabajadores.ListaTrabajadores.Add(otro);
        Autorizar(trabajadores: [_asignado, otro]);
        DocumentoDe(_asignado, _formacion, VigenciaDocumento.VenceEl(_hoy.AddMonths(6)));

        var filas = await Leer();

        filas.Should().HaveCount(2);
        var conDocumento = filas.Single(f => f.SujetoId == _asignado.Id);
        conDocumento.Estado.Should().Be(EstadoRequerimientoTecSinVeredicto.PendienteDeValidar);
        conDocumento.EstadoDocumento.Should().Be(EstadoDocumento.Vigente);
        conDocumento.DocumentoVencimiento.Should().Be(_hoy.AddMonths(6));
        conDocumento.DocumentosVigentes.Should().Be(1);
        conDocumento.Sujeto.Should().Be(SujetoRequerimientoTec.Trabajador);
        conDocumento.SujetoNombre.Should().Be("Luis Gómez Ara");
        conDocumento.EmpresaNombre.Should().Be("Montajes Moncayo S.L.");
        conDocumento.TipoDocumentoNombre.Should().Be("Formación en altura");
        conDocumento.ClienteEmpresarialNombre.Should().Be("Logística Delta Sur S.A.");
        conDocumento.ContratoAccesoId.Should().Be(_contrato);

        var sinDocumento = filas.Single(f => f.SujetoId == otro.Id);
        sinDocumento.Estado.Should().Be(EstadoRequerimientoTecSinVeredicto.PendienteDeSubir);
        sinDocumento.EstadoDocumento.Should().BeNull();
        sinDocumento.DocumentosVigentes.Should().Be(0);
    }

    [Fact]
    public async Task Un_Trabajador_no_asignado_no_aparece()
    {
        _requisitos.Add(Requisito("Formación en altura"));
        Autorizar(trabajadores: [_asignado]);
        DocumentoDe(_noAsignado, _formacion, VigenciaDocumento.NoCaduca);

        var filas = await Leer();

        filas.Select(f => f.SujetoId).Should().Equal(_asignado.Id);
    }

    [Fact]
    public async Task Un_requisito_solo_de_Extranjero_no_se_pide_al_Trabajador_por_cuenta_ajena()
    {
        // Máscara 8: «Permiso de trabajo y residencia» bloqueante solo para el Trabajador extranjero (#2338).
        _requisitos.Add(Requisito("Permiso de trabajo y residencia", contrataciones: TiposContratacion.Extranjero));
        _asignado.TipoContratacion.Should().Be(TipoContratacion.CuentaAjena);
        Autorizar(trabajadores: [_asignado]);

        (await Leer()).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_requisito_solo_de_Autonomo_solo_se_pide_al_Trabajador_autonomo()
    {
        // Máscara 2: solo Autónomo. El Trabajador por cuenta ajena del mismo contrato no lo recibe.
        _requisitos.Add(Requisito("Formación en altura", contrataciones: TiposContratacion.Autonomo));
        var autonomo = Trabajador.DeEmpresa(_propia.Id, "Marta", "Ruiz", "11111111H");
        autonomo.CambiarTipoContratacion(TipoContratacion.Autonomo);
        _trabajadores.ListaTrabajadores.Add(autonomo);
        Autorizar(trabajadores: [_asignado, autonomo]);

        var filas = await Leer();

        filas.Select(f => f.SujetoId).Should().Equal(autonomo.Id);
    }

    [Fact]
    public async Task Un_requisito_de_Extranjero_se_pide_al_Trabajador_extranjero_con_su_estado()
    {
        _requisitos.Add(Requisito("Permiso de trabajo y residencia", contrataciones: TiposContratacion.Extranjero));
        var extranjero = Trabajador.DeEmpresa(_propia.Id, "Marta", "Ruiz", "11111111H");
        extranjero.CambiarTipoContratacion(TipoContratacion.Extranjero);
        _trabajadores.ListaTrabajadores.Add(extranjero);
        Autorizar(trabajadores: [_asignado, extranjero]);

        var fila = (await Leer()).Should().ContainSingle().Subject;

        fila.SujetoId.Should().Be(extranjero.Id);
        fila.BloqueaAcceso.Should().BeTrue();
        fila.Estado.Should().Be(EstadoRequerimientoTecSinVeredicto.PendienteDeSubir);
    }

    [Fact]
    public async Task Una_mascara_sin_el_bit_Extranjero_no_se_pide_al_Trabajador_extranjero()
    {
        // 7 es lo que la migración TraduceExtranjeroAModeloDeContratacion deja en un requisito de Trabajador «solo a
        // nacionales»: cuenta ajena, autónomo y voluntario, sin Extranjero.
        _requisitos.Add(Requisito("Formación en altura",
            contrataciones: TiposContratacion.CuentaAjena | TiposContratacion.Autonomo | TiposContratacion.Voluntario,
            sePideA: "Nacionales"));
        var extranjero = Trabajador.DeEmpresa(_propia.Id, "Marta", "Ruiz", "11111111H");
        extranjero.CambiarTipoContratacion(TipoContratacion.Extranjero);
        _trabajadores.ListaTrabajadores.Add(extranjero);
        Autorizar(trabajadores: [_asignado, extranjero]);

        var filas = await Leer();

        filas.Select(f => f.SujetoId).Should().Equal(_asignado.Id);
    }

    [Theory]
    [InlineData("Nacionales")]
    [InlineData("Extranjeros")]
    public async Task SePideA_no_se_lee_en_el_Trabajador_cuando_contradice_la_mascara(string sePideA)
    {
        // La máscara 15 (Todos) incluye al Trabajador por cuenta ajena y al extranjero. Un «Se pide a» guardado como
        // «Nacionales» o «Extranjeros» (por ejemplo, tras añadir el bit Extranjero desde la pantalla) no recorta a nadie:
        // en el Trabajador manda solo la máscara (#2338).
        _requisitos.Add(Requisito("Formación en altura", contrataciones: TiposContratacion.Todos, sePideA: sePideA));
        var extranjero = Trabajador.DeEmpresa(_propia.Id, "Marta", "Ruiz", "11111111H");
        extranjero.CambiarTipoContratacion(TipoContratacion.Extranjero);
        _trabajadores.ListaTrabajadores.Add(extranjero);
        Autorizar(trabajadores: [_asignado, extranjero]);

        var filas = await Leer();

        filas.Select(f => f.SujetoId).Should().BeEquivalentTo([_asignado.Id, extranjero.Id]);
    }

    [Fact]
    public async Task SePideA_todavia_no_filtra_los_requisitos_de_Empresa()
    {
        // shortcut: Empresa no guarda país, así que «Se pide a» no se evalúa en la Empresa propia y el requisito sale siempre.
        // Si se decide ocultarlo, este test cambia con la decisión.
        _requisitos.Add(Requisito("Seguro de responsabilidad civil", entidad: "Empresa", sePideA: "Extranjeros"));
        Autorizar(incluyeEmpresa: true);

        (await Leer()).Should().ContainSingle().Which.Sujeto.Should().Be(SujetoRequerimientoTec.Empresa);
    }

    [Fact]
    public async Task La_mascara_de_contratacion_no_filtra_los_requisitos_de_Empresa()
    {
        // La máscara solo acota requisitos de Trabajador: aunque la de un requisito de Empresa no incluya ningún tipo, la
        // Empresa propia lo sigue recibiendo.
        _requisitos.Add(Requisito("Seguro de responsabilidad civil", entidad: "Empresa", contrataciones: TiposContratacion.Ninguno));
        Autorizar(incluyeEmpresa: true, trabajadores: [_asignado]);

        var fila = (await Leer()).Should().ContainSingle().Subject;

        fila.Sujeto.Should().Be(SujetoRequerimientoTec.Empresa);
    }

    [Fact]
    public async Task Un_Trabajador_asignado_a_otro_contrato_no_aparece_en_este()
    {
        var otroContrato = Guid.NewGuid();
        _requisitos.AddRange([Requisito("Formación en altura"), Requisito("Formación en altura", contrato: otroContrato)]);
        Autorizar(trabajadores: [_asignado]);
        Autorizar(contrato: otroContrato, trabajadores: [_noAsignado]);

        var filas = await Leer();

        filas.Should().HaveCount(2);
        filas.Single(f => f.ContratoAccesoId == _contrato).SujetoId.Should().Be(_asignado.Id);
        filas.Single(f => f.ContratoAccesoId == otroContrato).SujetoId.Should().Be(_noAsignado.Id);
    }

    [Fact]
    public async Task Un_contrato_sin_autorizacion_de_este_Tenant_TEC_no_da_filas()
    {
        // Lo que vería otro Tenant TEC: requisitos de un contrato al que este Tenant no ha concedido nada. En la base, además,
        // la función solo devuelve los contratos vinculados al Tenant del contexto firmado.
        _requisitos.Add(Requisito("Formación en altura", contrato: Guid.NewGuid()));
        Autorizar(trabajadores: [_asignado]);

        (await Leer()).Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_requisitos_de_X2_no_hay_filas_aunque_haya_autorizaciones()
    {
        Autorizar(incluyeEmpresa: true, trabajadores: [_asignado]);

        (await Leer()).Should().BeEmpty();
    }

    [Fact]
    public async Task Una_autorizacion_revocada_o_que_aun_no_ha_empezado_no_asigna_a_nadie()
    {
        _requisitos.Add(Requisito("Formación en altura"));
        Autorizar(revocada: true, trabajadores: [_asignado]);
        Autorizar(desde: _hoy.AddDays(5), trabajadores: [_noAsignado]);

        (await Leer()).Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_ParticipanteTec_no_hay_nada_y_no_se_llama_a_X2()
    {
        _requisitos.Add(Requisito("Formación en altura"));
        Autorizar(trabajadores: [_asignado]);
        var lector = new LectorFalso(_requisitos);

        (await Leer(Handler(lector, puerta: new PuertaQueNiega()))).Should().BeEmpty();
        lector.Llamadas.Should().Be(0);
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData(null)]
    public async Task Otro_rol_no_lee_nada(string? rol)
    {
        _requisitos.Add(Requisito("Formación en altura"));
        Autorizar(trabajadores: [_asignado]);
        var lector = new LectorFalso(_requisitos);

        (await Leer(Handler(lector, rol: rol))).Should().BeEmpty();
        lector.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task Un_Trabajador_eliminado_no_aparece()
    {
        _requisitos.Add(Requisito("Formación en altura"));
        Autorizar(trabajadores: [_asignado, _noAsignado]);
        _asignado.MarcarComoEliminado(Guid.NewGuid());

        (await Leer()).Select(f => f.SujetoId).Should().Equal(_noAsignado.Id);
    }

    [Fact]
    public async Task Un_Trabajador_anonimizado_no_aparece()
    {
        _requisitos.Add(Requisito("Formación en altura"));
        Autorizar(trabajadores: [_asignado, _noAsignado]);
        _asignado.Anonimizar(DateTime.UtcNow);

        (await Leer()).Select(f => f.SujetoId).Should().Equal(_noAsignado.Id);
    }

    [Fact]
    public async Task Cada_requisito_cuenta_solo_el_documento_de_su_tipo()
    {
        var alta = new TipoDocumento("Alta en la Seguridad Social", null, false, 3, AmbitoAplicacion.Trabajador,
            sensibilidad: SensibilidadDocumental.DatosPersonales);
        _tipos.ListaTiposDocumento.Add(alta);
        _requisitos.AddRange([Requisito("Formación en altura"), Requisito("Alta en la Seguridad Social")]);
        Autorizar(trabajadores: [_asignado]);
        DocumentoDe(_asignado, _formacion, VigenciaDocumento.NoCaduca);

        var filas = await Leer();

        filas.Should().HaveCount(2);
        filas.Single(f => f.TipoDocumentoNombre == "Formación en altura").Estado.Should().Be(EstadoRequerimientoTecSinVeredicto.PendienteDeValidar);
        var sinDocumento = filas.Single(f => f.TipoDocumentoNombre == "Alta en la Seguridad Social");
        sinDocumento.Estado.Should().Be(EstadoRequerimientoTecSinVeredicto.PendienteDeSubir,
            "D1: el documento de otro tipo del mismo Trabajador no cubre este requisito");
        sinDocumento.DocumentosVigentes.Should().Be(0);
    }

    [Fact]
    public async Task Un_documento_vencido_eliminado_o_sustituido_no_cuenta_como_vigente()
    {
        _requisitos.Add(Requisito("Formación en altura"));
        Autorizar(trabajadores: [_asignado]);
        DocumentoDe(_asignado, _formacion, VigenciaDocumento.VenceEl(_hoy.AddDays(-1)));
        DocumentoDe(_asignado, _formacion, VigenciaDocumento.NoCaduca).MarcarComoEliminado(Guid.NewGuid());
        var antiguo = DocumentoDe(_asignado, _formacion, VigenciaDocumento.NoCaduca);
        var nuevo = Documento.DeTrabajador(_asignado.Id, _formacion.Id, _hoy.AddDays(-30), VigenciaDocumento.VenceEl(_hoy.AddDays(-2)));
        antiguo.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);

        var fila = (await Leer()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoRequerimientoTecSinVeredicto.PendienteDeSubir);
        fila.DocumentosVigentes.Should().Be(0);
    }

    [Fact]
    public async Task El_documento_se_empareja_por_el_nombre_exacto_del_catalogo()
    {
        _requisitos.Add(Requisito("formación en altura"));
        Autorizar(trabajadores: [_asignado]);
        DocumentoDe(_asignado, _formacion, VigenciaDocumento.NoCaduca);

        var fila = (await Leer()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoRequerimientoTecSinVeredicto.PendienteDeSubir, "D1: el nombre es exacto, no se normaliza");
    }

    [Fact]
    public async Task Un_requisito_de_documento_especifico_no_lo_cubre_un_documento_propio_general()
    {
        _requisitos.AddRange([Requisito("Formación en altura", ambito: "EspecificoTce"), Requisito("Formación en altura", ambito: "EspecificoCentro")]);
        Autorizar(trabajadores: [_asignado]);
        DocumentoDe(_asignado, _formacion, VigenciaDocumento.NoCaduca);

        (await Leer()).Should().HaveCount(2).And.OnlyContain(f => f.Estado == EstadoRequerimientoTecSinVeredicto.PendienteDeSubir);
    }

    [Fact]
    public async Task Un_documento_de_salud_que_el_Tenant_TCE_no_podria_leer_no_cuenta()
    {
        var salud = new TipoDocumento("Reconocimiento médico completo", null, false, 3, AmbitoAplicacion.Trabajador);
        _tipos.ListaTiposDocumento.Add(salud);
        _requisitos.Add(Requisito("Reconocimiento médico completo"));
        Autorizar(trabajadores: [_asignado]);
        DocumentoDe(_asignado, salud, VigenciaDocumento.NoCaduca);

        (await Leer()).Should().ContainSingle().Which.Estado.Should().Be(EstadoRequerimientoTecSinVeredicto.PendienteDeSubir);
    }

    [Fact]
    public async Task La_Empresa_propia_es_sujeto_solo_si_la_autorizacion_la_incluye()
    {
        var conEmpresa = Guid.NewGuid();
        _requisitos.AddRange([Requisito("Seguro de responsabilidad civil", "Empresa"), Requisito("Seguro de responsabilidad civil", "Empresa", conEmpresa)]);
        Autorizar(incluyeEmpresa: false, trabajadores: [_asignado]);
        Autorizar(contrato: conEmpresa, incluyeEmpresa: true);
        _documentos.ListaDocumentos.Add(Documento.DeEmpresa(_propia.Id, _seguro.Id, _hoy.AddMonths(-1), VigenciaDocumento.VenceEl(_hoy.AddDays(5))));

        var fila = (await Leer()).Should().ContainSingle().Subject;

        fila.ContratoAccesoId.Should().Be(conEmpresa);
        fila.Sujeto.Should().Be(SujetoRequerimientoTec.Empresa);
        fila.SujetoId.Should().Be(_propia.Id);
        fila.SujetoNombre.Should().Be("Montajes Moncayo S.L.");
        fila.Estado.Should().Be(EstadoRequerimientoTecSinVeredicto.PendienteDeValidar);
        fila.EstadoDocumento.Should().Be(EstadoDocumento.Urgente);
    }

    [Fact]
    public async Task Con_dos_Empresas_propias_la_Empresa_no_es_sujeto()
    {
        _empresas.ListaEmpresas.Add(new Empresa("Montajes Moncayo Norte S.L."));
        _requisitos.Add(Requisito("Seguro de responsabilidad civil", "Empresa"));
        Autorizar(incluyeEmpresa: true);

        (await Leer()).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_requisito_de_vehiculo_no_tiene_sujeto_asignado_y_no_sale()
    {
        _requisitos.Add(Requisito("ITV", "VehiculoOMaquina"));
        Autorizar(incluyeEmpresa: true, trabajadores: [_asignado]);

        (await Leer()).Should().BeEmpty();
    }

    [Fact]
    public async Task El_Id_es_un_UUID_v5_estable_del_trio_y_distinto_por_sujeto()
    {
        var requisito = Requisito("Formación en altura");
        _requisitos.Add(requisito);
        Autorizar(trabajadores: [_asignado, _noAsignado]);

        var primera = await Leer();
        var segunda = await Leer();

        primera.Select(f => f.Id).Should().Equal(segunda.Select(f => f.Id));
        primera.Select(f => f.Id).Should().OnlyHaveUniqueItems();
        var id = primera.Single(f => f.SujetoId == _asignado.Id).Id;
        id.Should().Be(IdRequerimientoTec.De(_contrato, requisito.RequisitoId, SujetoRequerimientoTec.Trabajador, _asignado.Id));
        id.ToString("D")[14].Should().Be('5', "versión 5");
        "89ab".Should().Contain(id.ToString("D")[19].ToString(), "variante RFC");
    }

    [Fact]
    public void El_Id_v5_coincide_con_el_vector_de_la_RFC()
    {
        // RFC 9562, apéndice A.4: UUIDv5 de "www.example.com" en el espacio DNS. Fija el orden de bytes y los bits.
        IdRequerimientoTec.V5(Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), "www.example.com")
            .Should().Be(Guid.Parse("2ed6657d-e927-568b-95e1-2665a8aea6a2"));
    }
}
