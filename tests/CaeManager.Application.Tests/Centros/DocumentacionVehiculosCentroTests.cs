using CaeManager.Application.Centros.Queries.ObtenerDocumentacionVehiculosDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerVehiculosConActividadDeCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// Vista «Vehículos» de Centro 360: los Vehículos salen de ObtenerVehiculosConActividadDeCentroQuery, con el alcance de
/// los Trabajadores del Centro (Centro visible por rol y cartera), y la Empresa contratista de una Subcontrata solo se
/// nombra si el usuario ve esa Empresa.
/// </summary>
public class DocumentacionVehiculosCentroTests
{
    private sealed class VehiculosContexto : IVehiculosQueryContext
    {
        public List<Vehiculo> Lista { get; } = [];
        public IQueryable<Vehiculo> Vehiculos => new TestAsyncQueryable<Vehiculo>(Lista.AsQueryable());
    }

    private readonly CentrosQueryContextFalso _centros = new();
    private readonly AsignacionesQueryContextFalso _asignaciones = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly ConfiguracionQueryContextFalso _configuracion = new();
    private readonly VehiculosContexto _vehiculos = new();
    private readonly Empresa _contratista = new("Contratista del Centro");
    private readonly Empresa _subcontrata = new("Subcontrata Montajes");
    private readonly Centro _centro;
    private readonly Vehiculo _furgoneta;
    private readonly Vehiculo _grua;

