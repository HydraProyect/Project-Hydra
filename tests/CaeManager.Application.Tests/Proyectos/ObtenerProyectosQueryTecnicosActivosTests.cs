using CaeManager.Application.Proyectos;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;

namespace CaeManager.Application.Tests.Proyectos;

/// <summary>
/// La fila del listado de Proyectos enseña el recuento de técnicos activos y, al pasar el cursor,
/// sus nombres: la consulta de la lista los trae en una sola lectura para todos los proyectos.
/// </summary>
public class ObtenerProyectosQueryTecnicosActivosTests
{
    private static readonly Guid ClienteId = Guid.NewGuid();
    private static readonly Guid EmpresaId = Guid.NewGuid();
    private static readonly DateOnly Inicio = new(2026, 3, 12);

    private sealed class ProyectosContextoAsincrono : IProyectosQueryContext
    {
        public List<Proyecto> ListaProyectos { get; } = [];
        public List<ProyectoTecnico> ListaProyectosTecnicos { get; } = [];

        public IQueryable<Proyecto> Proyectos => new TestAsyncQueryable<Proyecto>(ListaProyectos.AsQueryable());
        public IQueryable<ProyectoTecnico> ProyectosTecnicos => new TestAsyncQueryable<ProyectoTecnico>(ListaProyectosTecnicos.AsQueryable());
    }

    private readonly CentrosQueryContextFalso _centros = new();
    private readonly ProyectosContextoAsincrono _proyectos = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();

    private ObtenerProyectosQueryHandler Handler() =>
        new(_centros, _proyectos, _trabajadores, new AlcanceDatosServiceFalso());

    private Proyecto NuevoProyecto(string nombre)
    {
        var centro = new Centro(ClienteId, EmpresaId, "Sede Sevilla");
        _centros.ListaCentros.Add(centro);
        var proyecto = Proyecto.Crear(ClienteId, centro.Id, nombre, Inicio, null, null);
        _proyectos.ListaProyectos.Add(proyecto);
        return proyecto;
    }

    private Trabajador NuevoTrabajador(string nombre, string apellidos, string dni)
    {
        var trabajador = Trabajador.DeEmpresa(EmpresaId, nombre, apellidos, dni);
        _trabajadores.ListaTrabajadores.Add(trabajador);
        return trabajador;
    }

    [Fact]
    public async Task Cada_proyecto_trae_sus_tecnicos_activos_como_Nombre_Apellidos_y_no_los_de_baja_ni_los_de_otro_proyecto()
    {
        var reforma = NuevoProyecto("Reforma nave");
        var incendios = NuevoProyecto("Instalación contra incendios");
        var paula = NuevoTrabajador("Paula", "Campos Lara", "60005002A");
        var oscar = NuevoTrabajador("Óscar", "Ferrer Pons", "60005104J");
        var carla = NuevoTrabajador("Carla", "Molina Ríos", "60005105Z");

        _proyectos.ListaProyectosTecnicos.Add(new ProyectoTecnico(reforma.Id, paula.Id, Inicio));
        _proyectos.ListaProyectosTecnicos.Add(new ProyectoTecnico(reforma.Id, carla.Id, Inicio));
        var deBaja = new ProyectoTecnico(reforma.Id, oscar.Id, Inicio);
        deBaja.DarDeBaja(Inicio.AddDays(10));
        _proyectos.ListaProyectosTecnicos.Add(deBaja);
        _proyectos.ListaProyectosTecnicos.Add(new ProyectoTecnico(incendios.Id, oscar.Id, Inicio));

        var lista = await Handler().Handle(new ObtenerProyectosQuery(ClienteId), CancellationToken.None);

        lista.Single(p => p.Id == reforma.Id).TecnicosActivos
            .Should().Equal(
                new TecnicoActivoListaDto(carla.Id, "Carla Molina Ríos"),
                new TecnicoActivoListaDto(paula.Id, "Paula Campos Lara"));
        lista.Single(p => p.Id == incendios.Id).TecnicosActivos
            .Should().Equal(new TecnicoActivoListaDto(oscar.Id, "Óscar Ferrer Pons"));
    }

    [Fact]
    public async Task Un_tecnico_cuyo_Trabajador_no_es_visible_no_se_nombra()
    {
        var reforma = NuevoProyecto("Reforma nave");
        // Alta de técnico hacia un Trabajador que la consulta de Trabajadores no devuelve
        // (baja lógica o fuera de los filtros de consulta): ni se cuenta ni se nombra.
        _proyectos.ListaProyectosTecnicos.Add(new ProyectoTecnico(reforma.Id, Guid.NewGuid(), Inicio));

        var lista = await Handler().Handle(new ObtenerProyectosQuery(ClienteId), CancellationToken.None);

        lista.Single().TecnicosActivos.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_proyecto_sin_tecnicos_trae_la_lista_vacia()
    {
        NuevoProyecto("Reforma nave");

        var lista = await Handler().Handle(new ObtenerProyectosQuery(ClienteId), CancellationToken.None);

        lista.Single().TecnicosActivos.Should().BeEmpty();
    }
}
