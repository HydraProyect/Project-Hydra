using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Proyectos.Queries.ObtenerVehiculosProyecto;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;

namespace CaeManager.Application.Tests.Proyectos;

/// <summary>
/// Vehículos de las Empresas que participan en el Proyecto: derivados de los técnicos activos, con el
/// mismo gate que Empresas del Proyecto y sin relación Vehículo-Proyecto en el modelo.
/// </summary>
public class ObtenerVehiculosProyectoTests
{
    private sealed class ProyectosContexto : IProyectosQueryContext
    {
        public List<Proyecto> Lista { get; } = [];
        public List<ProyectoTecnico> Tecnicos { get; } = [];
        public IQueryable<Proyecto> Proyectos => new TestAsyncQueryable<Proyecto>(Lista.AsQueryable());
        public IQueryable<ProyectoTecnico> ProyectosTecnicos => new TestAsyncQueryable<ProyectoTecnico>(Tecnicos.AsQueryable());
    }

    private sealed class VehiculosContexto : IVehiculosQueryContext
    {
        public List<Vehiculo> Lista { get; } = [];
        public IQueryable<Vehiculo> Vehiculos => new TestAsyncQueryable<Vehiculo>(Lista.AsQueryable());
    }

    private readonly ProyectosContexto _proyectos = new();
    private readonly VehiculosContexto _vehiculos = new();
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly Empresa _empleadora = new("Empresa empleadora del Proyecto");
    private readonly Centro _centro;
    private readonly Proyecto _proyecto;
    private readonly Vehiculo _furgoneta;

    public ObtenerVehiculosProyectoTests()
    {
        var clienteEmpresarialId = Guid.NewGuid();
        _centro = new Centro(clienteEmpresarialId, Guid.NewGuid(), "Centro del Proyecto");
        _proyecto = Proyecto.Crear(clienteEmpresarialId, _centro.Id, "Proyecto", new(2026, 10, 1), new(2026, 10, 31), null);
        _proyectos.Lista.Add(_proyecto);
        _centros.ListaCentros.Add(_centro);
        _empresas.ListaEmpresas.Add(_empleadora);
        var tecnico = Trabajador.DeEmpresa(_empleadora.Id, "Ana", "Alba", "60005002A");
        _trabajadores.ListaTrabajadores.Add(tecnico);
        _proyectos.Tecnicos.Add(new ProyectoTecnico(_proyecto.Id, tecnico.Id, new(2026, 10, 1)));
        _furgoneta = Vehiculo.DeEmpresa(_empleadora.Id, "Furgoneta 1", "Ford Transit", "1234ABC");
        _vehiculos.Lista.Add(_furgoneta);
    }

    private Task<IReadOnlyList<VehiculoProyectoDto>> VehiculosAsync(IAlcanceDatosService? alcance = null) =>
        new ObtenerVehiculosProyectoQueryHandler(_proyectos, _trabajadores, _empresas, _centros, _vehiculos,
            alcance ?? new AlcanceDatosServiceFalso())
            .Handle(new(_proyecto.Id), CancellationToken.None);

