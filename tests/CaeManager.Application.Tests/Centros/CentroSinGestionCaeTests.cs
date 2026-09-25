using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Commands.EstablecerGestionCaeCentro;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Visitas;
using CaeManager.Application.Visitas.Queries.ObtenerAvisoVisita;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// P1-X2 — un Centro de Trabajo sin gestión CAE no exige documentación: ni
/// estado de cumplimiento, ni aviso con datos sensibles, ni lo marca quien no
/// tiene alcance de gestión sobre él. Contextos falsos, sin Postgres: lo que
/// se prueba es la regla de Application.
/// </summary>
public class CentroSinGestionCaeTests
{
    private static readonly DateOnly Hoy = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly CentrosQueryContextFalso _centros = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly AsignacionesQueryContextFalso _asignaciones = new();
    private readonly ConfiguracionQueryContextFalso _configuracion = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly VisitasQueryContextFalso _visitas = new();

    private readonly Empresa _titular = Empresa.CrearComoCliente("Titular Demo SA", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
    private readonly Empresa _proveedora = new("Contratista Demo SL", "B12345674");
    private readonly Empresa _subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Demo SL", "B12345674", "Estandar");
    private readonly Centro _conGestion;
    private readonly Centro _sinGestion;
    private readonly Trabajador _ana;
    private readonly Trabajador _luis;

    public CentroSinGestionCaeTests()
    {
        _conGestion = new Centro(_titular.Id, _proveedora.Id, "Nave Norte");
        _sinGestion = new Centro(_titular.Id, _proveedora.Id, "Almacén Sur");
        _sinGestion.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);

        _ana = Trabajador.DeEmpresa(_proveedora.Id, "Ana", "Garcia", "12345678Z");
        _luis = Trabajador.DeEmpresa(_subcontrata.Id, "Luis", "Perez", "77189989B");

        _centros.ListaCentros.AddRange([_conGestion, _sinGestion]);
        _empresas.ListaEmpresas.AddRange([_titular, _proveedora, _subcontrata]);
        _trabajadores.ListaTrabajadores.AddRange([_ana, _luis]);
        _configuracion.ListaParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 7));

        // Un tipo obligatorio que nadie tiene: en un Centro con gestión CAE eso
        // es un hueco; en uno sin gestión CAE no puede serlo.
        _tipos.ListaTiposDocumento.Add(new TipoDocumento(
            "Reconocimiento médico", vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1,
            AmbitoAplicacion.Trabajador, RequisitoDocumental.Si));

        _asignaciones.ListaAsignaciones.Add(new Asignacion(_ana.Id, _conGestion.Id, Hoy.AddDays(-10)));
        _asignaciones.ListaAsignaciones.Add(new Asignacion(_ana.Id, _sinGestion.Id, Hoy.AddDays(-10)));
    }

    private CalculoEstadoCentroService Calculo() =>
        new(_centros, _documentos, _tipos, _trabajadores, _asignaciones, _configuracion);

    // ---- Cumplimiento y estado ----

    [Fact]
    public async Task El_centro_sin_gestion_cae_tiene_estado_propio_sin_causas_y_el_con_gestion_sigue_exigiendo()
    {
        var estados = await Calculo().CalcularAsync([_conGestion.Id, _sinGestion.Id], CancellationToken.None);

        estados[_sinGestion.Id].Estado.Should().Be(EstadoCentro.SinGestionCae,
            "sin gestión CAE no hay nada que exigir; ni Vigente (verde falso) ni Faltante (pendiente falso)");
        estados[_sinGestion.Id].Causas.Should().BeEmpty();

        // Control positivo: el mismo Trabajador, sin el mismo documento, sí deja
        // en falta el Centro que exige gestión CAE.
        estados[_conGestion.Id].Estado.Should().NotBe(EstadoCentro.Vigente);
        estados[_conGestion.Id].Causas.Should().NotBeEmpty();
    }

    [Fact]
    public async Task El_centro_sin_gestion_cae_no_tiene_porcentaje_de_cumplimiento()
    {
        var cumplimiento = await Calculo().CalcularCumplimientoAsync([_conGestion.Id, _sinGestion.Id], CancellationToken.None);

        var sin = cumplimiento.TryGetValue(_sinGestion.Id, out var fraccion) ? fraccion : new FraccionCumplimiento(0, 0);
        sin.Requeridos.Should().Be(0);
        sin.Porcentaje.Should().BeNull("un anillo al 0 % o al 100 % afirmaría algo que no se mide");

        cumplimiento[_conGestion.Id].Requeridos.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task El_acordeon_de_centro_360_no_exige_documentos_en_un_centro_sin_gestion_cae()
    {
        var handler = new ObtenerAsignacionesDocumentacionPorCentroQueryHandler(
            _asignaciones, _trabajadores, _tipos, _documentos, _configuracion, _centros, new AlcanceDatosServiceFalso());

        var sin = await handler.Handle(new ObtenerAsignacionesDocumentacionPorCentroQuery(_sinGestion.Id), CancellationToken.None);
        var con = await handler.Handle(new ObtenerAsignacionesDocumentacionPorCentroQuery(_conGestion.Id), CancellationToken.None);

        // La asignación se sigue viendo (quién va al Centro importa para el aviso);
        // lo que desaparece es la exigencia documental.
        sin.Should().ContainSingle().Which.Documentos.Should().BeEmpty();
        sin.Single().PeorEstado.Should().NotBe(EstadoDocumento.Faltante);

        // Control positivo: el mismo Trabajador en un Centro con gestión CAE sí
        // tiene el documento obligatorio en falta.
        con.Should().ContainSingle().Which.PeorEstado.Should().Be(EstadoDocumento.Faltante);
    }

    [Fact]
    public void El_estado_sin_gestion_cae_ordena_por_debajo_de_vigente()
    {
        CalculadoraEstadoCentro.Gravedad(EstadoCentro.SinGestionCae)
            .Should().BeLessThan(CalculadoraEstadoCentro.Gravedad(EstadoCentro.Vigente));
    }

    // ---- Aviso de visita ----

    private Visita VisitaA(Centro centro, params Trabajador[] quienes)
    {
        var visita = new Visita(centro.Id, Hoy.AddDays(2), Hoy.AddDays(2), "Nota interna: salud laboral de Ana", horaEstimadaAcceso: new TimeOnly(8, 30));
        _visitas.ListaVisitas.Add(visita);
        foreach (var trabajador in quienes)
            _visitas.ListaVisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, trabajador.Id));
        return visita;
    }

    private ObtenerAvisoVisitaQueryHandler Aviso(AlcanceDatosServiceFalso? alcance = null) =>
        new(_visitas, _centros, _empresas, _trabajadores, alcance ?? new AlcanceDatosServiceFalso());

    [Fact]
    public async Task El_aviso_lleva_centro_fecha_hora_y_trabajadores_con_su_empresa_y_nunca_el_dni()
    {
        var visita = VisitaA(_sinGestion, _ana, _luis);

        var resultado = await Aviso().Handle(new ObtenerAvisoVisitaQuery(visita.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var texto = resultado.Valor.Asunto + "\n" + resultado.Valor.Cuerpo;
        texto.Should().Contain("Almacén Sur");
        texto.Should().Contain(Hoy.AddDays(2).ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture));
        texto.Should().Contain("08:30");
        texto.Should().Contain("Ana Garcia (Contratista Demo SL)");
        texto.Should().Contain("Luis Perez (Subcontrata Demo SL)");
        texto.Should().NotContain("12345678Z").And.NotContain("77189989B");
        texto.Should().NotContain("Nota interna", "las notas de la visita no viajan en el aviso");
    }

    [Fact]
    public async Task Fuera_del_alcance_de_gestion_el_aviso_responde_como_visita_inexistente()
    {
        var visita = VisitaA(_sinGestion, _ana);
        // Lectura sobre el Centro, pero sin gestión: el Gestor CAE no lo tiene
        // en su Asignación de Cartera (o es un rol de portal).
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [_sinGestion.Id], centroIdsParaGestion: []);

        var resultado = await Aviso(alcance).Handle(new ObtenerAvisoVisitaQuery(visita.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Should().Be(ObtenerAvisoVisitaQueryHandler.NoEncontrada);
    }

    [Fact]
    public async Task Con_alcance_de_gestion_sobre_el_centro_el_aviso_se_compone()
    {
        var visita = VisitaA(_sinGestion, _ana);
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [_sinGestion.Id], centroIdsParaGestion: [_sinGestion.Id]);

        var resultado = await Aviso(alcance).Handle(new ObtenerAvisoVisitaQuery(visita.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Un_centro_con_gestion_cae_no_da_aviso_porque_la_visita_se_acredita_con_documentacion()
    {
        var visita = VisitaA(_conGestion, _ana);

        var resultado = await Aviso().Handle(new ObtenerAvisoVisitaQuery(visita.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Should().Be(ObtenerAvisoVisitaQueryHandler.CentroConGestionCae);
    }

    // ---- Marcar la modalidad ----

    [Fact]
    public async Task Marcar_sin_gestion_cae_exige_alcance_de_gestion_sobre_el_centro()
    {
        var repositorio = new CentroRepositorioFalso();
        repositorio.Agregar(_conGestion);
        var unidad = new UnitOfWorkFalso();
        var sinGestion = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [_conGestion.Id], centroIdsParaGestion: []);

        var resultado = await new EstablecerGestionCaeCentroCommandHandler(repositorio, sinGestion, unidad)
            .Handle(new EstablecerGestionCaeCentroCommand(_conGestion.Id, ModalidadGestionCae.SinGestionCae), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        _conGestion.GestionCae.Should().Be(ModalidadGestionCae.ConGestionCae);
        unidad.VecesGuardado.Should().Be(0);

        var conGestion = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [_conGestion.Id], centroIdsParaGestion: [_conGestion.Id]);

        var permitido = await new EstablecerGestionCaeCentroCommandHandler(repositorio, conGestion, unidad)
            .Handle(new EstablecerGestionCaeCentroCommand(_conGestion.Id, ModalidadGestionCae.SinGestionCae), CancellationToken.None);

        permitido.EsExitoso.Should().BeTrue();
        _conGestion.GestionCae.Should().Be(ModalidadGestionCae.SinGestionCae);
        unidad.VecesGuardado.Should().Be(1);
    }

    private sealed class VisitasQueryContextFalso : IVisitasQueryContext
    {
        public List<Visita> ListaVisitas { get; } = [];
        public List<VisitaTrabajador> ListaVisitasTrabajadores { get; } = [];

        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(ListaVisitas.AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores => new TestAsyncQueryable<VisitaTrabajador>(ListaVisitasTrabajadores.AsQueryable());
    }
}
