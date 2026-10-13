using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionEmpresasDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

public class DocumentacionEmpresasCentroFase2Tests
{
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly AsignacionesFalso _asignaciones = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly ConfiguracionQueryContextFalso _configuracion = new();
    private readonly RequisitosFalso _mediador = new();
    private readonly Empresa _empresa = new("Empleadora Uno");
    private readonly Centro _centro;
    private readonly Guid _tipo = Guid.NewGuid();

    public DocumentacionEmpresasCentroFase2Tests()
    {
        _centro = new(Guid.NewGuid(), _empresa.Id, "Centro Uno");
        _centros.ListaCentros.Add(_centro);
        _empresas.ListaEmpresas.Add(_empresa);
        var trabajador = Trabajador.DeEmpresa(_empresa.Id, "Ana", "Alba", "60005301A");
        _trabajadores.ListaTrabajadores.Add(trabajador);
        _asignaciones.Lista.Add(new(trabajador.Id, _centro.Id, DiaDeNegocio.Hoy().AddDays(-5)));
        _configuracion.ListaParametrosSistema.Add(new(30, 7));
        _mediador.Filas = [new(_tipo, "Seguro", AmbitoAplicacion.Empresa, true, true, true, null, false, null, null)];
    }
    private Task<IReadOnlyList<DocumentacionEmpresaCentroDto>> Leer(IAlcanceDatosService? alcance = null) =>
        new ObtenerDocumentacionEmpresasDeCentroQueryHandler(_centros, _asignaciones, _trabajadores, _empresas,
            _documentos, _configuracion, alcance ?? new AlcanceDatosServiceFalso(), _mediador).Handle(new(_centro.Id), CancellationToken.None);

