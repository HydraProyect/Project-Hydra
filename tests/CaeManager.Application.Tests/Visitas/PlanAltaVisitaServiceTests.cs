using CaeManager.Application.Gestiones;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Visitas.AltaDeVisita;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;
using TipoDocumentoEntidad = CaeManager.Domain.Documentos.TipoDocumento;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// <see cref="PlanAltaVisitaService"/> con los contextos de consulta en memoria: que cada filtro de las lecturas cuente. La
/// regla en sí la prueba <see cref="ReglaGestionesDeVisitaTests"/>; aquí, que le llegan los datos de este Centro, de estos
/// Trabajadores y solo los que valen (Asignaciones del par, Documentos operativos, Gestiones Pendientes, Tipos sin archivar,
/// fila explícita y periodicidad del Centro).
/// </summary>
public class PlanAltaVisitaServiceTests
{
    private static readonly DateOnly Fin = new(2026, 12, 10);
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    private readonly Centro _centro = new(Guid.NewGuid(), Guid.NewGuid(), "Planta norte");
    private readonly Centro _otroCentro = new(Guid.NewGuid(), Guid.NewGuid(), "Planta sur");
    private readonly Trabajador _ana;
    private readonly TipoDocumentoEntidad _formacion = Tipo("Formación PRL", RequisitoDocumental.Si);

    private readonly CentrosQueryContextFalso _centros = new();
    private readonly AsignacionesQueryContextFalso _asignaciones = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly GestionesFalsas _gestiones = new();

    public PlanAltaVisitaServiceTests()
    {
        _ana = Trabajador.DeEmpresa(_centro.EmpresaId, "Ana", "García", "12345678Z");
        _centros.ListaCentros.AddRange([_centro, _otroCentro]);
        _trabajadores.ListaTrabajadores.Add(_ana);
        _tipos.ListaTiposDocumento.Add(_formacion);
    }

    private static TipoDocumentoEntidad Tipo(string nombre, RequisitoDocumental requerido) =>
        new(nombre, vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1, AmbitoAplicacion.Trabajador, requerido);

    private Task<PlanAltaVisitaDto> CalcularAsync() =>
        new PlanAltaVisitaService(_centros, _asignaciones, _trabajadores, _tipos, _documentos, _gestiones)
            .CalcularAsync(_centro.Id, Fin, Hoy, [_ana.Id], CancellationToken.None);

    private void AsignarAna(Guid centroId) => _asignaciones.ListaAsignaciones.Add(new Asignacion(_ana.Id, centroId, Hoy.AddDays(-30)));

    private Documento DocumentoDeAna(TipoDocumentoEntidad tipo, VigenciaDocumento vigencia, DateOnly? emision = null)
    {
        var documento = Documento.DeTrabajador(_ana.Id, tipo.Id, emision ?? new DateOnly(2026, 1, 1), vigencia);
        _documentos.ListaDocumentos.Add(documento);
        return documento;
    }

    // ---- Asignaciones del par

    [Fact]
    public async Task Una_Asignacion_activa_a_otro_Centro_no_cuenta_como_asignada_a_este()
    {
        AsignarAna(_otroCentro.Id);

        var plan = await CalcularAsync();

        plan.Asignaciones.Select(a => a.TrabajadorId).Should().Equal(_ana.Id);
    }

