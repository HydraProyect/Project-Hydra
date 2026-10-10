using CaeManager.Application.Proyectos;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;

namespace CaeManager.Application.Tests.Proyectos;

/// <summary>
/// La fila del listado de Proyectos enseña el recuento de técnicos activos y, al pasar el cursor,
/// sus nombres: la consulta de la lista los trae en una sola lectura para los proyectos de la página.
/// </summary>
public class ObtenerProyectosQueryTecnicosActivosTests
{
    private static readonly Empresa ClienteEmpresarial = Empresa.CrearComoCliente("Obras del Sur S.L.", "B12345674", false, null, null);
    private static readonly Guid ClienteId = ClienteEmpresarial.Id;
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
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly ProyectosContextoAsincrono _proyectos = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();

    public ObtenerProyectosQueryTecnicosActivosTests() => _empresas.ListaEmpresas.Add(ClienteEmpresarial);

    private ObtenerProyectosQueryHandler Handler() =>
        new(_centros, _empresas, _proyectos, _trabajadores, new AlcanceDatosServiceFalso());

    private Proyecto NuevoProyecto(string nombre, DateOnly? inicio = null)
    {
        var centro = new Centro(ClienteId, EmpresaId, "Sede Sevilla");
        _centros.ListaCentros.Add(centro);
        var proyecto = Proyecto.Crear(ClienteId, centro.Id, nombre, inicio ?? Inicio, null, null);
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

        var lista = (await Handler().Handle(new ObtenerProyectosQuery(ClienteId), CancellationToken.None)).Elementos;

        lista.Single(p => p.Id == reforma.Id).TecnicosActivos
            .Should().Equal(
                new TecnicoActivoListaDto(carla.Id, "Carla Molina Ríos"),
                new TecnicoActivoListaDto(paula.Id, "Paula Campos Lara"));
        lista.Single(p => p.Id == incendios.Id).TecnicosActivos
            .Should().Equal(new TecnicoActivoListaDto(oscar.Id, "Óscar Ferrer Pons"));
    }

    [Fact]
    public async Task Un_tecnico_cuyo_Trabajador_no_devuelve_la_consulta_de_Trabajadores_no_se_nombra()
    {
        var reforma = NuevoProyecto("Reforma nave");
        // Alta de técnico hacia un Trabajador que la consulta de Trabajadores no devuelve: ni se
        // cuenta ni se nombra. Esto fija la unión; que los filtros de consulta de EF (Tenant
        // propietario, baja lógica) dejen fuera a un Trabajador no se observa en esta capa.
        _proyectos.ListaProyectosTecnicos.Add(new ProyectoTecnico(reforma.Id, Guid.NewGuid(), Inicio));

        var lista = (await Handler().Handle(new ObtenerProyectosQuery(ClienteId), CancellationToken.None)).Elementos;

        lista.Single().TecnicosActivos.Should().BeEmpty();
    }

    [Fact]
    public async Task Los_tecnicos_que_se_nombran_son_los_de_la_pagina_devuelta()
    {
        // Dos proyectos y página de uno: el técnico del que queda fuera no viaja con la respuesta.
        var reciente = NuevoProyecto("Reforma nave", Inicio.AddDays(1));
        var antiguo = NuevoProyecto("Instalación contra incendios", Inicio);
        var paula = NuevoTrabajador("Paula", "Campos Lara", "60005002A");
        var oscar = NuevoTrabajador("Óscar", "Ferrer Pons", "60005104J");
        _proyectos.ListaProyectosTecnicos.Add(new ProyectoTecnico(reciente.Id, paula.Id, Inicio));
        _proyectos.ListaProyectosTecnicos.Add(new ProyectoTecnico(antiguo.Id, oscar.Id, Inicio));

        var primera = await Handler().Handle(new ObtenerProyectosQuery(ClienteId, TamanoPagina: 1), CancellationToken.None);
        var segunda = await Handler().Handle(new ObtenerProyectosQuery(ClienteId, Pagina: 2, TamanoPagina: 1), CancellationToken.None);

        primera.Elementos.Should().ContainSingle().Which.TecnicosActivos
            .Should().Equal(new TecnicoActivoListaDto(paula.Id, "Paula Campos Lara"));
        segunda.Elementos.Should().ContainSingle().Which.TecnicosActivos
            .Should().Equal(new TecnicoActivoListaDto(oscar.Id, "Óscar Ferrer Pons"));
    }

    [Fact]
    public async Task Un_proyecto_sin_tecnicos_trae_la_lista_vacia()
    {
        NuevoProyecto("Reforma nave");

        var lista = (await Handler().Handle(new ObtenerProyectosQuery(ClienteId), CancellationToken.None)).Elementos;

        lista.Single().TecnicosActivos.Should().BeEmpty();
    }
}
