using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Application.Trabajadores.Commands.EliminarTrabajador;
using CaeManager.Application.Trabajadores.Commands.EliminarTrabajadores;
using CaeManager.Application.Trabajadores.Commands.RestaurarTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lista de Trabajadores (/trabajadores) contra su mockup Gen 2 («Lista
/// Trabajadores TALVEG.dc.html»). El vacío por filtro y el menú con «Abrir
/// Trabajador 360», que ya existían y se conservan, los sigue probando
/// <see cref="TrabajadoresVacioPorFiltroTests"/>.
///
/// <para>
/// El doble del mediador guarda los trabajadores y APLICA lo que recibe:
/// filtra por <c>Busqueda</c>, <c>EmpresaId</c>, <c>SubcontrataId</c> y
/// <c>EstadoDocumental</c>, ordena por <c>OrdenarPor</c>/<c>Descendente</c>
/// con la misma lista blanca que <c>ObtenerTrabajadoresQueryHandler</c>, y
/// pagina con <c>Pagina</c>/<c>TamanoPagina</c>. Los comandos cambian lo
/// guardado. Un doble que ignorase un parámetro dejaría en verde una pantalla
/// que no lo envía.
/// </para>
/// </summary>
public class TrabajadoresListaGen2Tests : BunitContext
{
    /// <summary>QuickGrid y AtajosListaTeclado importan sus módulos JS al montarse.</summary>
    public TrabajadoresListaGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid EmpresaEbro = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EmpresaDexter = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SubcontrataNervion = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed record Fila(TrabajadorListaDto Dto, Guid? EmpresaId, Guid? SubcontrataId);

    private sealed class MediatorFalso : IMediator
    {
        public List<Fila> Almacen { get; } = [];
        private readonly List<Fila> _papelera = [];
        public List<object> Enviadas { get; } = [];

        public PerfilVocabularioTenant Perfil { get; set; } = PerfilVocabularioTenant.Consultora;
        public List<EmpresaSelectorDto> Empresas { get; } =
            [new(EmpresaEbro, "Montajes Ebro S.L."), new(EmpresaDexter, "Dexter Industrial S.A.")];
        public List<FiltroGuardadoDto> FiltrosGuardados { get; } = [];
        public List<CentroSelectorDto> Centros { get; } = [];
        public Func<Guid, IReadOnlyList<DocumentoFaltanteDto>> Faltantes { get; set; } = _ => [];

        /// <summary>
        /// Si devuelve una tarea para la petición, esa es la respuesta: permite
        /// retenerla con un <see cref="TaskCompletionSource{TResult}"/> y
        /// resolverla fuera de orden.
        /// </summary>
        public Func<object, Task<object>?>? Retener { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);

            if (Retener?.Invoke(request) is { } retenida)
                return (TResponse)await retenida;

            // Síncrono a propósito, como los demás dobles de lista: lo asíncrono
            // de verdad se prueba reteniendo con Retener.
            return (TResponse)Responder(request);
        }

        private object Responder(object request)
        {
            switch (request)
            {
                case ObtenerPerfilVocabularioActualQuery:
                    return Perfil;
                case ObtenerEmpresasParaSelectorQuery:
                    return (IReadOnlyList<EmpresaSelectorDto>)Empresas.ToList();
                case ObtenerSubcontratasParaSelectorQuery:
                    return (IReadOnlyList<SubcontrataSelectorDto>)[new SubcontrataSelectorDto(SubcontrataNervion, "Aislamientos Nervión S.L.")];
                case ObtenerFiltrosGuardadosQuery:
                    return (IReadOnlyList<FiltroGuardadoDto>)FiltrosGuardados.ToList();
                case ObtenerTrabajadoresQuery q:
                    return Filtrar(q);
                case ObtenerTrabajadorPorIdQuery q:
                    return Detalle(q.Id)!;
                case ObtenerCentrosParaSelectorQuery:
                    return (IReadOnlyList<CentroSelectorDto>)Centros.ToList();
                case ObtenerDocumentosFaltantesParaAsignacionQuery q:
                    return Faltantes(q.CentroIds.Single());
                case CrearTrabajadorCommand:
                    return Result.Exito(Guid.NewGuid());
                case EliminarTrabajadorCommand c:
                    _papelera.AddRange(Almacen.Where(f => f.Dto.Id == c.Id));
                    Almacen.RemoveAll(f => f.Dto.Id == c.Id);
                    return Result.Exito();
                case RestaurarTrabajadorCommand c:
                    Almacen.AddRange(_papelera.Where(f => f.Dto.Id == c.Id));
                    _papelera.RemoveAll(f => f.Dto.Id == c.Id);
                    return Result.Exito();
                case EliminarTrabajadoresCommand c:
                    var borrados = Almacen.RemoveAll(f => c.Ids.Contains(f.Dto.Id));
                    return Result.Exito(new ResultadoEliminacionLoteDto(borrados, []));
                default:
                    throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
            }
        }

        public TrabajadorDetalleDto? Detalle(Guid id) =>
            Almacen.Select(f => f.Dto).Where(t => t.Id == id)
                .Select(t => new TrabajadorDetalleDto(t.Id, null, null, t.EmpleadorNombre, t.Nombre, t.Apellidos, t.Dni,
                    null, null, null, null, null, null, Guid.NewGuid()))
                .SingleOrDefault();

        /// <summary>
        /// Filtra, ordena y pagina como <c>ObtenerTrabajadoresQueryHandler</c>:
        /// el total es el de los coincidentes y las filas, solo las de la
        /// página pedida.
        /// </summary>
        public ResultadoPaginado<TrabajadorListaDto> Filtrar(ObtenerTrabajadoresQuery q)
        {
            var coincidentes = Almacen
                .Where(f => q.Busqueda is null
                    || $"{f.Dto.Nombre} {f.Dto.Apellidos} {f.Dto.Dni}".Contains(q.Busqueda, StringComparison.OrdinalIgnoreCase))
                .Where(f => q.EmpresaId is null || f.EmpresaId == q.EmpresaId)
                .Where(f => q.SubcontrataId is null || f.SubcontrataId == q.SubcontrataId)
                .Where(f => EstadoDocumentalFiltro.Coincide(f.Dto.EstadoDocumental, q.EstadoDocumental))
                .Select(f => f.Dto)
                .ToList();

            var pagina = Ordenar(coincidentes, q.OrdenarPor, q.Descendente)
                .Skip((q.Pagina - 1) * q.TamanoPagina)
                .Take(q.TamanoPagina)
                .ToList();
            return new ResultadoPaginado<TrabajadorListaDto>(pagina, coincidentes.Count, q.Pagina, q.TamanoPagina);
        }

        /// <summary>Misma lista blanca que el handler; cualquier otro nombre cae en Apellidos, Nombre.</summary>
        private static IEnumerable<TrabajadorListaDto> Ordenar(List<TrabajadorListaDto> filas, string? ordenarPor, bool descendente)
        {
            var c = StringComparer.Ordinal;
            return (ordenarPor, descendente) switch
            {
                (nameof(TrabajadorListaDto.Apellidos), true) => filas.OrderByDescending(t => t.Apellidos, c).ThenBy(t => t.Nombre, c),
                (nameof(TrabajadorListaDto.Nombre), false) => filas.OrderBy(t => t.Nombre, c).ThenBy(t => t.Apellidos, c),
                (nameof(TrabajadorListaDto.Nombre), true) => filas.OrderByDescending(t => t.Nombre, c).ThenBy(t => t.Apellidos, c),
                (nameof(TrabajadorListaDto.Dni), false) => filas.OrderBy(t => t.Dni, c),
                (nameof(TrabajadorListaDto.Dni), true) => filas.OrderByDescending(t => t.Dni, c),
                (nameof(TrabajadorListaDto.EmpleadorNombre), false) => filas.OrderBy(t => t.EmpleadorNombre, c).ThenBy(t => t.Apellidos, c),
                (nameof(TrabajadorListaDto.EmpleadorNombre), true) => filas.OrderByDescending(t => t.EmpleadorNombre, c).ThenBy(t => t.Apellidos, c),
                (nameof(TrabajadorListaDto.EstadoDocumental), false) => filas.OrderBy(t => EstadoDocumentalFiltro.ClaveOrden(t.EstadoDocumental)).ThenBy(t => t.Apellidos, c),
                (nameof(TrabajadorListaDto.EstadoDocumental), true) => filas.OrderByDescending(t => EstadoDocumentalFiltro.ClaveOrden(t.EstadoDocumental)).ThenBy(t => t.Apellidos, c),
                _ => filas.OrderBy(t => t.Apellidos, c).ThenBy(t => t.Nombre, c)
            };
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private static Fila Trabajador(string nombre, string apellidos, Guid? empresaId = null, Guid? subcontrataId = null,
        EstadoDocumento? estado = EstadoDocumento.Vigente)
    {
        var empleador = subcontrataId is not null ? "Aislamientos Nervión S.L."
            : empresaId == EmpresaDexter ? "Dexter Industrial S.A." : "Montajes Ebro S.L.";
        return new Fila(
            new TrabajadorListaDto(Guid.NewGuid(), nombre, apellidos, $"{Math.Abs(apellidos.GetHashCode()) % 100000000:00000000}Z", empleador, estado),
            subcontrataId is null ? empresaId ?? EmpresaEbro : null,
            subcontrataId);
    }

    private void Registrar(MediatorFalso mediador, string url)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearTrabajadorCommand>>(_ => new InlineValidator<CrearTrabajadorCommand>());

        // Los filtros de URL son [SupplyParameterFromQuery]: se llega a ellos
        // navegando, no pasándolos como parámetros de componente.
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
    }

    private IRenderedComponent<Trabajadores> Renderizar(MediatorFalso mediador, string url = "trabajadores")
    {
        Registrar(mediador, url);
        var cut = Render<Trabajadores>();
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("aria-busy=\"true\""));
        return cut;
    }

    private static List<IElement> FilasDeDatos(IRenderedComponent<Trabajadores> cut) =>
        cut.FindAll("tbody tr").Where(tr => tr.QuerySelector(".enlace-nombre-fila") is not null).ToList();

    /// <summary>Texto del botón de nombre en la posición <paramref name="columna"/> (0 Apellidos, 1 Nombre) de cada fila.</summary>
    private static List<string> Columna(IRenderedComponent<Trabajadores> cut, int columna) =>
        FilasDeDatos(cut).Select(tr => tr.QuerySelectorAll(".enlace-nombre-fila")[columna].TextContent.Trim()).ToList();

    private static IElement SelectDeLaBarra(IRenderedComponent<Trabajadores> cut, string etiqueta) =>
        cut.FindAll(".barra-trabajo-trabajadores .campo")
            .Single(c => c.QuerySelector("label")?.TextContent.Trim() == etiqueta)
            .QuerySelector("select")!;

    private static IElement BotonDeLaBarra(IRenderedComponent<Trabajadores> cut, string texto) =>
        cut.FindAll(".barra-trabajo-trabajadores button").Single(b => b.TextContent.Trim() == texto);

    private static ObtenerTrabajadoresQuery UltimaConsulta(MediatorFalso mediador) =>
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Last();

    // --- Cabecera y barra de trabajo -----------------------------------------------------------

    [Theory]
    [InlineData(PerfilVocabularioTenant.Consultora, "Trabajadores")]
    [InlineData(PerfilVocabularioTenant.ClienteDirecto, "Mis trabajadores")]
    public void La_cabecera_es_la_Gen_2_con_el_titulo_del_perfil_y_las_acciones_de_la_pagina(
        PerfilVocabularioTenant perfil, string titulo)
    {
        var mediador = new MediatorFalso { Perfil = perfil, Almacen = { Trabajador("Javier", "Salas Moreno") } };
        var cut = Renderizar(mediador);

        var cabecera = cut.Find("header.cabecera-pagina");
        cabecera.QuerySelector("h1.titulo-pagina")!.TextContent.Trim().Should().Be(titulo);
        cabecera.QuerySelector(".cabecera-pagina-kicker").Should().BeNull("el mockup no lleva kicker");
        var acciones = cabecera.QuerySelector(".acciones-cabecera")!;
        acciones.QuerySelector("a.enlace-exportar")!.GetAttribute("href").Should().Be("/trabajadores/exportar.xlsx");
        acciones.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).Should().Contain("+ Nuevo trabajador");
    }

    /// <summary>
    /// El mockup pinta «Todos los centros», pero ObtenerTrabajadoresQuery no
    /// acepta un filtro por Centro: un desplegable que no filtra nada no se
    /// pinta. Barrera: los otros tres sí están.
    /// </summary>
    [Fact]
    public void La_barra_no_pinta_un_filtro_de_centro_que_la_consulta_no_sabe_aplicar()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno") } });

        cut.FindAll(".barra-trabajo-trabajadores .campo label").Select(l => l.TextContent.Trim())
            .Should().Equal("Documentación", "Empresa", "Subcontrata");
        cut.Markup.Should().NotContain("Todos los centros");
    }

    [Fact]
    public async Task Seleccion_multiple_en_la_barra_pinta_las_casillas_y_al_apagarse_suelta_la_seleccion()
    {
        var bea = Trabajador("Bea", "Alonso");
        var cut = Renderizar(new MediatorFalso { Almacen = { bea, Trabajador("Ana", "Moreno") } });
        BotonDeLaBarra(cut, "Selección múltiple").GetAttribute("aria-pressed").Should().Be("false");
        cut.FindAll("tbody input[type=checkbox]").Should().BeEmpty("sin el modo no hay casillas de fila");

        await BotonDeLaBarra(cut, "Selección múltiple").ClickAsync(new MouseEventArgs());

        BotonDeLaBarra(cut, "Selección múltiple").GetAttribute("aria-pressed").Should().Be("true");
        await cut.Find("tbody input[aria-label='Seleccionar a Bea Alonso']").ChangeAsync(new ChangeEventArgs { Value = true });
        cut.Find(".barra-acciones-lote-cantidad").TextContent.Trim().Should().Be("1 seleccionado en esta página");

        await BotonDeLaBarra(cut, "Selección múltiple").ClickAsync(new MouseEventArgs());

        cut.FindAll("tbody input[type=checkbox]").Should().BeEmpty();
        cut.FindAll(".barra-acciones-lote").Should().BeEmpty("apagar el modo suelta la selección que ya no se ve");
    }

    // --- Estados ------------------------------------------------------------------------------

    [Fact]
    public async Task Mientras_carga_se_ve_el_esqueleto_y_no_el_vacio()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno") } };
        mediador.Retener = p => p is ObtenerTrabajadoresQuery ? respuesta.Task : null;
        Registrar(mediador, "trabajadores");

        var cut = Render<Trabajadores>();

        cut.WaitForAssertion(() => cut.FindAll(".esqueleto-lista[aria-busy=true]").Should().ContainSingle());
        cut.Markup.Should().NotContain("Aún no hay trabajadores", "todavía no se sabe si hay alguno");

        mediador.Retener = null;
        await cut.InvokeAsync(() => respuesta.SetResult(mediador.Filtrar(UltimaConsulta(mediador))));

        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Salas Moreno"));
        cut.FindAll(".esqueleto-lista").Should().BeEmpty();
    }

    [Fact]
    public void Sin_trabajadores_el_vacio_invita_a_dar_de_alta_el_primero()
    {
        var cut = Renderizar(new MediatorFalso());

        var vacio = cut.Find(".estado-vacio");
        vacio.QuerySelector("h3")!.TextContent.Trim().Should().Be("Aún no hay trabajadores");
        vacio.TextContent.Should().Contain("Da de alta el primero para empezar a controlar su documentación.");
        vacio.QuerySelector("button")!.TextContent.Trim().Should().Be("+ Nuevo trabajador");
    }

    /// <summary>
    /// La consulta del arranque (sin filtro) tarda; mientras tanto se filtra
    /// por «Vencido», que responde en seguida sin nada. Cuando la vieja llega,
    /// no puede pisar el total: la pantalla sigue diciendo que ninguno coincide.
    /// </summary>
    [Fact]
    public async Task Una_respuesta_lenta_de_la_pregunta_anterior_no_pisa_el_resultado_de_la_vigente()
    {
        var respuestaVieja = new TaskCompletionSource<object>();
        var retenida = false;
        var mediador = new MediatorFalso
        {
            Almacen = { Trabajador("Javier", "Salas Moreno"), Trabajador("Ana", "Vega Ortiz"), Trabajador("Luis", "Iglesias Rey") }
        };
        mediador.Retener = p =>
        {
            if (retenida || p is not ObtenerTrabajadoresQuery { EstadoDocumental: null }) return null;
            retenida = true;
            return respuestaVieja.Task;
        };
        Registrar(mediador, "trabajadores");
        var cut = Render<Trabajadores>();
        cut.WaitForAssertion(() => retenida.Should().BeTrue());

        await SelectDeLaBarra(cut, "Documentación").ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoDocumento.Vencido) });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ningún trabajador con estos filtros"));

        await cut.InvokeAsync(() => respuestaVieja.SetResult(
            mediador.Filtrar(new ObtenerTrabajadoresQuery(null))));

        // Cualquier repintado posterior enseña el estado que dejó la respuesta
        // vieja; se fuerza uno para no depender de cuál llegue.
        cut.Render();

        cut.Markup.Should().Contain("Ningún trabajador con estos filtros",
            "la respuesta vieja era de la lista sin filtro, no de la pregunta vigente");
        cut.Find(".paginador-texto").TextContent.Should().Contain("0 trabajador(es)");
    }

    // --- Orden y página -----------------------------------------------------------------------

    /// <summary>
    /// El doble ordena según lo que recibe. El orden por defecto (apellidos)
    /// deja los nombres como Bea, Ana, Carlos: ni el ascendente ni el
    /// descendente por nombre.
    /// </summary>
    [Fact]
    public async Task Pulsar_la_cabecera_Nombre_ordena_la_consulta_por_nombre_y_la_segunda_vez_al_reves()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Trabajador("Bea", "Alonso"), Trabajador("Ana", "Moreno"), Trabajador("Carlos", "Zubiri") }
        };
        var cut = Renderizar(mediador);
        Columna(cut, 1).Should().Equal(["Bea", "Ana", "Carlos"], "punto de partida: el orden por apellidos");

        await CabeceraOrdenable(cut, "Nombre").ClickAsync(new MouseEventArgs());

        UltimaConsulta(mediador).OrdenarPor.Should().Be(nameof(TrabajadorListaDto.Nombre));
        UltimaConsulta(mediador).Descendente.Should().BeFalse();
        cut.WaitForAssertion(() => Columna(cut, 1).Should().Equal(["Ana", "Bea", "Carlos"]));

        await CabeceraOrdenable(cut, "Nombre").ClickAsync(new MouseEventArgs());

        UltimaConsulta(mediador).OrdenarPor.Should().Be(nameof(TrabajadorListaDto.Nombre));
        UltimaConsulta(mediador).Descendente.Should().BeTrue("la segunda pulsación invierte el orden");
        cut.WaitForAssertion(() => Columna(cut, 1).Should().Equal(["Carlos", "Bea", "Ana"]));
    }

    private static IElement CabeceraOrdenable(IRenderedComponent<Trabajadores> cut, string titulo) =>
        cut.FindAll("thead th").Single(th => th.TextContent.Trim() == titulo).QuerySelector("button")!;

    /// <summary>
    /// 25 trabajadores: la página 1 son los 20 primeros por apellidos y la 2,
    /// los cinco últimos. Si la pantalla mandara siempre la página 1, la
    /// segunda repetiría la primera.
    /// </summary>
    [Fact]
    public async Task Pasar_a_la_pagina_siguiente_pide_la_pagina_2_y_cambiar_el_tamano_vuelve_a_la_1()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 25; i++)
            mediador.Almacen.Add(Trabajador("Nombre", $"Apellido {i:00}"));
        var cut = Renderizar(mediador);
        Columna(cut, 0).Should().HaveCount(20).And.StartWith("Apellido 01");
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 1 de 2");

        await cut.FindAll(".paginador-simple button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());

        UltimaConsulta(mediador).Pagina.Should().Be(2);
        UltimaConsulta(mediador).TamanoPagina.Should().Be(20);
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal(
            ["Apellido 21", "Apellido 22", "Apellido 23", "Apellido 24", "Apellido 25"]));
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 2 de 2").And.Contain("25 trabajador(es)");

        await cut.Find(".paginador-tamano-select").ChangeAsync(new ChangeEventArgs { Value = "50" });

        UltimaConsulta(mediador).Pagina.Should().Be(1);
        UltimaConsulta(mediador).TamanoPagina.Should().Be(50);
        cut.WaitForAssertion(() => Columna(cut, 0).Should().HaveCount(25));
    }

    // --- Filtros y URL ------------------------------------------------------------------------

    [Fact]
    public async Task Filtrar_por_empresa_manda_su_id_y_filtrar_por_subcontrata_suelta_el_de_empresa()
    {
        var mediador = new MediatorFalso
        {
            Almacen =
            {
                Trabajador("Javier", "Salas Moreno", empresaId: EmpresaEbro),
                Trabajador("Pedro", "Roldán Cano", empresaId: EmpresaDexter),
                Trabajador("Marta", "Duarte Gil", subcontrataId: SubcontrataNervion),
            }
        };
        var cut = Renderizar(mediador);

        await SelectDeLaBarra(cut, "Empresa").ChangeAsync(new ChangeEventArgs { Value = EmpresaDexter.ToString() });

        UltimaConsulta(mediador).EmpresaId.Should().Be(EmpresaDexter);
        UltimaConsulta(mediador).SubcontrataId.Should().BeNull();
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Roldán Cano"));

        await SelectDeLaBarra(cut, "Subcontrata").ChangeAsync(new ChangeEventArgs { Value = SubcontrataNervion.ToString() });

        UltimaConsulta(mediador).SubcontrataId.Should().Be(SubcontrataNervion);
        UltimaConsulta(mediador).EmpresaId.Should().BeNull("los dos filtros de empleador se excluyen");
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Duarte Gil"));
    }

    [Fact]
    public async Task Quitar_los_filtros_desde_el_vacio_los_quita_de_la_url_y_de_la_consulta()
    {
        var mediador = new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno"), Trabajador("Ana", "Vega Ortiz") } };
        var cut = Renderizar(mediador, "trabajadores?q=Nadie&estado=Vencido");
        var navegacion = Services.GetRequiredService<NavigationManager>();
        cut.Markup.Should().Contain("Ningún trabajador con estos filtros", "es el punto de partida de este caso");
        UltimaConsulta(mediador).Busqueda.Should().Be("Nadie");
        UltimaConsulta(mediador).EstadoDocumental.Should().Be("Vencido");

        await cut.FindAll(".estado-vacio button").Single(b => b.TextContent.Trim() == "Quitar los filtros").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().NotContain("q=").And.NotContain("estado=",
            "si solo se quitaran en memoria, la siguiente pasada de parámetros los repondría desde la URL");
        UltimaConsulta(mediador).Busqueda.Should().BeNull();
        UltimaConsulta(mediador).EstadoDocumental.Should().BeNull();
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Salas Moreno", "Vega Ortiz"));
        cut.FindAll(".chip-filtro").Should().BeEmpty();
    }

    /// <summary>
    /// Volver atrás, o pegar un enlace, a otro <c>?q=</c> estando ya en la
    /// página: el chip y la lista tienen que hablar de la misma búsqueda.
    /// </summary>
    [Fact]
    public async Task Navegar_a_otra_busqueda_dentro_de_la_pagina_vuelve_a_pedir_la_lista()
    {
        var mediador = new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno"), Trabajador("Ana", "Vega Ortiz") } };
        var cut = Renderizar(mediador, "trabajadores?q=Salas");
        Columna(cut, 0).Should().Equal("Salas Moreno");

        await cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo("trabajadores?q=Vega"));

        cut.WaitForAssertion(() => UltimaConsulta(mediador).Busqueda.Should().Be("Vega"));
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Vega Ortiz"));
        cut.Find(".chip-filtro").TextContent.Should().Contain("Vega");
    }

    /// <summary>
    /// La búsqueda de un filtro guardado se escribe en <c>?q=</c>. Si solo se
    /// aplicara en memoria, cambiar después el filtro de documentación (que sí
    /// escribe la URL) la borraría al releer un <c>?q=</c> vacío.
    /// </summary>
    [Fact]
    public async Task Un_filtro_guardado_con_busqueda_sobrevive_a_cambiar_la_documentacion()
    {
        var filtroId = Guid.NewGuid();
        var mediador = new MediatorFalso
        {
            Almacen = { Trabajador("Javier", "Salas Moreno"), Trabajador("Ana", "Vega Ortiz") },
            FiltrosGuardados = { new FiltroGuardadoDto(filtroId, "Solo Vega", "{\"Busqueda\":\"Vega\"}", DateTime.UtcNow) }
        };
        var cut = Renderizar(mediador);

        await SelectDeLaBarra(cut, "Filtros guardados").ChangeAsync(new ChangeEventArgs { Value = filtroId.ToString() });

        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=Vega");
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Vega Ortiz"));

        await SelectDeLaBarra(cut, "Documentación").ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoDocumento.Vigente) });

        UltimaConsulta(mediador).Busqueda.Should().Be("Vega", "cambiar un filtro no puede soltar otro que sigue puesto");
        UltimaConsulta(mediador).EstadoDocumental.Should().Be(nameof(EstadoDocumento.Vigente));
    }

    // --- Acciones de fila ---------------------------------------------------------------------

    private static async Task PulsarEnElMenuDeLaFila(IRenderedComponent<Trabajadores> cut, int fila, string item)
    {
        await cut.FindAll(".menu-acciones-disparador")[fila].ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-item").Single(i => i.TextContent.Trim() == item).ClickAsync(new MouseEventArgs());
    }

    [Fact]
    public async Task Eliminar_pide_confirmacion_con_su_efecto_borra_esa_fila_y_deshacer_la_restaura()
    {
        var ana = Trabajador("Ana", "Moreno");
        var mediador = new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso"), ana } };
        var cut = Renderizar(mediador);

        await PulsarEnElMenuDeLaFila(cut, 1, "Eliminar");

        var dialogo = cut.Find("[role=dialog]");
        dialogo.TextContent.Should().Contain("¿Eliminar a Ana Moreno?")
            .And.Contain("Se ocultará de las listas activas. Podrás deshacerlo desde el aviso que aparecerá.");
        mediador.Enviadas.OfType<EliminarTrabajadorCommand>().Should().BeEmpty("abrir el diálogo no borra nada");

        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarTrabajadorCommand>().Should().Equal([new EliminarTrabajadorCommand(ana.Dto.Id)]);
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Alonso"));

        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Single(m => m.TextoAccion == "Deshacer");
        await cut.InvokeAsync(aviso.OnAccion!);

        mediador.Enviadas.OfType<RestaurarTrabajadorCommand>().Should().Equal([new RestaurarTrabajadorCommand(ana.Dto.Id)]);
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Alonso", "Moreno"));
    }

    [Fact]
    public async Task Abrir_Trabajador_360_desde_el_menu_navega_a_la_ficha_de_esa_fila()
    {
        var ana = Trabajador("Ana", "Moreno");
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso"), ana } });

        await PulsarEnElMenuDeLaFila(cut, 1, "Abrir Trabajador 360");

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"/trabajadores/{ana.Dto.Id}");
    }

    [Fact]
    public async Task Los_atajos_j_x_y_Enter_recorren_marcan_y_abren_la_vista_previa_de_la_fila()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso"), Trabajador("Ana", "Moreno") } });
        await BotonDeLaBarra(cut, "Selección múltiple").ClickAsync(new MouseEventArgs());
        var atajos = cut.FindComponent<AtajosListaTeclado>();

        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("j"));
        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("j"));
        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("x"));

        cut.Find("tbody input[aria-label='Seleccionar a Ana Moreno']").HasAttribute("checked").Should().BeTrue();
        cut.Find("tbody input[aria-label='Seleccionar a Bea Alonso']").HasAttribute("checked").Should().BeFalse();

        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("Enter"));

        cut.WaitForAssertion(() => cut.Find("aside.drawer-preview-trabajador .nombre-cabecera-preview-trabajador")
            .TextContent.Trim().Should().Be("Ana Moreno"));
    }

    // --- Carreras y dobles clics --------------------------------------------------------------

    /// <summary>
    /// La vista previa de A tarda; mientras tanto se abre la de B, que responde
    /// en seguida. Cuando A llega, el panel sigue siendo de B.
    /// </summary>
    [Fact]
    public async Task La_vista_previa_de_una_fila_no_se_pisa_con_la_respuesta_tardia_de_otra()
    {
        var a = Trabajador("Bea", "Alonso");
        var b = Trabajador("Ana", "Moreno");
        var respuestaDeA = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso
        {
            Almacen = { a, b },
            Retener = p => p is ObtenerTrabajadorPorIdQuery q && q.Id == a.Dto.Id ? respuestaDeA.Task : null
        };
        var cut = Renderizar(mediador);

        // Sin await: el drawer espera a la consulta retenida.
        var clicA = cut.FindAll("td .enlace-nombre-fila").First(e => e.TextContent.Trim() == "Alonso").ClickAsync(new MouseEventArgs());
        await cut.FindAll("td .enlace-nombre-fila").First(e => e.TextContent.Trim() == "Moreno").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find(".nombre-cabecera-preview-trabajador").TextContent.Trim().Should().Be("Ana Moreno"));

        await cut.InvokeAsync(() => respuestaDeA.SetResult(mediador.Detalle(a.Dto.Id)!));
        await clicA;
        cut.Render();

        cut.Find(".nombre-cabecera-preview-trabajador").TextContent.Trim().Should().Be("Ana Moreno",
            "la respuesta de Bea era de otra pregunta");
    }

    /// <summary>
    /// Dos «Guardar» del alta que llegan antes de que el botón se pinte
    /// deshabilitado: un solo comando. Se invoca el manejador del botón dos
    /// veces, que es lo que el servidor recibe en ese caso (Boton mantiene el
    /// @onclick enganchado aunque esté disabled).
    /// </summary>
    [Fact]
    public async Task Dos_Guardar_seguidos_del_alta_mandan_un_solo_comando()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso { Perfil = PerfilVocabularioTenant.ClienteDirecto };
        mediador.Empresas.RemoveAt(1);
        mediador.Retener = p => p is CrearTrabajadorCommand ? respuesta.Task : null;
        var cut = Renderizar(mediador);

        await cut.Find("header.cabecera-pagina button").ClickAsync(new MouseEventArgs());
        var guardar = cut.FindComponents<Boton>().Single(b => b.Find("button").TextContent.Trim() == "Guardar");

        var primero = cut.InvokeAsync(() => guardar.Instance.OnClick.InvokeAsync());
        var segundo = cut.InvokeAsync(() => guardar.Instance.OnClick.InvokeAsync());

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().ContainSingle(
            "el segundo clic llega con el primero todavía en vuelo");
        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Single().EmpresaId.Should().Be(EmpresaEbro,
            "perfil Cliente Directo con una única Empresa: se resuelve en silencio");

        await cut.InvokeAsync(() => respuesta.SetResult(Result.Exito(Guid.NewGuid())));
        await Task.WhenAll(primero, segundo);
    }

    /// <summary>
    /// En «Asignar a centro…», elegir el centro A (lento, con faltantes) y
    /// después el B (sin faltantes): el aviso no puede hablar de A.
    /// </summary>
    [Fact]
    public async Task El_aviso_de_faltantes_es_del_centro_elegido_y_no_del_anterior_que_respondio_tarde()
    {
        var bea = Trabajador("Bea", "Alonso");
        var centroA = new CentroSelectorDto(Guid.NewGuid(), "Planta Zaragoza", "Refrielectric S.L.", "Montajes Ebro S.L.");
        var centroB = new CentroSelectorDto(Guid.NewGuid(), "Centro Logístico Norte", "Refrielectric S.L.", "Montajes Ebro S.L.");
        var respuestaDeA = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso
        {
            Almacen = { bea },
            Centros = { centroA, centroB },
            Retener = p => p is ObtenerDocumentosFaltantesParaAsignacionQuery q && q.CentroIds.Single() == centroA.Id ? respuestaDeA.Task : null
        };
        var cut = Renderizar(mediador);

        await BotonDeLaBarra(cut, "Selección múltiple").ClickAsync(new MouseEventArgs());
        await cut.Find("tbody input[aria-label='Seleccionar a Bea Alonso']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim() == "Asignar a centro…").ClickAsync(new MouseEventArgs());
        var campo = cut.FindComponent<CampoBuscarSelect>();

        var eleccionA = cut.InvokeAsync(() => campo.Instance.ValorChanged.InvokeAsync(centroA.Id.ToString()));
        await cut.InvokeAsync(() => campo.Instance.ValorChanged.InvokeAsync(centroB.Id.ToString()));

        IReadOnlyList<DocumentoFaltanteDto> faltantesDeA =
            [new(bea.Dto.Id, "Bea Alonso", centroA.Id, centroA.Nombre, Guid.NewGuid(), "Formación PRL específica")];
        await cut.InvokeAsync(() => respuestaDeA.SetResult(faltantesDeA));
        await eleccionA;
        cut.Render();

        cut.FindAll(".alerta-preflight-asignacion").Should().BeEmpty("el centro elegido es B, que no deja faltantes");
        cut.FindAll(".modal-pie button, [role=dialog] button").Select(b => b.TextContent.Trim())
            .Should().Contain("Asignar").And.NotContain("Asignar igualmente");
    }
}
