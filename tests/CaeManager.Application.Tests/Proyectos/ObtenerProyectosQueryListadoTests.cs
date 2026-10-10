using CaeManager.Application.Proyectos;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using FluentAssertions;

namespace CaeManager.Application.Tests.Proyectos;

/// <summary>
/// El listado de Proyectos ya no exige un Cliente empresarial: lista los de todos los que el usuario
/// alcanza, paginado, y la búsqueda y el estado los filtra la consulta. Estas pruebas fijan esa lógica
/// con dobles en memoria; que el alcance y el Tenant propietario se cumplan contra PostgreSQL con la
/// identidad de runtime lo prueba <c>ObtenerProyectosListadoBajoRuntimeTests</c> (integración).
/// </summary>
public class ObtenerProyectosQueryListadoTests
{
    private static readonly DateOnly Inicio = new(2026, 3, 12);
    private static readonly Guid EmpresaPropiaId = Guid.NewGuid();

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

    private readonly Empresa _obrasDelSur = Empresa.CrearComoCliente("Obras del Sur S.L.", "B12345674", false, null, null);
    private readonly Empresa _montajesNorte = Empresa.CrearComoCliente("Montajes Norte S.A.", "B87654323", false, null, null);

    public ObtenerProyectosQueryListadoTests()
    {
        _empresas.ListaEmpresas.Add(_obrasDelSur);
        _empresas.ListaEmpresas.Add(_montajesNorte);
    }

    private ObtenerProyectosQueryHandler Handler(AlcanceDatosServiceFalso? alcance = null) =>
        new(_centros, _empresas, _proyectos, _trabajadores, alcance ?? new AlcanceDatosServiceFalso());

