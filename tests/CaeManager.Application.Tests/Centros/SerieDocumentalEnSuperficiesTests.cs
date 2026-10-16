using CaeManager.Application.Centros;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Queries.ObtenerSupervisionSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerTrabajadoresDocumentacionPorSubcontrata;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Tests.Visitas;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTipoDocumentoPorId;
using CaeManager.Application.Visitas;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// Serie documental por tipo de contratación (decisión 2026-10-16) en las superficies que no cubre
/// <see cref="SerieDocumentalPorTipoContratacionTests"/>: supervisión, cumplimiento y documentación de la Subcontrata,
/// expediente y documentación de la Visita, alta de acreditaciones en plataforma, rechazos en plataforma del estado del
/// Centro y lectura del tipo. Cada caso se pone en rojo si la superficie ignora la serie: el mismo escenario con la
/// serie por defecto (Todos) sí exige el tipo al voluntario. Contextos falsos, sin Postgres.
/// </summary>
public class SerieDocumentalEnSuperficiesTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    private readonly CentrosQueryContextFalso _centros = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly AsignacionesQueryContextFalso _asignaciones = new();
    private readonly ConfiguracionQueryContextFalso _configuracion = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly SubcontratasQueryContextFalso _subcontratas = new();
    private readonly VisitasQueryContextFalso _visitas = new();
    private readonly CaeManager.Application.Tests.Proyectos.ProyectosQueryContextFalso _proyectos = new();

    private readonly Empresa _titular = Empresa.CrearComoCliente("Titular Demo SA", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
    private readonly Empresa _proveedora = new("Contratista Demo SL", "B12345674");
    private readonly Empresa _subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Demo SL", "B12345674", "Estandar");
    private readonly Centro _centro;
    private readonly Trabajador _cuentaAjena;
    private readonly Trabajador _voluntario;
    private readonly TipoDocumento _tipo;

    public SerieDocumentalEnSuperficiesTests()
    {
        _centro = new Centro(_titular.Id, _proveedora.Id, "Nave Norte");
        _cuentaAjena = Trabajador.DeSubcontrata(_subcontrata.Id, "Ana", "Garcia", "12345678Z");
        _voluntario = Trabajador.DeSubcontrata(_subcontrata.Id, "Vic", "Voluntario", "77189989B");
        _voluntario.CambiarTipoContratacion(TipoContratacion.Voluntario);

        _centros.ListaCentros.Add(_centro);
        _empresas.ListaEmpresas.AddRange([_titular, _proveedora, _subcontrata]);
        _trabajadores.ListaTrabajadores.AddRange([_cuentaAjena, _voluntario]);
        _configuracion.ListaParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 7));

        _tipo = new TipoDocumento(
            "Aptitud médica", vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1,
            AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        _tipos.ListaTiposDocumento.Add(_tipo);

        // Un voluntario sin evento sin terminar está inactivo y no cuenta en el Centro: se le da uno vigente.
        var evento = Proyecto.Crear(_titular.Id, _centro.Id, "Evento Demo", Hoy.AddDays(-1), Hoy.AddDays(30), null, TipoProyecto.Evento);
        _proyectos.ListaProyectos.Add(evento);
        _proyectos.ListaProyectosTecnicos.Add(new ProyectoTecnico(evento.Id, _voluntario.Id, Hoy.AddDays(-1)));
    }

    private void SerieSinVoluntario() =>
        _tipo.EstablecerTiposContratacionDeSerie(TiposContratacion.CuentaAjena | TiposContratacion.Autonomo);

    private void Asignar(Trabajador trabajador) =>
        _asignaciones.ListaAsignaciones.Add(new Asignacion(trabajador.Id, _centro.Id, Hoy.AddDays(-10)));

    // ---- Supervisión de la Subcontrata: checklist del Centro ----

    private async Task<SupervisionTipoDto?> FilaDeSupervision()
    {
        var supervision = await new ObtenerSupervisionSubcontrataQueryHandler(
                _subcontratas, _asignaciones, _trabajadores, _centros, _empresas, _tipos, _configuracion,
                new AlcanceDatosServiceFalso())
            .Handle(new ObtenerSupervisionSubcontrataQuery(_subcontrata.Id), CancellationToken.None);
        return supervision!.Centros.Where(c => c.CentroId == _centro.Id).SelectMany(c => c.Tipos)
            .SingleOrDefault(t => t.TipoDocumentoId == _tipo.Id);
    }

    [Fact]
    public async Task Supervision_con_personal_solo_voluntario_y_serie_sin_voluntario_no_exige_el_tipo_en_el_centro()
    {
        Asignar(_voluntario);

        (await FilaDeSupervision())!.Exigido.Should().BeTrue("control: con la serie por defecto sí se exige");

        SerieSinVoluntario();
        (await FilaDeSupervision()).Should().BeNull("nadie de la Subcontrata en el Centro tiene un tipo de contratación de la serie");
    }

    [Fact]
    public async Task Supervision_con_personal_mixto_exige_el_tipo_en_el_centro()
    {
        SerieSinVoluntario();
        Asignar(_voluntario);
        Asignar(_cuentaAjena);

        (await FilaDeSupervision())!.Exigido.Should().BeTrue("el de cuenta ajena está en la serie");
    }

    [Fact]
    public async Task Supervision_con_fila_explicita_del_centro_exige_el_tipo_aunque_el_personal_sea_solo_voluntario()
    {
        SerieSinVoluntario();
        Asignar(_voluntario);
        _tipos.ListaTiposDocumentoCentros.Add(new TipoDocumentoCentro(_tipo.Id, _centro.Id, incluido: true));

        (await FilaDeSupervision())!.Exigido.Should().BeTrue("la fila explícita del Centro manda sobre la serie");
    }

    [Fact]
    public async Task Supervision_sin_personal_en_el_centro_aplica_la_regla_general_sin_serie()
    {
        SerieSinVoluntario();
        // El Centro entra en la supervisión solo por una verificación registrada (de otro tipo): no hay personal.
        var otroTipo = new TipoDocumento(
            "Seguro RC", vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 2,
            AmbitoAplicacion.Empresa, RequisitoDocumental.No);
        _tipos.ListaTiposDocumento.Add(otroTipo);
        _subcontratas.ListaVerificacionesExternaSubcontrata.Add(new VerificacionExternaSubcontrata(
            _subcontrata.Id, _centro.Id, otroTipo.Id, Hoy.AddDays(-2), ResultadoVerificacionExterna.Valido, Guid.NewGuid()));

        (await FilaDeSupervision())!.Exigido.Should().BeTrue("sin personal no hay tipo de contratación al que aplicar la serie");
    }

    // ---- Cumplimiento y documentación de la Subcontrata ----

    private async Task<int> RequeridosDeLaSubcontrata() =>
        (await new CalculoEstadoSubcontrataService(
                _trabajadores, _asignaciones, _documentos, _tipos, _configuracion, _centros, new AlcanceDatosServiceFalso(),
                _proyectos, _proyectos)
            .CalcularCumplimientoAsync([_subcontrata.Id], CancellationToken.None))[_subcontrata.Id].Requeridos;

    [Fact]
    public async Task El_cumplimiento_de_la_subcontrata_no_cuenta_el_tipo_al_voluntario_si_la_serie_no_lo_incluye()
    {
        Asignar(_voluntario);
        (await RequeridosDeLaSubcontrata()).Should().Be(1, "control: con la serie por defecto se le exige");

        SerieSinVoluntario();
        (await RequeridosDeLaSubcontrata()).Should().Be(0);
    }

    private async Task<bool> LaDocumentacionDeLaSubcontrataLoExigeA(Trabajador trabajador)
    {
        var filas = await new ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler(
                _trabajadores, _asignaciones, _tipos, _documentos, _configuracion, _centros, new AlcanceDatosServiceFalso(),
                _proyectos, _proyectos)
            .Handle(new ObtenerTrabajadoresDocumentacionPorSubcontrataQuery(_subcontrata.Id), CancellationToken.None);
        return filas.Single(f => f.TrabajadorId == trabajador.Id).Documentos.Any(d => d.TipoDocumentoId == _tipo.Id);
    }

    [Fact]
    public async Task La_documentacion_por_subcontrata_decide_por_cada_trabajador_con_su_tipo_de_contratacion()
    {
        Asignar(_voluntario);
        Asignar(_cuentaAjena);
        (await LaDocumentacionDeLaSubcontrataLoExigeA(_voluntario)).Should().BeTrue("control: serie por defecto");

        SerieSinVoluntario();
        (await LaDocumentacionDeLaSubcontrataLoExigeA(_voluntario)).Should().BeFalse();
        (await LaDocumentacionDeLaSubcontrataLoExigeA(_cuentaAjena)).Should().BeTrue();
    }

    // ---- Visita: expediente y documentación ----

    private Visita VisitaCon(params Trabajador[] participantes)
    {
        var visita = new Visita(_centro.Id, Hoy.AddDays(3), Hoy.AddDays(3), null);
        visita.RegistrarOrigenSolicitud(Guid.NewGuid(), DateTime.UtcNow.AddHours(-1));
        _visitas.ListaVisitas.Add(visita);
        foreach (var trabajador in participantes)
            _visitas.ListaVisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, trabajador.Id));
        return visita;
    }

    private async Task<bool> ElExpedienteQuedaCompleto(Visita visita)
    {
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Visitas.Add(visita);
        return await new EvaluadorExpedienteVisitaService(
                _visitas, _centros, _documentos, _tipos, _trabajadores, _configuracion, repositorio, new UnitOfWorkFalso(),
                NullLogger<EvaluadorExpedienteVisitaService>.Instance)
            .EvaluarAsync(visita.Id);
    }

    [Fact]
    public async Task El_expediente_de_una_visita_con_un_voluntario_no_espera_un_tipo_cuya_serie_no_lo_incluye()
    {
        SerieSinVoluntario();

        (await ElExpedienteQuedaCompleto(VisitaCon(_voluntario))).Should().BeTrue(
            "al voluntario no se le pide de serie, así que no falta nada que reunir");
    }

    [Fact]
    public async Task El_expediente_de_una_visita_con_un_voluntario_espera_el_tipo_con_la_serie_por_defecto()
    {
        (await ElExpedienteQuedaCompleto(VisitaCon(_voluntario))).Should().BeFalse("control: la serie Todos se lo pide");
    }

    private async Task<bool> LaDocumentacionDeLaVisitaLeEchaEnFaltaA(Visita visita, Trabajador trabajador)
    {
        var documentacion = await new ObtenerDocumentacionVisitaQueryHandler(
                _visitas, _centros, _documentos, _tipos, _trabajadores, _empresas, _configuracion, new AlcanceDatosServiceFalso())
            .Handle(new ObtenerDocumentacionVisitaQuery(visita.Id), CancellationToken.None);
        return documentacion!.Trabajadores.Single(t => t.TrabajadorId == trabajador.Id).Documentacion.Documentos
            .Any(d => d.TipoDocumentoId == _tipo.Id && d.Estado == EstadoDocumento.Faltante);
    }

    [Fact]
    public async Task La_documentacion_de_la_visita_decide_el_faltante_por_cada_participante()
    {
        var visita = VisitaCon(_voluntario, _cuentaAjena);
        (await LaDocumentacionDeLaVisitaLeEchaEnFaltaA(visita, _voluntario)).Should().BeTrue("control: serie por defecto");

        SerieSinVoluntario();
        (await LaDocumentacionDeLaVisitaLeEchaEnFaltaA(visita, _voluntario)).Should().BeFalse();
        (await LaDocumentacionDeLaVisitaLeEchaEnFaltaA(visita, _cuentaAjena)).Should().BeTrue();
    }

    // ---- Alta de acreditaciones en plataforma ----

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task El_alta_de_acreditaciones_no_da_de_alta_un_documento_de_voluntario_que_su_serie_no_pide(
        bool serieSinVoluntario, int agregadasEsperadas)
    {
        var mundo = new MundoAcreditaciones();
        var voluntario = mundo.Trabajador();
        voluntario.CambiarTipoContratacion(TipoContratacion.Voluntario);
        var centro = mundo.Centro();
        mundo.AccesoPlataforma(centro);
        mundo.Asignacion(voluntario, centro);
        var tipo = mundo.Tipo(RequisitoDocumental.Si);
        if (serieSinVoluntario)
            tipo.EstablecerTiposContratacionDeSerie(TiposContratacion.CuentaAjena | TiposContratacion.Autonomo);
        var documento = Documento.DeTrabajador(voluntario.Id, tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(5)));

        var agregadas = await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] });

        agregadas.Should().Be(agregadasEsperadas);
    }

    // ---- Estado del Centro: rechazos en plataforma ----

    private async Task<bool> ElEstadoDelCentroMuestraElRechazoDe(Trabajador trabajador)
    {
        var estados = await new CalculoEstadoCentroService(
                _centros, _documentos, _tipos, _trabajadores, _asignaciones, _configuracion, _empresas, _proyectos, _proyectos)
            .CalcularAsync([_centro.Id], CancellationToken.None);
        return estados[_centro.Id].Causas.Any(c =>
            c.Descripcion.Contains(trabajador.Nombre, StringComparison.Ordinal)
            && c.Descripcion.Contains("rechazado por la plataforma", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Un_rechazo_en_plataforma_de_un_tipo_cuya_serie_no_incluye_al_voluntario_no_bloquea_el_centro()
    {
        Asignar(_voluntario);
        var canal = CanalGestionDocumental.DePlataforma(_centro.Id, "Portal Norte", Guid.NewGuid(), null, null, null);
        _centros.ListaCanalesGestionDocumental.Add(canal);
        var documento = Documento.DeTrabajador(_voluntario.Id, _tipo.Id, Hoy.AddDays(-5), VigenciaDocumento.VenceEl(Hoy.AddYears(1)));
        _documentos.ListaDocumentos.Add(documento);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        acreditacion.MarcarSubida();
        acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "No se lee", DateTime.UtcNow.AddDays(-1));
        _documentos.ListaAcreditacionesDocumentoPlataforma.Add(acreditacion);

        (await ElEstadoDelCentroMuestraElRechazoDe(_voluntario)).Should().BeTrue("control: con la serie por defecto el rechazo bloquea");

        SerieSinVoluntario();
        (await ElEstadoDelCentroMuestraElRechazoDe(_voluntario)).Should().BeFalse("el tipo no se le pide de serie al voluntario");
    }

    // ---- Lectura del tipo ----

    [Fact]
    public async Task La_lectura_del_tipo_por_id_devuelve_su_serie()
    {
        SerieSinVoluntario();

        var detalle = await new ObtenerTipoDocumentoPorIdQueryHandler(_tipos, PoliticaNotaInternaPruebas.Con("GestorCae"))
            .Handle(new ObtenerTipoDocumentoPorIdQuery(_tipo.Id), CancellationToken.None);

        detalle!.TiposContratacionDeSerie.Should().Be(TiposContratacion.CuentaAjena | TiposContratacion.Autonomo);
    }

    private sealed class VisitasQueryContextFalso : IVisitasQueryContext
    {
        public List<Visita> ListaVisitas { get; } = [];
        public List<VisitaTrabajador> ListaVisitasTrabajadores { get; } = [];

        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(ListaVisitas.AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores => new TestAsyncQueryable<VisitaTrabajador>(ListaVisitasTrabajadores.AsQueryable());
    }
}