    private AlcanceDatosServiceFalso Alcance(
        IReadOnlyList<Guid>? empresas = null, IReadOnlyList<Guid>? subcontratas = null, IReadOnlyList<Guid>? vehiculos = null) =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: [_proyecto.ClienteId], centroIdsVisibles: [_centro.Id],
            empresaIdsVisibles: empresas ?? [_empleadora.Id], subcontrataIdsVisibles: subcontratas ?? [_empleadora.Id],
            vehiculoIdsVisibles: vehiculos ?? [_furgoneta.Id]);

    [Fact]
    public async Task Devuelve_los_vehiculos_de_la_Empresa_empleadora_de_un_tecnico_activo_con_su_dueno()
    {
        var fila = (await VehiculosAsync()).Should().ContainSingle().Which;
        fila.Id.Should().Be(_furgoneta.Id);
        fila.EmpleadorId.Should().Be(_empleadora.Id);
        fila.EmpleadorNombre.Should().Be("Empresa empleadora del Proyecto");
        fila.NumeroPlaca.Should().Be("1234ABC");
    }

    [Fact]
    public async Task Un_vehiculo_de_una_Empresa_que_no_participa_no_aparece()
    {
        _vehiculos.Lista.Add(Vehiculo.DeEmpresa(Guid.NewGuid(), "Ajena", "Renault Master", "7788LLK"));
        (await VehiculosAsync()).Should().ContainSingle().Which.Id.Should().Be(_furgoneta.Id);
    }

    [Fact]
    public async Task Un_tecnico_de_baja_no_hace_participar_a_su_Empresa()
    {
        _proyectos.Tecnicos[0].DarDeBaja(new(2026, 10, 2));
        (await VehiculosAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_tecnico_de_otro_Proyecto_no_hace_participar_a_su_Empresa()
    {
        _proyectos.Tecnicos.Clear();
        _proyectos.Tecnicos.Add(new ProyectoTecnico(Guid.NewGuid(), _trabajadores.ListaTrabajadores[0].Id, new(2026, 10, 1)));
        (await VehiculosAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_alcance_al_Cliente_empresarial_del_Proyecto_no_se_revela_ningun_vehiculo()
    {
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [],
            centroIdsVisibles: [_centro.Id], empresaIdsVisibles: [_empleadora.Id], vehiculoIdsVisibles: [_furgoneta.Id]);
        (await VehiculosAsync(alcance)).Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_alcance_al_Centro_no_se_revela_ningun_vehiculo()
    {
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [_proyecto.ClienteId],
            centroIdsVisibles: [], empresaIdsVisibles: [_empleadora.Id], vehiculoIdsVisibles: [_furgoneta.Id]);
        (await VehiculosAsync(alcance)).Should().BeEmpty();
    }

    [Fact]
    public async Task La_Empresa_fuera_del_alcance_no_aporta_vehiculos()
    {
        (await VehiculosAsync(Alcance(empresas: []))).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_vehiculo_fuera_del_alcance_de_vehiculos_no_aparece_aunque_su_Empresa_sea_visible()
    {
        (await VehiculosAsync(Alcance(vehiculos: []))).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Proyecto_inexistente_no_revela_vehiculos()
    {
        _proyectos.Lista.Clear();
        (await VehiculosAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task La_relacion_Subcontrata_aporta_los_vehiculos_de_la_Subcontrata_y_exige_su_alcance()
    {
        var trabajador = Trabajador.DeSubcontrata(_empleadora.Id, "Leo", "Mas", "11111111H");
        _trabajadores.ListaTrabajadores.Clear();
        _trabajadores.ListaTrabajadores.Add(trabajador);
        _proyectos.Tecnicos.Clear();
        _proyectos.Tecnicos.Add(new ProyectoTecnico(_proyecto.Id, trabajador.Id, new(2026, 10, 1)));
        var deSubcontrata = Vehiculo.DeSubcontrata(_empleadora.Id, "Grua", "Liebherr", "9999ZZZ");
        _vehiculos.Lista.Add(deSubcontrata);

        // El vehículo de la Empresa directa no entra por la ruta de Subcontrata, y viceversa.
        var permitido = Alcance(vehiculos: [_furgoneta.Id, deSubcontrata.Id]);
        (await VehiculosAsync(permitido)).Should().ContainSingle().Which.Id.Should().Be(deSubcontrata.Id);
        (await VehiculosAsync(Alcance(subcontratas: [], vehiculos: [_furgoneta.Id, deSubcontrata.Id]))).Should().BeEmpty();
    }

    [Fact]
    public async Task La_Empresa_empleadora_directa_no_aporta_sus_vehiculos_por_la_ruta_de_Subcontrata()
    {
        // La misma Empresa con un vehículo registrado como de Subcontrata: solo entra si participa como Subcontrata.
        _vehiculos.Lista.Add(Vehiculo.DeSubcontrata(_empleadora.Id, "Grua de la relacion", "Liebherr", "9999ZZZ"));
        var filas = await VehiculosAsync(Alcance(vehiculos: [_furgoneta.Id, _vehiculos.Lista[1].Id]));
        filas.Should().ContainSingle().Which.Id.Should().Be(_furgoneta.Id);
    }

    [Fact]
    public async Task Se_ordenan_por_nombre()
    {
        _vehiculos.Lista.Add(Vehiculo.DeEmpresa(_empleadora.Id, "Camión", "Iveco", "0001AAA"));
        var filas = await VehiculosAsync();
        filas.Select(f => f.Nombre).Should().Equal("Camión", "Furgoneta 1");
    }

    [Fact]
    public void El_DTO_no_expone_estado_documental_ni_asignacion()
    {
        typeof(VehiculoProyectoDto).GetProperty("Estado").Should().BeNull();
        typeof(VehiculoProyectoDto).GetProperty("Cumplimiento").Should().BeNull();
        typeof(VehiculoProyectoDto).GetProperty("FechaAlta").Should().BeNull();
    }
}