    private static AlcanceDatosServiceFalso AlcanceLimitadoA(params Guid[] clienteIds) =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: clienteIds);

    private Proyecto NuevoProyecto(Empresa cliente, string nombre, string centroNombre = "Sede Sevilla", DateOnly? inicio = null, DateOnly? cierre = null)
    {
        var centro = new Centro(cliente.Id, EmpresaPropiaId, centroNombre);
        _centros.ListaCentros.Add(centro);
        var proyecto = Proyecto.Crear(cliente.Id, centro.Id, nombre, inicio ?? Inicio, null, null);
        if (cierre is { } fechaCierre)
            proyecto.Cerrar(fechaCierre);
        _proyectos.ListaProyectos.Add(proyecto);
        return proyecto;
    }

    private Task<CaeManager.Application.Common.ResultadoPaginado<ProyectoListaDto>> Pedir(ObtenerProyectosQuery consulta, AlcanceDatosServiceFalso? alcance = null) =>
        Handler(alcance).Handle(consulta, CancellationToken.None);

    [Fact]
    public async Task Sin_Cliente_empresarial_lista_los_de_todos_y_cada_fila_dice_el_suyo()
    {
        var delSur = NuevoProyecto(_obrasDelSur, "Reforma nave");
        var delNorte = NuevoProyecto(_montajesNorte, "Instalación contra incendios", "Planta Bilbao");

        var resultado = await Pedir(new ObtenerProyectosQuery());

        resultado.TotalElementos.Should().Be(2);
        resultado.Elementos.Single(p => p.Id == delSur.Id).Should().Match<ProyectoListaDto>(p =>
            p.ClienteId == _obrasDelSur.Id && p.ClienteRazonSocial == "Obras del Sur S.L." && p.CentroNombre == "Sede Sevilla");
        resultado.Elementos.Single(p => p.Id == delNorte.Id).Should().Match<ProyectoListaDto>(p =>
            p.ClienteId == _montajesNorte.Id && p.ClienteRazonSocial == "Montajes Norte S.A." && p.CentroNombre == "Planta Bilbao");
    }

    [Fact]
    public async Task Con_Cliente_empresarial_solo_lista_y_cuenta_los_suyos()
    {
        var delSur = NuevoProyecto(_obrasDelSur, "Reforma nave");
        NuevoProyecto(_montajesNorte, "Instalación contra incendios");

        var resultado = await Pedir(new ObtenerProyectosQuery(_obrasDelSur.Id, ConRecuentosPorEstado: true));

        resultado.Elementos.Select(p => p.Id).Should().Equal(delSur.Id);
        resultado.TotalElementos.Should().Be(1);
        resultado.RecuentosPorEstado.Should().Equal(new Dictionary<string, int>
        {
            [ObtenerProyectosQuery.EstadoAbiertos] = 1,
            [ObtenerProyectosQuery.EstadoCerrados] = 0
        });
    }

    [Fact]
    public async Task Con_alcance_limitado_y_sin_Cliente_empresarial_ni_lista_ni_cuenta_los_de_fuera_del_alcance()
    {
        var visible = NuevoProyecto(_obrasDelSur, "Reforma nave");
        NuevoProyecto(_montajesNorte, "Instalación contra incendios");
        NuevoProyecto(_montajesNorte, "Ampliación planta", cierre: Inicio.AddDays(30));

        var resultado = await Pedir(new ObtenerProyectosQuery(ConRecuentosPorEstado: true), AlcanceLimitadoA(_obrasDelSur.Id));

        resultado.Elementos.Select(p => p.Id).Should().Equal(visible.Id);
        resultado.TotalElementos.Should().Be(1, "el total es el de lo que este usuario alcanza, no el del Tenant");
        resultado.RecuentosPorEstado.Should().Equal(new Dictionary<string, int>
        {
            [ObtenerProyectosQuery.EstadoAbiertos] = 1,
            [ObtenerProyectosQuery.EstadoCerrados] = 0
        }, "la franja no puede delatar con una cifra los Proyectos de un Cliente empresarial fuera del alcance");
    }

    [Fact]
    public async Task Un_Cliente_empresarial_pedido_fuera_del_alcance_devuelve_la_lista_vacia_y_recuentos_a_cero()
    {
        NuevoProyecto(_obrasDelSur, "Reforma nave");
        NuevoProyecto(_montajesNorte, "Instalación contra incendios");

        var resultado = await Pedir(
            new ObtenerProyectosQuery(_montajesNorte.Id, ConRecuentosPorEstado: true), AlcanceLimitadoA(_obrasDelSur.Id));

        resultado.Elementos.Should().BeEmpty();
        resultado.TotalElementos.Should().Be(0);
        resultado.RecuentosPorEstado!.Values.Should().OnlyContain(filas => filas == 0);
    }

    [Fact]
    public async Task Un_alcance_sin_ningun_Cliente_empresarial_no_lista_nada()
    {
        NuevoProyecto(_obrasDelSur, "Reforma nave");

        var resultado = await Pedir(new ObtenerProyectosQuery(), AlcanceLimitadoA());

        resultado.Elementos.Should().BeEmpty("una lista de alcance vacía es «ninguno», no «sin restricción»");
        resultado.TotalElementos.Should().Be(0);
    }

    [Fact]
    public async Task Pagina_en_orden_de_inicio_descendente_y_el_total_cuenta_todas_las_paginas()
    {
        var marzo = NuevoProyecto(_obrasDelSur, "Marzo", inicio: new DateOnly(2026, 3, 1));
        var mayo = NuevoProyecto(_montajesNorte, "Mayo", inicio: new DateOnly(2026, 5, 1));
        var abril = NuevoProyecto(_obrasDelSur, "Abril", inicio: new DateOnly(2026, 4, 1));

        var primera = await Pedir(new ObtenerProyectosQuery(TamanoPagina: 2));
        var segunda = await Pedir(new ObtenerProyectosQuery(Pagina: 2, TamanoPagina: 2));

        primera.Elementos.Select(p => p.Id).Should().Equal(mayo.Id, abril.Id);
        segunda.Elementos.Select(p => p.Id).Should().Equal(marzo.Id);
        primera.TotalElementos.Should().Be(3);
        segunda.TotalElementos.Should().Be(3);
        primera.TotalPaginas.Should().Be(2);
    }

    [Fact]
    public async Task Los_proyectos_con_la_misma_fecha_de_inicio_no_se_repiten_ni_se_pierden_entre_paginas()
    {
        // Misma fecha de inicio en todos: sin desempate, el corte entre páginas no es estable.
        var todos = Enumerable.Range(1, 5).Select(n => NuevoProyecto(_obrasDelSur, $"Proyecto {n}").Id).ToList();

        var vistos = new List<Guid>();
        for (var pagina = 1; pagina <= 3; pagina++)
            vistos.AddRange((await Pedir(new ObtenerProyectosQuery(Pagina: pagina, TamanoPagina: 2))).Elementos.Select(p => p.Id));

        vistos.Should().BeEquivalentTo(todos).And.OnlyHaveUniqueItems();
        vistos.Should().Equal(todos.OrderBy(id => id), "con la fecha empatada el orden lo cierra el Id");
    }

    [Theory]
    [InlineData("reforma")]
    [InlineData("  REFORMA  ")]
    [InlineData("nave")]
    public async Task La_busqueda_encuentra_por_nombre_del_Proyecto_sin_distinguir_mayusculas_ni_espacios_alrededor(string termino)
    {
        var reforma = NuevoProyecto(_obrasDelSur, "Reforma nave");
        NuevoProyecto(_montajesNorte, "Instalación contra incendios", "Planta Bilbao");

        var resultado = await Pedir(new ObtenerProyectosQuery(Busqueda: termino));

        resultado.Elementos.Select(p => p.Id).Should().Equal(reforma.Id);
        resultado.TotalElementos.Should().Be(1);
    }

    [Fact]
    public async Task La_busqueda_encuentra_por_nombre_del_Centro_y_no_por_el_del_Cliente_empresarial()
    {
        NuevoProyecto(_obrasDelSur, "Reforma nave", "Sede Sevilla");
        var enBilbao = NuevoProyecto(_montajesNorte, "Instalación contra incendios", "Planta Bilbao");

        (await Pedir(new ObtenerProyectosQuery(Busqueda: "bilbao"))).Elementos.Select(p => p.Id).Should().Equal(enBilbao.Id);
        (await Pedir(new ObtenerProyectosQuery(Busqueda: "Montajes"))).Elementos
            .Should().BeEmpty("el placeholder del buscador promete nombre o centro; el Cliente empresarial tiene su propio filtro");
    }

    [Fact]
    public async Task El_estado_filtra_antes_de_paginar_y_el_total_es_el_del_estado_pedido()
    {
        // Tres cerrados más recientes que el único abierto: paginando antes de filtrar, la primera
        // página de «abiertos» saldría vacía.
        var abierto = NuevoProyecto(_obrasDelSur, "Abierto", inicio: new DateOnly(2026, 1, 1));
        for (var n = 1; n <= 3; n++)
            NuevoProyecto(_obrasDelSur, $"Cerrado {n}", inicio: new DateOnly(2026, 6, n), cierre: new DateOnly(2026, 7, n));

        var abiertos = await Pedir(new ObtenerProyectosQuery(SoloAbiertos: true, TamanoPagina: 2));
        var cerrados = await Pedir(new ObtenerProyectosQuery(SoloAbiertos: false, TamanoPagina: 2));

        abiertos.Elementos.Select(p => p.Id).Should().Equal(abierto.Id);
        abiertos.TotalElementos.Should().Be(1);
        abiertos.Elementos.Should().OnlyContain(p => p.EstaAbierto && p.FechaCierreReal == null);
        cerrados.Elementos.Should().HaveCount(2).And.OnlyContain(p => !p.EstaAbierto && p.FechaCierreReal != null);
        cerrados.TotalElementos.Should().Be(3);
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task Los_recuentos_llevan_los_demas_filtros_y_no_el_de_estado_y_el_total_si_lo_lleva(bool? soloAbiertos, int totalEsperado)
    {
        NuevoProyecto(_obrasDelSur, "Reforma nave uno");
        NuevoProyecto(_obrasDelSur, "Reforma nave dos", cierre: Inicio.AddDays(10));
        NuevoProyecto(_obrasDelSur, "Reforma nave tres", cierre: Inicio.AddDays(20));
        // No pasan la búsqueda: no cuentan en ningún estado.
        NuevoProyecto(_obrasDelSur, "Instalación contra incendios");
        NuevoProyecto(_obrasDelSur, "Mantenimiento", cierre: Inicio.AddDays(5));

        var resultado = await Pedir(new ObtenerProyectosQuery(SoloAbiertos: soloAbiertos, Busqueda: "reforma", ConRecuentosPorEstado: true));

        resultado.RecuentosPorEstado.Should().Equal(new Dictionary<string, int>
        {
            [ObtenerProyectosQuery.EstadoAbiertos] = 1,
            [ObtenerProyectosQuery.EstadoCerrados] = 2
        }, "cada cifra dice cuántos quedarían al marcar solo ese estado, con la búsqueda puesta");
        resultado.TotalElementos.Should().Be(totalEsperado);
        resultado.Elementos.Should().HaveCount(totalEsperado);
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task El_total_es_el_mismo_se_pidan_o_no_los_recuentos(bool? soloAbiertos, int totalEsperado)
    {
        NuevoProyecto(_obrasDelSur, "Uno");
        NuevoProyecto(_obrasDelSur, "Dos", cierre: Inicio.AddDays(10));
        NuevoProyecto(_montajesNorte, "Tres", cierre: Inicio.AddDays(20));

        var sinRecuentos = await Pedir(new ObtenerProyectosQuery(SoloAbiertos: soloAbiertos));
        var conRecuentos = await Pedir(new ObtenerProyectosQuery(SoloAbiertos: soloAbiertos, ConRecuentosPorEstado: true));

        sinRecuentos.RecuentosPorEstado.Should().BeNull("no se pidieron: null no es «cero filas»");
        sinRecuentos.TotalElementos.Should().Be(totalEsperado);
        conRecuentos.TotalElementos.Should().Be(totalEsperado);
    }

    [Fact]
    public async Task Sin_ningun_proyecto_los_recuentos_llevan_los_dos_estados_a_cero()
    {
        var resultado = await Pedir(new ObtenerProyectosQuery(ConRecuentosPorEstado: true));

        resultado.Elementos.Should().BeEmpty();
        resultado.RecuentosPorEstado.Should().Equal(new Dictionary<string, int>
        {
            [ObtenerProyectosQuery.EstadoAbiertos] = 0,
            [ObtenerProyectosQuery.EstadoCerrados] = 0
        });
    }

    [Fact]
    public async Task Un_proyecto_cuyo_Cliente_empresarial_no_devuelve_la_consulta_de_Empresas_no_se_lista()
    {
        // Cliente empresarial dado de baja: los filtros de consulta de EF lo dejan fuera de Empresas.
        // Antes tampoco era alcanzable: el selector que obligaba a elegirlo no lo ofrecía.
        var deBaja = Empresa.CrearComoCliente("Cliente dado de baja S.L.", "B10380186", false, null, null);
        NuevoProyecto(deBaja, "Obra huérfana");
        var vigente = NuevoProyecto(_obrasDelSur, "Reforma nave");

        var resultado = await Pedir(new ObtenerProyectosQuery(ConRecuentosPorEstado: true));

        resultado.Elementos.Select(p => p.Id).Should().Equal(vigente.Id);
        resultado.TotalElementos.Should().Be(1);
    }
}
