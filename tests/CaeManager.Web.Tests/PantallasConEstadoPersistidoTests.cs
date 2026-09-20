using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Incidencias.Commands.EliminarIncidencia;
using CaeManager.Application.Incidencias.Queries.ObtenerIncidencias;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Incidencias;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Empresas.Pages;
using CaeManager.Web.Features.Incidencias.Pages;
using CaeManager.Web.Features.Subcontratas.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Las tres pantallas con estado persistido (Empresas, Subcontratas,
/// Incidencias), de punta a punta a nivel de componente: un primer montaje
/// hace de prerender (consulta y guarda), su estado se serializa con el gestor
/// de persistencia real de Blazor, y un segundo montaje —otro contenedor de
/// DI, como el circuito— lo restaura.
///
/// <para>
/// Lo que se afirma es de cada pantalla, no del ayudante (eso lo cubre
/// <see cref="EstadoDePantallaPersistidoTests"/>): que el circuito NO repite la
/// consulta de la lista cuando la sesión y la consulta son las mismas, y que SÍ
/// la repite —y enseña los datos de su propia sesión, nunca los del prerender—
/// cuando la consulta (filtro de la URL) o el Tenant propietario son otros.
/// </para>
/// </summary>
public class PantallasConEstadoPersistidoTests
{
    private static readonly Guid TenantX = SesionDePruebaParaEstadoPersistido.TenantFijo;
    private static readonly Guid TenantY = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private sealed class MediatorContador(Func<object, object> responder) : IMediator
    {
        public List<Type> Peticiones { get; } = [];
        public List<object> Enviadas { get; } = [];

        public int Veces<TQuery>() => Peticiones.Count(t => t == typeof(TQuery));

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Peticiones.Add(request.GetType());
            Enviadas.Add(request);
            return Task.FromResult((TResponse)responder(request));
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

    private sealed class AlmacenEnMemoria : IPersistentComponentStateStore
    {
        public Dictionary<string, byte[]> Contenido { get; private set; } = [];

        public Task<IDictionary<string, byte[]>> GetPersistedStateAsync() =>
            Task.FromResult<IDictionary<string, byte[]>>(new Dictionary<string, byte[]>(Contenido));

        public Task PersistStateAsync(IReadOnlyDictionary<string, byte[]> state)
        {
            Contenido = new Dictionary<string, byte[]>(state);
            return Task.CompletedTask;
        }
    }

    // ── El escenario: prerender → serialización → circuito ──────────────────────

    private sealed record Resultado(MediatorContador Circuito, string Markup, int EntradasPersistidas);

    private static BunitContext Contexto(
        Func<object, object> responder, Guid tenant, out MediatorContador mediador,
        out Microsoft.AspNetCore.Components.Infrastructure.ComponentStatePersistenceManager gestor,
        bool interactivo = false)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var contador = new MediatorContador(responder);        mediador = contador;
        ctx.Services.AddScoped<IMediator>(_ => contador);
        ctx.Services.AddScoped<ToastService>();
        ctx.Services.AddScoped<ContextWorkspaceService>();
        ctx.Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        ctx.Services.AddScoped<IValidator<CrearEmpresaCommand>>(_ => new InlineValidator<CrearEmpresaCommand>());
        ctx.Services.AddScoped<IValidator<CrearSubcontrataCommand>>(_ => new InlineValidator<CrearSubcontrataCommand>());
        gestor = ctx.AddEstadoDePantallaPersistidoParaPruebas(interactivo: interactivo, conSesion: true, tenant: tenant);
        return ctx;
    }

    /// <summary>
    /// Monta <typeparamref name="TPagina"/> como prerender con
    /// <paramref name="urlPrerender"/>, serializa su estado y la monta otra vez
    /// como circuito con <paramref name="urlCircuito"/>, con la sesión del
    /// Tenant <paramref name="tenantDelCircuito"/> y sus propios datos.
    /// </summary>
    private static async Task<Resultado> PrerenderYCircuitoAsync<TPagina>(
        Func<object, object> datosDelPrerender, Func<object, object> datosDelCircuito,
        string urlPrerender, string urlCircuito, Guid tenantDelCircuito,
        Func<string, bool>? esperaCarga = null, Func<IRenderedComponent<TPagina>, Task>? despues = null)
        where TPagina : IComponent
    {
        var almacen = new AlmacenEnMemoria();

        using (var prerender = Contexto(datosDelPrerender, TenantX, out _, out var gestorPrerender))
        {
            prerender.Services.GetRequiredService<NavigationManager>().NavigateTo(urlPrerender);
            var cut = prerender.Render<TPagina>();
            cut.WaitForState(() => esperaCarga?.Invoke(cut.Markup) ?? true);
            await gestorPrerender.PersistStateAsync(almacen, prerender.Renderer);
        }

        using var circuito = Contexto(
            datosDelCircuito, tenantDelCircuito, out var mediadorCircuito, out var gestorCircuito, interactivo: true);
        await gestorCircuito.RestoreStateAsync(almacen);
        circuito.Services.GetRequiredService<NavigationManager>().NavigateTo(urlCircuito);
        var cutCircuito = circuito.Render<TPagina>();
        cutCircuito.WaitForState(() => esperaCarga?.Invoke(cutCircuito.Markup) ?? true);
        if (despues is not null)
            await despues(cutCircuito);

        return new Resultado(mediadorCircuito, cutCircuito.Markup, almacen.Contenido.Count);
    }

    // ── El circuito interactivo no persiste ─────────────────────────────────────

    /// <summary>
    /// Solo el prerender persiste. En el circuito no se paga la huella de
    /// sesión (para un workspace delegado consulta la base) ni se anota nada.
    /// </summary>
    [Theory]
    [InlineData("empresas")]
    [InlineData("subcontratas")]
    [InlineData("incidencias")]
    public async Task El_circuito_interactivo_no_persiste_nada_y_el_prerender_si(string ruta)
    {
        foreach (var interactivo in new[] { false, true })
        {
            var (datos, texto) = ruta switch
            {
                "empresas" => (DatosEmpresas("Empresa cargada"), "Empresa cargada"),
                "subcontratas" => (DatosSubcontratas("Sub cargada"), "Sub cargada"),
                _ => (DatosIncidencias("Centro cargado"), "Centro cargado"),
            };
            using var ctx = Contexto(datos, TenantX, out _, out var gestor, interactivo);
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(ruta);
            IRenderedComponent<IComponent> cut = ruta switch
            {
                "empresas" => ctx.Render<Empresas>(),
                "subcontratas" => ctx.Render<Subcontratas>(),
                _ => ctx.Render<Incidencias>(),
            };
            cut.WaitForState(() => cut.Markup.Contains(texto));

            var almacen = new AlmacenEnMemoria();
            await gestor.PersistStateAsync(almacen, ctx.Renderer);

            if (interactivo)
                almacen.Contenido.Should().BeEmpty($"/{ruta} en el circuito no debe persistir");
            else
                almacen.Contenido.Should().HaveCount(1, $"/{ruta} en el prerender sí persiste");
        }
    }

    // ── Empresas ────────────────────────────────────────────────────────────────

    private static Func<object, object> DatosEmpresas(string razonSocial) => peticion => peticion switch
    {
        ObtenerPerfilVocabularioActualQuery => PerfilVocabularioTenant.Consultora,
        ObtenerClientesParaSelectorQuery => Array.Empty<ClienteSelectorDto>(),
        ObtenerEmpresasQuery q => new ResultadoPaginado<EmpresaListaDto>(
            [new EmpresaListaDto(Guid.NewGuid(), razonSocial, "B-11111111", DateTime.UtcNow)], 1, q.Pagina, q.TamanoPagina),
        _ => throw new NotSupportedException(peticion.GetType().Name),
    };

    [Fact]
    public async Task Empresas_el_circuito_recoge_lo_del_prerender_sin_repetir_ninguna_consulta()
    {
        var r = await PrerenderYCircuitoAsync<Empresas>(
            DatosEmpresas("Empresa del prerender"), DatosEmpresas("Empresa del circuito"),
            "empresas", "empresas", TenantX, m => m.Contains("Empresa del"));

        r.EntradasPersistidas.Should().Be(1);
        r.Circuito.Peticiones.Should().BeEmpty("la lista y el perfil de vocabulario viajaron en el estado");
        r.Markup.Should().Contain("Empresa del prerender").And.NotContain("Empresa del circuito");
    }

    /// <summary>
    /// Una carga que empieza y falla no puede dejar publicada la lista de la
    /// carga anterior (p. ej. la que aún tiene la fila que se acaba de
    /// eliminar): la pantalla enseña el error y lo único honesto que puede
    /// persistir es nada.
    /// </summary>
    [Fact]
    public async Task Empresas_una_carga_que_falla_no_deja_persistida_la_lista_de_la_carga_anterior()
    {
        var falla = false;
        var datos = DatosEmpresas("Empresa buena");
        using var ctx = Contexto(
            peticion => falla && peticion is ObtenerEmpresasQuery ? throw new InvalidOperationException("BD caída") : datos(peticion),
            TenantX, out _, out var gestor);
        var navegacion = ctx.Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo("empresas");
        var cut = ctx.Render<Empresas>();
        cut.WaitForState(() => cut.Markup.Contains("Empresa buena"));

        falla = true;
        navegacion.NavigateTo("empresas?q=acme");
        cut.WaitForState(() => !cut.Markup.Contains("Empresa buena"));

        var almacen = new AlmacenEnMemoria();
        await gestor.PersistStateAsync(almacen, ctx.Renderer);

        almacen.Contenido.Should().BeEmpty("la última carga falló: no hay nada verdadero que dejar al circuito");
    }

    [Fact]
    public async Task Empresas_otro_filtro_en_la_URL_del_circuito_consulta_de_nuevo()
    {
        var r = await PrerenderYCircuitoAsync<Empresas>(
            DatosEmpresas("Empresa del prerender"), DatosEmpresas("Empresa del circuito"),
            "empresas", "empresas?q=acme", TenantX, m => m.Contains("Empresa del"));

        r.Circuito.Veces<ObtenerEmpresasQuery>().Should().Be(1);
        r.Markup.Should().Contain("Empresa del circuito").And.NotContain("Empresa del prerender");
    }

    [Fact]
    public async Task Empresas_otro_Tenant_propietario_en_el_circuito_no_ve_las_empresas_del_prerender()
    {
        var r = await PrerenderYCircuitoAsync<Empresas>(
            DatosEmpresas("Empresa del Tenant X"), DatosEmpresas("Empresa del Tenant Y"),
            "empresas", "empresas", TenantY, m => m.Contains("Empresa del Tenant"));

        r.Circuito.Veces<ObtenerEmpresasQuery>().Should().Be(1);
        r.Markup.Should().Contain("Empresa del Tenant Y").And.NotContain("Empresa del Tenant X");
    }

    // ── Subcontratas ────────────────────────────────────────────────────────────

    private static Func<object, object> DatosSubcontratas(string razonSocial) => peticion => peticion switch
    {
        ObtenerPerfilVocabularioActualQuery => PerfilVocabularioTenant.Consultora,
        ObtenerClientesParaSelectorQuery => Array.Empty<ClienteSelectorDto>(),
        ObtenerEmpresasParaSelectorQuery => Array.Empty<EmpresaSelectorDto>(),
        ObtenerSubcontratasQuery q => new ResultadoPaginado<SubcontrataListaDto>(
            [new SubcontrataListaDto(
                Guid.NewGuid(), razonSocial, "B-22222222", DateTime.UtcNow,
                NivelServicioSubcontrata.Gestionada, 100, RecuentosSubcontrataDto.Vacio)],
            1, q.Pagina, q.TamanoPagina),
        _ => throw new NotSupportedException(peticion.GetType().Name),
    };

    [Fact]
    public async Task Subcontratas_el_circuito_recoge_lo_del_prerender_sin_repetir_la_consulta()
    {
        var r = await PrerenderYCircuitoAsync<Subcontratas>(
            DatosSubcontratas("Sub del prerender"), DatosSubcontratas("Sub del circuito"),
            "subcontratas", "subcontratas", TenantX, m => m.Contains("Sub del"));

        r.EntradasPersistidas.Should().Be(1);
        r.Circuito.Veces<ObtenerSubcontratasQuery>().Should().Be(0);
        r.Markup.Should().Contain("Sub del prerender").And.NotContain("Sub del circuito");
    }

    [Fact]
    public async Task Subcontratas_una_carga_que_falla_no_deja_persistida_la_lista_de_la_carga_anterior()
    {
        var falla = false;
        var datos = DatosSubcontratas("Sub buena");
        using var ctx = Contexto(
            peticion => falla && peticion is ObtenerSubcontratasQuery ? throw new InvalidOperationException("BD caída") : datos(peticion),
            TenantX, out _, out var gestor);
        var navegacion = ctx.Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo("subcontratas");
        var cut = ctx.Render<Subcontratas>();
        cut.WaitForState(() => cut.Markup.Contains("Sub buena"));

        falla = true;
        navegacion.NavigateTo("subcontratas?q=acme");
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Sub buena"));

        var almacen = new AlmacenEnMemoria();
        await gestor.PersistStateAsync(almacen, ctx.Renderer);

        almacen.Contenido.Should().BeEmpty("la última carga falló: no hay nada verdadero que dejar al circuito");
    }

    /// <summary>
    /// Refrescar UNA fila en sitio (tras gestionar un documento desde el
    /// acordeón) cambia lo que la pantalla enseña sin pasar por una carga de
    /// lista: lo anotado por la carga tendría la fila anterior.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Subcontratas_refrescar_una_fila_en_sitio_descarta_lo_anotado_por_la_carga(bool laFilaSigueExistiendo)
    {
        var id = Guid.NewGuid();
        SubcontrataListaDto Fila(string razonSocial) => new(
            id, razonSocial, "B-22222222", DateTime.UtcNow, NivelServicioSubcontrata.Gestionada, 100, RecuentosSubcontrataDto.Vacio);
        using var ctx = Contexto(
            peticion => peticion switch
            {
                ObtenerPerfilVocabularioActualQuery => PerfilVocabularioTenant.Consultora,
                ObtenerClientesParaSelectorQuery => Array.Empty<ClienteSelectorDto>(),
                ObtenerEmpresasParaSelectorQuery => Array.Empty<EmpresaSelectorDto>(),
                ObtenerSubcontratasQuery { SubcontrataId: not null } q =>
                    new ResultadoPaginado<SubcontrataListaDto>(
                        laFilaSigueExistiendo ? [Fila("Sub refrescada")] : [], laFilaSigueExistiendo ? 1 : 0, q.Pagina, q.TamanoPagina),
                ObtenerSubcontratasQuery q =>
                    new ResultadoPaginado<SubcontrataListaDto>([Fila("Sub cargada")], 1, q.Pagina, q.TamanoPagina),
                _ => throw new NotSupportedException(peticion.GetType().Name),
            },
            TenantX, out _, out var gestor);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("subcontratas");
        var cut = ctx.Render<Subcontratas>();
        cut.WaitForState(() => cut.Markup.Contains("Sub cargada"));

        var refrescar = typeof(Subcontratas).GetMethod(
            "RefrescarSubcontrataAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)refrescar.Invoke(cut.Instance, [id])!);
        if (laFilaSigueExistiendo)
            cut.Markup.Should().Contain("Sub refrescada");

        var almacen = new AlmacenEnMemoria();
        await gestor.PersistStateAsync(almacen, ctx.Renderer);

        almacen.Contenido.Should().BeEmpty("lo anotado por la carga tiene la fila de antes de refrescar");
    }

    [Fact]
    public async Task Subcontratas_otra_busqueda_en_la_URL_del_circuito_consulta_de_nuevo()
    {
        var r = await PrerenderYCircuitoAsync<Subcontratas>(
            DatosSubcontratas("Sub del prerender"), DatosSubcontratas("Sub del circuito"),
            "subcontratas", "subcontratas?q=acme", TenantX, m => m.Contains("Sub del"));

        r.Circuito.Veces<ObtenerSubcontratasQuery>().Should().Be(1);
        r.Markup.Should().Contain("Sub del circuito").And.NotContain("Sub del prerender");
    }

    [Fact]
    public async Task Subcontratas_otro_Tenant_propietario_en_el_circuito_no_ve_las_del_prerender()
    {
        var r = await PrerenderYCircuitoAsync<Subcontratas>(
            DatosSubcontratas("Sub del Tenant X"), DatosSubcontratas("Sub del Tenant Y"),
            "subcontratas", "subcontratas", TenantY, m => m.Contains("Sub del Tenant"));

        r.Circuito.Veces<ObtenerSubcontratasQuery>().Should().Be(1);
        r.Markup.Should().Contain("Sub del Tenant Y").And.NotContain("Sub del Tenant X");
    }

    // ── Incidencias ─────────────────────────────────────────────────────────────

    private static Func<object, object> DatosIncidencias(string centro) => peticion => peticion switch
    {
        ObtenerCentrosParaSelectorQuery => Array.Empty<CentroSelectorDto>(),
        ObtenerTrabajadoresParaSelectorQuery => Array.Empty<TrabajadorSelectorDto>(),
        EliminarIncidenciaCommand => Result.Exito(),
        ObtenerIncidenciasQuery q => new ResultadoPaginado<IncidenciaListaDto>(
            [new IncidenciaListaDto(
                Guid.NewGuid(), Guid.NewGuid(), centro, null, null, TipoIncidencia.Accidente,
                GravedadIncidencia.Leve, new DateOnly(2026, 9, 1), false)],
            1, q.Pagina, q.TamanoPagina),
        _ => throw new NotSupportedException(peticion.GetType().Name),
    };

    /// <summary>
    /// La recarga tras eliminar una incidencia (misma huella que la carga
    /// anterior) que falla no puede dejar persistida la lista con la fila que
    /// se acaba de eliminar.
    /// </summary>
    [Fact]
    public async Task Incidencias_una_recarga_que_falla_tras_eliminar_no_deja_persistida_la_lista_anterior()
    {
        var falla = false;
        var datos = DatosIncidencias("Centro bueno");
        using var ctx = Contexto(
            peticion => falla && peticion is ObtenerIncidenciasQuery ? throw new InvalidOperationException("BD caída") : datos(peticion),
            TenantX, out _, out var gestor);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("incidencias");
        var cut = ctx.Render<Incidencias>();
        cut.WaitForState(() => cut.Markup.Contains("Centro bueno"));

        falla = true;
        await cut.FindAll(".menu-acciones-disparador")[0].ClickAsync(new());
        await cut.FindAll(".menu-acciones-item").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new());
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new());
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Centro bueno"));

        var almacen = new AlmacenEnMemoria();
        await gestor.PersistStateAsync(almacen, ctx.Renderer);

        almacen.Contenido.Should().BeEmpty("la recarga falló: la lista anterior aún tiene la fila eliminada");
    }

    [Fact]
    public async Task Incidencias_el_circuito_recoge_lo_del_prerender_sin_repetir_la_consulta()
    {
        var r = await PrerenderYCircuitoAsync<Incidencias>(
            DatosIncidencias("Centro del prerender"), DatosIncidencias("Centro del circuito"),
            "incidencias", "incidencias", TenantX, m => m.Contains("Centro del"));

        r.EntradasPersistidas.Should().Be(1);
        r.Circuito.Veces<ObtenerIncidenciasQuery>().Should().Be(0);
        r.Markup.Should().Contain("Centro del prerender").And.NotContain("Centro del circuito");
    }

    /// <summary>
    /// Una acción del usuario nunca se contesta con estado persistido: tras
    /// eliminar, la lista se pide otra vez a la base aunque la consulta sea
    /// idéntica a la que dejó el prerender (misma huella).
    /// </summary>
    [Fact]
    public async Task Incidencias_tras_una_accion_del_usuario_la_lista_se_pide_a_la_base_aunque_la_huella_no_cambie()
    {
        var r = await PrerenderYCircuitoAsync<Incidencias>(
            DatosIncidencias("Centro del prerender"), DatosIncidencias("Centro del circuito"),
            "incidencias", "incidencias", TenantX, m => m.Contains("Centro del"),
            despues: async cut =>
            {
                cut.Markup.Should().Contain("Centro del prerender", "antes de actuar se ve lo que recogió del prerender");
                await cut.FindAll(".menu-acciones-disparador")[0].ClickAsync(new());
                await cut.FindAll(".menu-acciones-item").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new());
                await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new());
                cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro del circuito"));
            });

        r.Circuito.Peticiones.Should().Contain(typeof(EliminarIncidenciaCommand));
        r.Circuito.Veces<ObtenerIncidenciasQuery>().Should().BeGreaterThan(0);
        r.Markup.Should().NotContain("Centro del prerender");
    }

    /// <summary>
    /// Ordenar por una columna es otra pregunta que la del prerender: la
    /// recogida no puede contestarla con las filas en el orden anterior.
    /// </summary>
    [Fact]
    public async Task Incidencias_ordenar_por_una_columna_tras_recoger_el_estado_pregunta_a_la_base_con_ese_orden()
    {
        var r = await PrerenderYCircuitoAsync<Incidencias>(
            DatosIncidencias("Centro del prerender"), DatosIncidencias("Centro del circuito"),
            "incidencias", "incidencias", TenantX, m => m.Contains("Centro del"),
            despues: async cut =>
            {
                cut.Markup.Should().Contain("Centro del prerender");
                await cut.FindAll("thead th").Single(th => th.TextContent.Trim() == "Centro")
                    .QuerySelector("button")!.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
                cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro del circuito"));
            });

        r.Circuito.Enviadas.OfType<ObtenerIncidenciasQuery>().Last().OrdenarPor
            .Should().Be(nameof(IncidenciaListaDto.CentroNombre));
        r.Markup.Should().NotContain("Centro del prerender");
    }

    [Fact]
    public async Task Incidencias_otro_filtro_en_la_URL_del_circuito_consulta_de_nuevo()
    {
        var r = await PrerenderYCircuitoAsync<Incidencias>(
            DatosIncidencias("Centro del prerender"), DatosIncidencias("Centro del circuito"),
            "incidencias", "incidencias?estado=Resuelta", TenantX, m => m.Contains("Centro del"));

        r.Circuito.Veces<ObtenerIncidenciasQuery>().Should().BeGreaterThan(0,
            "QuickGrid puede pedir la misma página más de una vez; lo que importa es que se pregunta a la base");
        r.Markup.Should().Contain("Centro del circuito").And.NotContain("Centro del prerender");
    }

    [Fact]
    public async Task Incidencias_otro_Tenant_propietario_en_el_circuito_no_ve_las_del_prerender()
    {
        var r = await PrerenderYCircuitoAsync<Incidencias>(
            DatosIncidencias("Centro del Tenant X"), DatosIncidencias("Centro del Tenant Y"),
            "incidencias", "incidencias", TenantY, m => m.Contains("Centro del Tenant"));

        r.Circuito.Veces<ObtenerIncidenciasQuery>().Should().BeGreaterThan(0,
            "QuickGrid puede pedir la misma página más de una vez; lo que importa es que se pregunta a la base");
        r.Markup.Should().Contain("Centro del Tenant Y").And.NotContain("Centro del Tenant X");
    }
}
