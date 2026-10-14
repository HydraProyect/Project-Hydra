using CaeManager.Application.Asignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.SituacionEnCentro;
using CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConActividadDeSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConCumplimientoDeSubcontrata;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Subcontratas;

public class SubcontrataCentrosCumplimientoFase2Tests
{
    private readonly Guid _empresa = Guid.NewGuid();
    private readonly Centro _centro = new(Guid.NewGuid(), Guid.NewGuid(), "Centro Uno");
    private readonly AsignacionesFalso _asignaciones = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly ConfiguracionQueryContextFalso _configuracion = new();
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly Trabajador _ana;
    private readonly TipoDocumento _tipo = new("Formación", null, false, 0, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);

    public SubcontrataCentrosCumplimientoFase2Tests()
    {
        _ana = Trabajador.DeSubcontrata(_empresa, "Ana", "Alba", "60005301A");
        var bea = Trabajador.DeSubcontrata(_empresa, "Bea", "Bueno", "60005302G");
        var otra = Trabajador.DeSubcontrata(Guid.NewGuid(), "Otra", "Empresa", "60005303M");
        _trabajadores.ListaTrabajadores.AddRange([_ana, bea, otra]);
        foreach (var t in _trabajadores.ListaTrabajadores)
            _asignaciones.Lista.Add(new(t.Id, _centro.Id, DiaDeNegocio.Hoy().AddDays(-5)));
        _centros.ListaCentros.Add(_centro);
        _tipos.ListaTiposDocumento.Add(_tipo);
        _configuracion.ListaParametrosSistema.Add(new(30, 7));
        _documentos.ListaDocumentos.Add(Documento.DeTrabajador(_ana.Id, _tipo.Id, DiaDeNegocio.Hoy(), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddDays(90))));
    }
    private ObtenerAsignacionesDocumentacionPorCentroQueryHandler Lector(IAlcanceDatosService? alcance = null) =>
        new(_asignaciones, _trabajadores, _tipos, _documentos, _configuracion, _centros, alcance ?? new AlcanceDatosServiceFalso(), new SituacionVacia());
    private Task<IReadOnlyList<TrabajadorAsignacionDocumentacionDto>> Leer(IAlcanceDatosService? alcance = null) =>
        Lector(alcance).Handle(new(_centro.Id, EmpresaEmpleadoraSubcontratistaId: _empresa), CancellationToken.None);

    [Fact]
    public async Task Solo_los_Trabajadores_de_esa_Empresa_aportan_documentos_y_fraccion()
    {
        var filas = await Leer();
        filas.Select(f => f.TrabajadorNombre).Should().Equal("Ana Alba", "Bea Bueno");
        filas.Sum(f => f.Cumplimiento.AlDia).Should().Be(1);
        filas.Sum(f => f.Cumplimiento.Requeridos).Should().Be(2);
    }
    [Fact]
    public async Task Sin_alcance_a_la_Relacion_de_subcontratacion_no_revela_Trabajadores()
    {
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [_centro.Id], subcontrataIdsVisibles: []);
        (await Leer(alcance)).Should().BeEmpty();
    }
    [Fact]
    public async Task Sin_alcance_al_Centro_no_revela_Trabajadores_ni_documentos()
    {
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [], subcontrataIdsVisibles: [_empresa]);
        (await Leer(alcance)).Should().BeEmpty();
    }
    [Fact]
    public async Task La_Asignacion_de_baja_no_aumenta_el_denominador()
    {
        _asignaciones.Lista[1].DarDeBaja(DiaDeNegocio.Hoy());
        (await Leer()).Should().ContainSingle().Which.Cumplimiento.Requeridos.Should().Be(1);
    }
    [Fact]
    public async Task Cero_requisitos_no_es_cien_por_ciento_y_la_exclusion_del_Centro_prevalece()
    {
        _tipos.ListaTiposDocumentoCentros.Add(new(_tipo.Id, _centro.Id, incluido: false));
        (await Leer()).Should().OnlyContain(f => f.Cumplimiento.Requeridos == 0 && f.Cumplimiento.Porcentaje == null);
    }
    [Fact]
    public async Task El_documento_efectivo_evita_que_la_version_vencida_reste_cumplimiento()
    {
        var hoy = DiaDeNegocio.Hoy();
        _documentos.ListaDocumentos.Insert(0, Documento.DeTrabajador(_ana.Id, _tipo.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-1))));
        (await Leer()).Single(f => f.TrabajadorId == _ana.Id).Cumplimiento.AlDia.Should().Be(1);
    }
    [Fact]
    public async Task La_tolerancia_se_muestra_pero_no_cuenta_el_vencido_como_al_dia()
    {
        var hoy = DiaDeNegocio.Hoy();
        _documentos.ListaDocumentos.Clear();
        _documentos.ListaDocumentos.Add(Documento.DeTrabajador(_ana.Id, _tipo.Id, hoy.AddMonths(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-1))));
        _tipos.ListaTiposDocumentoCentros.Add(new(_tipo.Id, _centro.Id, toleranciaDias: 10));
        var ana = (await Leer()).Single(f => f.TrabajadorId == _ana.Id);
        ana.Cumplimiento.AlDia.Should().Be(0);
        ana.Documentos.Should().ContainSingle().Which.Estado.Should().Be(EstadoDocumento.EnTolerancia);
    }
    [Fact]
    public async Task El_lector_agregado_suma_todos_los_pares_sin_el_tope_de_la_pagina()
    {
        for (var i = 0; i < 58; i++)
        {
            var numero = 60005400 + i;
            var dni = $"{numero:00000000}{"TRWAGMYFPDXBNJZSQVHLCKE"[numero % 23]}";
            var trabajador = Trabajador.DeSubcontrata(_empresa, $"Trabajador {i}", "De prueba", dni);
            _trabajadores.ListaTrabajadores.Add(trabajador);
            _asignaciones.Lista.Add(new(trabajador.Id, _centro.Id, DiaDeNegocio.Hoy()));
        }
        var mediator = new MediadorReal(Lector(), _centro);
        var resultado = await new ObtenerCentrosConCumplimientoDeSubcontrataQueryHandler(mediator, new AlcanceDatosServiceFalso()).Handle(new(_empresa), CancellationToken.None);
        resultado.Should().ContainSingle().Which.Cumplimiento.Should().Be(new FraccionCumplimiento(1, 60));
        resultado.Single().PeorEstado.Should().Be(EstadoDocumento.Faltante);
        mediator.Enviadas.OfType<ObtenerAsignacionesDocumentacionPorCentroQuery>().Should().ContainSingle().Which.EmpresaEmpleadoraSubcontratistaId.Should().Be(_empresa);
    }
    [Fact]
    public async Task El_lector_agregado_no_consulta_si_la_Empresa_esta_fuera_del_alcance()
    {
        var mediator = new MediadorReal(Lector(), _centro);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [_centro.Id], subcontrataIdsVisibles: []);
        (await new ObtenerCentrosConCumplimientoDeSubcontrataQueryHandler(mediator, alcance).Handle(new(_empresa), CancellationToken.None)).Should().BeEmpty();
        mediator.Enviadas.Should().BeEmpty();
    }
    [Fact]
    public async Task El_lector_agregado_no_lee_el_Centro_fuera_del_alcance_aunque_la_Empresa_sea_visible()
    {
        var mediator = new MediadorReal(Lector(), _centro);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [], subcontrataIdsVisibles: [_empresa]);
        (await new ObtenerCentrosConCumplimientoDeSubcontrataQueryHandler(mediator, alcance).Handle(new(_empresa), CancellationToken.None)).Should().BeEmpty();
        mediator.Enviadas.OfType<ObtenerAsignacionesDocumentacionPorCentroQuery>().Should().BeEmpty();
    }
    [Fact]
    public async Task Sin_el_parametro_nuevo_se_conserva_el_universo_completo_del_Centro()
    {
        (await Lector().Handle(new(_centro.Id), CancellationToken.None)).Should().HaveCount(3);
    }

    private sealed class AsignacionesFalso : IAsignacionesQueryContext
    {
        public List<Asignacion> Lista { get; } = [];
        public IQueryable<Asignacion> Asignaciones => new TestAsyncQueryable<Asignacion>(Lista.AsQueryable());
    }
    private sealed class SituacionVacia : ISituacionDocumentosEnCentrosService
    {
        public Task<SituacionDocumentosEnCentros> CargarAsync(IReadOnlyCollection<Guid> centros, IReadOnlyCollection<Guid> documentos,
            IReadOnlyCollection<Guid> trabajadores, IReadOnlyCollection<Guid> tipos, CancellationToken ct) => Task.FromResult(SituacionDocumentosEnCentros.Vacia);
    }
    private sealed class MediadorReal(ObtenerAsignacionesDocumentacionPorCentroQueryHandler lector, Centro centro) : IMediator
    {
        public List<object> Enviadas { get; } = [];
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
        {
            Enviadas.Add(request);
            object resultado = request switch
            {
                ObtenerCentrosConActividadDeSubcontrataQuery => new List<CentroConActividadDto> { new(centro.Id, centro.Nombre, "Cliente Uno", 2) },
                ObtenerAsignacionesDocumentacionPorCentroQuery q => await lector.Handle(q, ct),
                _ => throw new NotSupportedException()
            };
            return (TResponse)resultado;
        }
        public Task Send<TRequest>(TRequest request, CancellationToken ct = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken ct = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : INotification => Task.CompletedTask;
    }
}
