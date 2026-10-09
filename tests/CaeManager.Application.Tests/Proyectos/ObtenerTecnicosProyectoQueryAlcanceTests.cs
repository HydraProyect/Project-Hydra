using CaeManager.Application.Proyectos;
using CaeManager.Application.Proyectos.Queries.ObtenerTecnicosProyecto;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;

namespace CaeManager.Application.Tests.Proyectos;

/// <summary>
/// Los técnicos de un Proyecto solo se nombran a quien ve el Proyecto: la consulta aplica por sí
/// misma el criterio de <c>ObtenerProyectoPorIdQuery</c> (el Cliente empresarial del proyecto dentro
/// del alcance), sin depender de que el componente haya pedido antes el detalle.
/// </summary>
public class ObtenerTecnicosProyectoQueryAlcanceTests
{
    private static readonly Guid ClienteId = Guid.NewGuid();
    private static readonly DateOnly Inicio = new(2026, 3, 12);

    private sealed class ProyectosContextoAsincrono : IProyectosQueryContext
    {
        public List<Proyecto> ListaProyectos { get; } = [];
        public List<ProyectoTecnico> ListaProyectosTecnicos { get; } = [];

        public IQueryable<Proyecto> Proyectos => new TestAsyncQueryable<Proyecto>(ListaProyectos.AsQueryable());
        public IQueryable<ProyectoTecnico> ProyectosTecnicos => new TestAsyncQueryable<ProyectoTecnico>(ListaProyectosTecnicos.AsQueryable());
    }

    private readonly ProyectosContextoAsincrono _proyectos = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly Proyecto _proyecto;
    private readonly Trabajador _tecnico;

    public ObtenerTecnicosProyectoQueryAlcanceTests()
    {
        _proyecto = Proyecto.Crear(ClienteId, Guid.NewGuid(), "Reforma nave", Inicio, null, null);
        _proyectos.ListaProyectos.Add(_proyecto);
        _tecnico = Trabajador.DeEmpresa(Guid.NewGuid(), "Paula", "Campos Lara", "60005002A");
        _trabajadores.ListaTrabajadores.Add(_tecnico);
        _proyectos.ListaProyectosTecnicos.Add(new ProyectoTecnico(_proyecto.Id, _tecnico.Id, Inicio));
    }

    private Task<IReadOnlyList<TecnicoProyectoDto>> PedirAsync(Guid proyectoId, AlcanceDatosServiceFalso alcance) =>
        new ObtenerTecnicosProyectoQueryHandler(_proyectos, _trabajadores, alcance)
            .Handle(new ObtenerTecnicosProyectoQuery(proyectoId), CancellationToken.None);

    [Fact]
    public async Task Sin_restriccion_de_alcance_se_nombran_los_tecnicos_del_proyecto()
    {
        var tecnicos = await PedirAsync(_proyecto.Id, new AlcanceDatosServiceFalso());

        tecnicos.Should().ContainSingle().Which.Should().Match<TecnicoProyectoDto>(
            t => t.TrabajadorId == _tecnico.Id && t.TrabajadorNombreCompleto == "Paula Campos Lara" && t.EstaActivo);
    }

    [Fact]
    public async Task Con_el_Cliente_empresarial_del_proyecto_dentro_del_alcance_se_nombran_los_tecnicos()
    {
        var tecnicos = await PedirAsync(
            _proyecto.Id, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [ClienteId]));

        tecnicos.Should().ContainSingle().Which.TrabajadorId.Should().Be(_tecnico.Id);
    }

    [Fact]
    public async Task Con_el_Cliente_empresarial_del_proyecto_fuera_del_alcance_no_se_nombra_a_nadie()
    {
        var tecnicos = await PedirAsync(
            _proyecto.Id, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]));

        tecnicos.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_ningun_Cliente_empresarial_en_el_alcance_no_se_nombra_a_nadie()
    {
        var tecnicos = await PedirAsync(
            _proyecto.Id, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: []));

        tecnicos.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_proyecto_que_no_existe_devuelve_la_lista_vacia()
    {
        var tecnicos = await PedirAsync(Guid.NewGuid(), new AlcanceDatosServiceFalso());

        tecnicos.Should().BeEmpty();
    }
}