    public DocumentacionVehiculosCentroTests()
    {
        _centro = new(Guid.NewGuid(), _contratista.Id, "Centro Uno");
        _centros.ListaCentros.Add(_centro);
        _empresas.ListaEmpresas.AddRange([_contratista, _subcontrata]);
        _empresas.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(_subcontrata.Id, _contratista.Id, DateTime.UtcNow.AddYears(-1)));
        var propio = Trabajador.DeEmpresa(_contratista.Id, "Ana", "Alba", "60005301A");
        var subcontratado = Trabajador.DeSubcontrata(_subcontrata.Id, "Leo", "Mas", "11111111H");
        _trabajadores.ListaTrabajadores.AddRange([propio, subcontratado]);
        _asignaciones.ListaAsignaciones.Add(new(propio.Id, _centro.Id, DiaDeNegocio.Hoy().AddDays(-5)));
        _asignaciones.ListaAsignaciones.Add(new(subcontratado.Id, _centro.Id, DiaDeNegocio.Hoy().AddDays(-5)));
        _furgoneta = Vehiculo.DeEmpresa(_contratista.Id, "Renault Kangoo", "Kangoo", "4821KLM");
        _grua = Vehiculo.DeSubcontrata(_subcontrata.Id, "Citroen Berlingo", "Berlingo", "7310JHB");
        _vehiculos.Lista.AddRange([_furgoneta, _grua]);
        _configuracion.ListaParametrosSistema.Add(new(30, 7));
    }

    private sealed class Mediador(Func<ObtenerVehiculosConActividadDeCentroQuery, Task<IReadOnlyList<VehiculoConActividadDto>>> vehiculos) : IMediator
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            return request is ObtenerVehiculosConActividadDeCentroQuery consulta
                ? (TResponse)(object)await vehiculos(consulta)
                : throw new NotSupportedException(request.GetType().Name);
        }
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private Task<IReadOnlyList<VehiculoDocumentacionCentroDto>> Leer(IAlcanceDatosService? alcance = null)
    {
        alcance ??= new AlcanceDatosServiceFalso();
        var conActividad = new ObtenerVehiculosConActividadDeCentroQueryHandler(_asignaciones, _empresas, _trabajadores, _vehiculos, alcance);
        var mediador = new Mediador(c => conActividad.Handle(c, CancellationToken.None));
        return new ObtenerDocumentacionVehiculosDeCentroQueryHandler(mediador, _documentos, _tipos, _configuracion,
                new SituacionDocumentosSinDatos(), _centros, _asignaciones, _trabajadores, _empresas, alcance)
            .Handle(new(_centro.Id), CancellationToken.None);
    }

    [Fact]
    public async Task Un_rol_con_cartera_que_no_incluye_el_Centro_no_ve_ningun_Vehiculo_ni_sus_documentos()
    {
        _documentos.ListaDocumentos.Add(Documento.DeVehiculo(_furgoneta.Id, Guid.NewGuid(), DiaDeNegocio.Hoy(), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddDays(90))));
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [], empresaIdsVisibles: [_contratista.Id],
            subcontrataIdsVisibles: [_subcontrata.Id]);
        (await Leer(alcance)).Should().BeEmpty();
    }

    [Fact]
    public async Task Con_el_Centro_en_cartera_ve_los_mismos_Vehiculos_que_la_consulta_de_actividad_y_ninguno_mas()
    {
        var ajena = new Empresa("Empresa sin Asignación activa");
        _empresas.ListaEmpresas.Add(ajena);
        _vehiculos.Lista.Add(Vehiculo.DeEmpresa(ajena.Id, "Iveco", "Daily", "0001AAA"));
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [_centro.Id], empresaIdsVisibles: [_contratista.Id]);

        var filas = await Leer(alcance);

        filas.Select(v => v.VehiculoId).Should().BeEquivalentTo([_furgoneta.Id, _grua.Id]);
    }

    [Fact]
    public async Task El_Vehiculo_de_una_Subcontrata_lleva_su_Subcontrata_y_la_Empresa_contratista_de_la_relacion()
    {
        var filas = await Leer();

        filas.Single(v => v.VehiculoId == _furgoneta.Id).SubcontrataId.Should().BeNull();
        var grua = filas.Single(v => v.VehiculoId == _grua.Id);
        grua.SubcontrataId.Should().Be(_subcontrata.Id);
        grua.EmpleadorNombre.Should().Be(_subcontrata.RazonSocial);
        grua.EmpresaContratistaNombre.Should().Be(_contratista.RazonSocial);
    }

    [Fact]
    public async Task La_Empresa_contratista_fuera_de_la_cartera_no_se_nombra()
    {
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [_centro.Id], empresaIdsVisibles: []);

        var grua = (await Leer(alcance)).Single(v => v.VehiculoId == _grua.Id);

        grua.EmpresaContratistaNombre.Should().BeNull();
    }

    [Fact]
    public async Task Entre_varias_contratistas_vigentes_se_nombra_la_que_esta_en_el_Centro()
    {
        var otra = new Empresa("Otra contratista ajena al Centro");
        _empresas.ListaEmpresas.Add(otra);
        _empresas.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(_subcontrata.Id, otra.Id, DateTime.UtcNow.AddYears(-1)));

        var grua = (await Leer()).Single(v => v.VehiculoId == _grua.Id);

        grua.EmpresaContratistaNombre.Should().Be(_contratista.RazonSocial);
    }

    [Fact]
    public async Task Cada_tipo_cuenta_una_vez_con_el_documento_efectivo_y_lo_peor_primero()
    {
        var hoy = DiaDeNegocio.Hoy();
        var itv = Guid.NewGuid();
        var seguro = Guid.NewGuid();
        _documentos.ListaDocumentos.AddRange([
            Documento.DeVehiculo(_furgoneta.Id, itv, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-1))),
            Documento.DeVehiculo(_furgoneta.Id, itv, hoy, VigenciaDocumento.VenceEl(hoy.AddDays(200))),
            Documento.DeVehiculo(_furgoneta.Id, seguro, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-3)))]);

        var furgoneta = (await Leer()).Single(v => v.VehiculoId == _furgoneta.Id);

        furgoneta.Documentos.Should().HaveCount(2);
        furgoneta.Documentos[0].TipoDocumentoId.Should().Be(seguro);
        furgoneta.Documentos[0].Estado.Should().Be(EstadoDocumento.Vencido);
        furgoneta.Documentos.Single(d => d.TipoDocumentoId == itv).Estado.Should().Be(EstadoDocumento.Vigente);
    }
}