    [Fact]
    public async Task Una_Asignacion_activa_a_este_Centro_cuenta_como_asignada()
    {
        AsignarAna(_centro.Id);

        var plan = await CalcularAsync();

        plan.Asignaciones.Should().BeEmpty();
        plan.Solapadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_Asignacion_cerrada_que_ya_termino_no_cuenta_como_asignada_ni_solapa()
    {
        var cerrada = new Asignacion(_ana.Id, _centro.Id, Hoy.AddDays(-30));
        cerrada.DarDeBaja(Hoy.AddDays(-10));
        _asignaciones.ListaAsignaciones.Add(cerrada);

        var plan = await CalcularAsync();

        plan.Asignaciones.Select(a => a.TrabajadorId).Should().Equal(_ana.Id);
        plan.Solapadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_Asignacion_cerrada_cuya_baja_es_posterior_a_hoy_solapa_y_no_se_ofrece_asignar()
    {
        var cerrada = new Asignacion(_ana.Id, _centro.Id, Hoy.AddDays(-5));
        cerrada.DarDeBaja(Hoy.AddDays(3));
        _asignaciones.ListaAsignaciones.Add(cerrada);

        var plan = await CalcularAsync();

        plan.Asignaciones.Should().BeEmpty();
        plan.Solapadas.Should().ContainSingle().Which.Should().Be(new AsignacionPrevistaDto(_ana.Id, "Ana García"));
    }

    [Fact]
    public async Task Una_Asignacion_cerrada_de_otro_Centro_no_solapa_con_este()
    {
        var cerrada = new Asignacion(_ana.Id, _otroCentro.Id, Hoy.AddDays(-5));
        cerrada.DarDeBaja(Hoy.AddDays(3));
        _asignaciones.ListaAsignaciones.Add(cerrada);

        var plan = await CalcularAsync();

        plan.Solapadas.Should().BeEmpty();
        plan.Asignaciones.Select(a => a.TrabajadorId).Should().Equal(_ana.Id);
    }

    // ---- Gestiones Pendientes

    [Fact]
    public async Task Una_Gestion_Pendiente_de_este_Centro_y_Tipo_suprime_la_nueva()
    {
        _gestiones.Lista.Add(new Gestion(_ana.Id, _centro.Id, _formacion.Id));

        (await CalcularAsync()).Gestiones.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_Gestion_Pendiente_de_otro_Centro_no_suprime_la_de_este()
    {
        _gestiones.Lista.Add(new Gestion(_ana.Id, _otroCentro.Id, _formacion.Id));

        (await CalcularAsync()).Gestiones.Should().ContainSingle().Which.TipoDocumentoId.Should().Be(_formacion.Id);
    }

    [Fact]
    public async Task Una_Gestion_ya_Completada_no_suprime_la_nueva()
    {
        var completada = new Gestion(_ana.Id, _centro.Id, _formacion.Id);
        completada.Completar(DateTime.UtcNow);
        _gestiones.Lista.Add(completada);

        (await CalcularAsync()).Gestiones.Should().ContainSingle().Which.Motivo.Should().Be(MotivoGestionPrevista.Falta);
    }

    // ---- Documentos

    [Fact]
    public async Task Un_Documento_vigente_cubre_el_Tipo()
    {
        DocumentoDeAna(_formacion, VigenciaDocumento.NoCaduca);

        (await CalcularAsync()).Gestiones.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Documento_que_no_es_operativo_no_cubre_el_Tipo()
    {
        DocumentoDeAna(_formacion, VigenciaDocumento.NoCaduca).MarcarComoEliminado(Guid.NewGuid());

        (await CalcularAsync()).Gestiones.Should().ContainSingle().Which.Motivo.Should().Be(MotivoGestionPrevista.Falta);
    }

    [Fact]
    public async Task Una_renuncia_no_cubre_el_Tipo_y_nace_la_Gestion()
    {
        // La renuncia nunca es conforme ni sustituye al apto (CumplimientoDocumental.EsConforme, decisión del 2026-10-17).
        _formacion.EstablecerDatosAdicionales(DatosAdicionalesDocumento.Renuncia);
        var renuncia = DocumentoDeAna(_formacion, VigenciaDocumento.NoCaduca);
        renuncia.EstablecerDatosAdicionales(_formacion, horas: null, nivel: null, esRenuncia: true);

        (await CalcularAsync()).Gestiones.Should().ContainSingle().Which.Motivo.Should().Be(MotivoGestionPrevista.Falta);
    }

    // ---- Tipos y configuración del Centro

    [Fact]
    public async Task Un_Tipo_archivado_no_da_Gestion_aunque_el_Centro_lo_incluya()
    {
        var archivado = Tipo("Carné antiguo", RequisitoDocumental.Si);
        archivado.Archivar(sustituto: null);
        _tipos.ListaTiposDocumento.Add(archivado);
        _tipos.ListaTiposDocumentoCentros.Add(new TipoDocumentoCentro(archivado.Id, _centro.Id, incluido: true));

        (await CalcularAsync()).Gestiones.Select(g => g.TipoDocumentoId).Should().Equal(_formacion.Id);
    }

    [Fact]
    public async Task La_fila_explicita_del_Centro_exige_un_Tipo_que_de_serie_no_se_pide()
    {
        var opcional = Tipo("Carné de carretillero", RequisitoDocumental.No);
        _tipos.ListaTiposDocumento.Add(opcional);
        _tipos.ListaTiposDocumentoCentros.Add(new TipoDocumentoCentro(opcional.Id, _centro.Id, incluido: true));

        (await CalcularAsync()).Gestiones.Select(g => g.TipoDocumentoId).Should().BeEquivalentTo([_formacion.Id, opcional.Id]);
    }

    [Fact]
    public async Task Un_Tipo_de_ambito_Empresa_no_da_Gestion_al_Trabajador_aunque_sea_obligatorio_y_el_Centro_lo_incluya()
    {
        var deEmpresa = new TipoDocumentoEntidad(
            "Seguro de responsabilidad civil", vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 2,
            AmbitoAplicacion.Empresa, RequisitoDocumental.Si);
        _tipos.ListaTiposDocumento.Add(deEmpresa);
        _tipos.ListaTiposDocumentoCentros.Add(new TipoDocumentoCentro(deEmpresa.Id, _centro.Id, incluido: true));

        (await CalcularAsync()).Gestiones.Should().ContainSingle().Which.TipoDocumentoId.Should().Be(_formacion.Id);
    }

    [Fact]
    public async Task La_fila_de_otro_Centro_no_cambia_lo_que_exige_este()
    {
        _tipos.ListaTiposDocumentoCentros.Add(new TipoDocumentoCentro(_formacion.Id, _otroCentro.Id, incluido: false));

        (await CalcularAsync()).Gestiones.Should().ContainSingle().Which.TipoDocumentoId.Should().Be(_formacion.Id);
    }

    [Fact]
    public async Task La_periodicidad_especial_del_Centro_se_carga_y_manda_sobre_la_fecha_del_Documento()
    {
        // Vence en 2027, pero este Centro lo pide renovado cada 6 meses desde la emisión: para él vence el 2026-07-01.
        DocumentoDeAna(_formacion, VigenciaDocumento.VenceEl(new DateOnly(2027, 6, 1)), emision: new DateOnly(2026, 1, 1));
        _tipos.ListaTiposDocumentoCentros.Add(new TipoDocumentoCentro(_formacion.Id, _centro.Id, incluido: true, periodicidadEspecialMeses: 6));

        (await CalcularAsync()).Gestiones.Should().ContainSingle().Which.Motivo.Should().Be(MotivoGestionPrevista.CaducaAntesDelFin);
    }

    [Fact]
    public async Task Un_Centro_sin_gestion_CAE_no_da_Gestiones_pero_si_asigna()
    {
        _centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);

        var plan = await CalcularAsync();

        plan.Gestiones.Should().BeEmpty();
        plan.Asignaciones.Select(a => a.TrabajadorId).Should().Equal(_ana.Id);
    }

    private sealed class GestionesFalsas : IGestionesQueryContext
    {
        public List<Gestion> Lista { get; } = [];

        public IQueryable<Gestion> Gestiones => new TestAsyncQueryable<Gestion>(Lista.AsQueryable());
        public IQueryable<GestionAutomatica> GestionesAutomaticas =>
            new TestAsyncQueryable<GestionAutomatica>(Array.Empty<GestionAutomatica>().AsQueryable());
        public IQueryable<PasoGestionAutomatica> PasosGestionAutomatica =>
            new TestAsyncQueryable<PasoGestionAutomatica>(Array.Empty<PasoGestionAutomatica>().AsQueryable());
    }
}
