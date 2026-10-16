using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// Serie documental por tipo de contratación (decisión 2026-10-16): un Tipo de documento de Ámbito Trabajador se pide
/// «de serie» solo a los tipos de contratación de su serie. La fila explícita del Centro manda sobre la serie. Se prueba
/// en las tres superficies que deciden qué se exige a un Trabajador concreto: el estado del Centro, los faltantes y la
/// ficha del Trabajador, más el acordeón de Asignaciones del Centro, que decide por cada Trabajador del mismo listado.
/// Contextos falsos, sin Postgres.
/// </summary>
public class SerieDocumentalPorTipoContratacionTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    private readonly CentrosQueryContextFalso _centros = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly AsignacionesQueryContextFalso _asignaciones = new();
    private readonly ConfiguracionQueryContextFalso _configuracion = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly CaeManager.Application.Tests.Proyectos.ProyectosQueryContextFalso _proyectos = new();

    private readonly Empresa _titular = Empresa.CrearComoCliente("Titular Demo SA", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
    private readonly Empresa _proveedora = new("Contratista Demo SL", "B12345674");
    private readonly Centro _centro;
    private readonly Trabajador _cuentaAjena;
    private readonly Trabajador _voluntario;
    private readonly TipoDocumento _tipo;

    public SerieDocumentalPorTipoContratacionTests()
    {
        _centro = new Centro(_titular.Id, _proveedora.Id, "Nave Norte");
        _cuentaAjena = Trabajador.DeEmpresa(_proveedora.Id, "Ana", "Garcia", "12345678Z");
        _voluntario = Trabajador.DeEmpresa(_proveedora.Id, "Vic", "Voluntario", "77189989B");
        _voluntario.CambiarTipoContratacion(TipoContratacion.Voluntario);

        _centros.ListaCentros.Add(_centro);
        _empresas.ListaEmpresas.AddRange([_titular, _proveedora]);
        _trabajadores.ListaTrabajadores.AddRange([_cuentaAjena, _voluntario]);
        _configuracion.ListaParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 7));

        _tipo = new TipoDocumento(
            "Contrato de trabajo", vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1,
            AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        _tipos.ListaTiposDocumento.Add(_tipo);

        _asignaciones.ListaAsignaciones.Add(new Asignacion(_cuentaAjena.Id, _centro.Id, Hoy.AddDays(-10)));
        _asignaciones.ListaAsignaciones.Add(new Asignacion(_voluntario.Id, _centro.Id, Hoy.AddDays(-10)));

        // Un voluntario sin evento sin terminar está inactivo y no cuenta en el Centro: se le da uno vigente.
        var evento = Proyecto.Crear(_titular.Id, _centro.Id, "Evento Demo", Hoy.AddDays(-1), Hoy.AddDays(30), null, TipoProyecto.Evento);
        _proyectos.ListaProyectos.Add(evento);
        _proyectos.ListaProyectosTecnicos.Add(new ProyectoTecnico(evento.Id, _voluntario.Id, Hoy.AddDays(-1)));
    }

    private void SerieSinVoluntario() =>
        _tipo.EstablecerTiposContratacionDeSerie(TiposContratacion.CuentaAjena | TiposContratacion.Autonomo);

    private void CentroIncluyeElTipo() =>
        _tipos.ListaTiposDocumentoCentros.Add(new TipoDocumentoCentro(_tipo.Id, _centro.Id, incluido: true));

    // ---- Las superficies, preguntadas por Trabajador ----

    private async Task<bool> EstadoDelCentroLoEchaEnFaltaA(Trabajador trabajador)
    {
        var estados = await new CalculoEstadoCentroService(
                _centros, _documentos, _tipos, _trabajadores, _asignaciones, _configuracion, _empresas, _proyectos, _proyectos)
            .CalcularAsync([_centro.Id], CancellationToken.None);
        return estados[_centro.Id].Causas.Any(c =>
            c.TrabajadorId == trabajador.Id && c.TipoDocumentoId == _tipo.Id && c.Estado == EstadoDocumento.Faltante);
    }

    private async Task<bool> FaltantesLoEchanEnFaltaA(Trabajador trabajador)
    {
        var faltantes = await new DocumentosFaltantesService(_tipos, _documentos, _centros).CalcularAsync(
            [new ParejaTrabajadorCentro(trabajador.Id, trabajador.Nombre, _centro.Id, _centro.Nombre, trabajador.TipoContratacion)],
            CancellationToken.None);
        return faltantes.Any(f => f.TrabajadorId == trabajador.Id && f.TipoDocumentoId == _tipo.Id);
    }

    private async Task<bool> LaFichaLoExigeA(Trabajador trabajador)
    {
        var centros = await new ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler(
                _asignaciones, _centros, _empresas, _tipos, _documentos, _configuracion, _trabajadores,
                new AlcanceDatosServiceFalso(), new SituacionDocumentosSinDatos())
            .Handle(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(trabajador.Id), CancellationToken.None);
        return centros.Single(c => c.CentroId == _centro.Id).Documentos.Any(d => d.TipoDocumentoId == _tipo.Id);
    }

    private async Task<bool> ElAcordeonDelCentroLoExigeA(Trabajador trabajador)
    {
        var filas = await new ObtenerAsignacionesDocumentacionPorCentroQueryHandler(
                _asignaciones, _trabajadores, _tipos, _documentos, _configuracion, _centros,
                new AlcanceDatosServiceFalso(), new SituacionDocumentosSinDatos())
            .Handle(new ObtenerAsignacionesDocumentacionPorCentroQuery(_centro.Id), CancellationToken.None);
        return filas.Single(f => f.TrabajadorId == trabajador.Id).Documentos.Any(d => d.TipoDocumentoId == _tipo.Id);
    }

    // ---- (1) Con serie Todos nada cambia ----

    [Fact]
    public async Task Con_la_serie_por_defecto_el_estado_del_centro_exige_el_tipo_a_todos()
    {
        _tipo.TiposContratacionDeSerie.Should().Be(TiposContratacion.Todos);

        (await EstadoDelCentroLoEchaEnFaltaA(_cuentaAjena)).Should().BeTrue();
        (await EstadoDelCentroLoEchaEnFaltaA(_voluntario)).Should().BeTrue();
    }

    [Fact]
    public async Task Con_la_serie_por_defecto_los_faltantes_exigen_el_tipo_a_todos()
    {
        (await FaltantesLoEchanEnFaltaA(_cuentaAjena)).Should().BeTrue();
        (await FaltantesLoEchanEnFaltaA(_voluntario)).Should().BeTrue();
    }

    [Fact]
    public async Task Con_la_serie_por_defecto_la_ficha_y_el_acordeon_exigen_el_tipo_a_todos()
    {
        (await LaFichaLoExigeA(_cuentaAjena)).Should().BeTrue();
        (await LaFichaLoExigeA(_voluntario)).Should().BeTrue();
        (await ElAcordeonDelCentroLoExigeA(_cuentaAjena)).Should().BeTrue();
        (await ElAcordeonDelCentroLoExigeA(_voluntario)).Should().BeTrue();
    }

    // ---- (2) Una serie sin Voluntario no se le exige al Voluntario, pero sí al de cuenta ajena ----

    [Fact]
    public async Task Una_serie_sin_voluntario_no_deja_en_falta_el_estado_del_centro_a_un_voluntario_activo()
    {
        SerieSinVoluntario();

        (await EstadoDelCentroLoEchaEnFaltaA(_cuentaAjena)).Should().BeTrue("la serie incluye cuenta ajena");
        (await EstadoDelCentroLoEchaEnFaltaA(_voluntario)).Should().BeFalse("la serie no incluye voluntario");
    }

    [Fact]
    public async Task Una_serie_sin_voluntario_no_le_echa_en_falta_el_tipo_en_los_faltantes()
    {
        SerieSinVoluntario();

        (await FaltantesLoEchanEnFaltaA(_cuentaAjena)).Should().BeTrue();
        (await FaltantesLoEchanEnFaltaA(_voluntario)).Should().BeFalse();
    }

    [Fact]
    public async Task Una_serie_sin_voluntario_no_se_lista_en_la_ficha_del_voluntario_pero_si_en_la_de_cuenta_ajena()
    {
        SerieSinVoluntario();

        (await LaFichaLoExigeA(_cuentaAjena)).Should().BeTrue();
        (await LaFichaLoExigeA(_voluntario)).Should().BeFalse();
    }

    [Fact]
    public async Task El_acordeon_del_centro_decide_por_cada_trabajador_con_su_tipo_de_contratacion()
    {
        SerieSinVoluntario();

        // Mismo Centro, mismo listado: al de cuenta ajena se le exige y al voluntario no.
        (await ElAcordeonDelCentroLoExigeA(_cuentaAjena)).Should().BeTrue();
        (await ElAcordeonDelCentroLoExigeA(_voluntario)).Should().BeFalse();
    }

    // ---- (3) La fila explícita del Centro lo exige igualmente al Voluntario ----

    [Fact]
    public async Task Una_fila_incluida_del_centro_exige_el_tipo_al_voluntario_en_el_estado_del_centro()
    {
        SerieSinVoluntario();
        CentroIncluyeElTipo();

        (await EstadoDelCentroLoEchaEnFaltaA(_voluntario)).Should().BeTrue();
        (await EstadoDelCentroLoEchaEnFaltaA(_cuentaAjena)).Should().BeTrue();
    }

    [Fact]
    public async Task Una_fila_incluida_del_centro_exige_el_tipo_al_voluntario_en_los_faltantes()
    {
        SerieSinVoluntario();
        CentroIncluyeElTipo();

        (await FaltantesLoEchanEnFaltaA(_voluntario)).Should().BeTrue();
    }

    [Fact]
    public async Task Una_fila_incluida_del_centro_exige_el_tipo_al_voluntario_en_la_ficha_y_el_acordeon()
    {
        SerieSinVoluntario();
        CentroIncluyeElTipo();

        (await LaFichaLoExigeA(_voluntario)).Should().BeTrue();
        (await ElAcordeonDelCentroLoExigeA(_voluntario)).Should().BeTrue();
    }
}