    [Fact]
    public async Task Fuera_del_alcance_del_Centro_no_revela_Empresa_ni_documentos_ni_consulta_requisitos()
    {
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [], empresaIdsVisibles: [_empresa.Id]);
        (await Leer(alcance)).Should().BeEmpty();
        _mediador.Peticiones.Should().BeEmpty();
    }
    [Fact]
    public async Task Fuera_del_alcance_de_la_Empresa_no_revela_su_nombre_ni_documentos()
    {
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [_centro.Id], empresaIdsVisibles: []);
        (await Leer(alcance)).Should().BeEmpty();
    }
    [Fact]
    public async Task Sin_gestion_CAE_no_exige_documentacion_ni_carga_requisitos()
    {
        _centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        (await Leer()).Should().BeEmpty();
        _mediador.Peticiones.Should().BeEmpty();
    }
    [Fact]
    public async Task Solo_las_Asignaciones_activas_del_Centro_aportan_Empresa()
    {
        var otra = new Empresa("Empresa con Asignación de baja");
        _empresas.ListaEmpresas.Add(otra);
        var trabajador = Trabajador.DeEmpresa(otra.Id, "Ana", "Alba", "60005301A");
        _trabajadores.ListaTrabajadores.Add(trabajador);
        _asignaciones.Lista.Clear();
        var baja = new Asignacion(trabajador.Id, _centro.Id, DiaDeNegocio.Hoy().AddDays(-1));
        baja.DarDeBaja(DiaDeNegocio.Hoy());
        _asignaciones.Lista.Add(baja);
        _asignaciones.Lista.Add(new(trabajador.Id, Guid.NewGuid(), DiaDeNegocio.Hoy()));
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [_centro.Id], empresaIdsVisibles: [otra.Id]);
        (await Leer(alcance)).Should().BeEmpty();
    }
    [Fact]
    public async Task Ausente_y_exclusion_de_tipo_no_inventan_un_cien_por_ciento()
    {
        var empresa = (await Leer()).Should().ContainSingle().Which;
        empresa.Documentos.Should().ContainSingle().Which.Estado.Should().Be(EstadoDocumento.Faltante);
        empresa.Cumplimiento.AlDia.Should().Be(0);
        _mediador.Filas = _mediador.Filas.Select(r => r with { Aplica = false }).ToList();
        empresa = (await Leer()).Should().ContainSingle().Which;
        empresa.Documentos.Should().BeEmpty();
        empresa.Cumplimiento.Porcentaje.Should().BeNull();
    }
    [Fact]
    public async Task Un_tipo_de_Trabajador_no_se_exige_a_la_Empresa()
    {
        _mediador.Filas = _mediador.Filas.Select(r => r with { AmbitoAplicacion = AmbitoAplicacion.Trabajador }).ToList();
        (await Leer()).Should().ContainSingle().Which.Documentos.Should().BeEmpty();
    }
    [Fact]
    public async Task Un_documento_sustituido_no_cuenta_y_el_efectivo_prevalece_sobre_la_version_vencida()
    {
        var hoy = DiaDeNegocio.Hoy();
        var antiguo = Documento.DeEmpresa(_empresa.Id, _tipo, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-1)));
        var vigente = Documento.DeEmpresa(_empresa.Id, _tipo, hoy, VigenciaDocumento.VenceEl(hoy.AddDays(90)));
        _documentos.ListaDocumentos.AddRange([antiguo, vigente]);
        (await Leer()).Should().ContainSingle().Which.Cumplimiento.AlDia.Should().Be(1);
        vigente.SustituirPor(Documento.DeEmpresa(_empresa.Id, _tipo, hoy, VigenciaDocumento.VenceEl(hoy.AddDays(180))), MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
        (await Leer()).Should().ContainSingle().Which.Documentos.Should().ContainSingle().Which.Estado.Should().Be(EstadoDocumento.Vencido);
    }
    [Fact]
    public async Task La_tolerancia_se_presenta_pero_el_vencido_no_cuenta_como_al_dia()
    {
        var hoy = DiaDeNegocio.Hoy();
        _mediador.Filas = _mediador.Filas.Select(r => r with { ToleranciaDias = 10 }).ToList();
        _documentos.ListaDocumentos.Add(Documento.DeEmpresa(_empresa.Id, _tipo, hoy.AddMonths(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-1))));
        var empresa = (await Leer()).Should().ContainSingle().Which;
        empresa.Cumplimiento.AlDia.Should().Be(0);
        empresa.Documentos.Should().ContainSingle().Which.Estado.Should().Be(EstadoDocumento.EnTolerancia);
    }
    [Fact]
    public async Task La_relacion_de_subcontratacion_conserva_su_ruta_y_su_alcance()
    {
        var trabajador = Trabajador.DeSubcontrata(_empresa.Id, "Ana", "Alba", "60005301A");
        _trabajadores.ListaTrabajadores.Clear();
        _trabajadores.ListaTrabajadores.Add(trabajador);
        _asignaciones.Lista.Clear();
        _asignaciones.Lista.Add(new(trabajador.Id, _centro.Id, DiaDeNegocio.Hoy()));
        var negado = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [_centro.Id], subcontrataIdsVisibles: []);
        (await Leer(negado)).Should().BeEmpty();
        var permitido = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [_centro.Id], subcontrataIdsVisibles: [_empresa.Id]);
        (await Leer(permitido)).Should().ContainSingle().Which.DesdeRelacionSubcontrata.Should().BeTrue();
    }

    [Fact]
    public async Task La_lista_documental_se_ordena_del_peor_estado_al_mejor()
    {
        var hoy = DiaDeNegocio.Hoy();
        var segundo = Guid.NewGuid();
        _mediador.Filas = [.. _mediador.Filas, new(segundo, "Pendiente", AmbitoAplicacion.Empresa, true, true, true, null, false, null, null)];
        _documentos.ListaDocumentos.Add(Documento.DeEmpresa(_empresa.Id, _tipo, hoy, VigenciaDocumento.VenceEl(hoy.AddDays(90))));
        (await Leer()).Should().ContainSingle().Which.Documentos.Select(d => d.Estado).Should().Equal(EstadoDocumento.Faltante, EstadoDocumento.Vigente);
    }

    [Fact]
    public async Task La_Empresa_configurada_conserva_sus_exigidos_sin_Asignaciones_activas()
    {
        _asignaciones.Lista.Clear();
        var empresa = (await Leer()).Should().ContainSingle().Which;
        empresa.EmpresaId.Should().Be(_empresa.Id);
        empresa.Documentos.Should().ContainSingle().Which.Estado.Should().Be(EstadoDocumento.Faltante);
    }

    private sealed class AsignacionesFalso : IAsignacionesQueryContext
    {
        public List<Asignacion> Lista { get; } = [];
        public IQueryable<Asignacion> Asignaciones => new TestAsyncQueryable<Asignacion>(Lista.AsQueryable());
    }
    private sealed class RequisitosFalso : IMediator
    {
        public IReadOnlyList<DocumentacionRequeridaCentroDto> Filas { get; set; } = [];
        public List<object> Peticiones { get; } = [];
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Peticiones.Add(request);
            return Task.FromResult((TResponse)(object)Filas);
        }
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }
}
