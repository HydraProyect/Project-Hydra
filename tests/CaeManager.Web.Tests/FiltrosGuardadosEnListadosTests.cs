using System.Web;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Tenants.Queries.UsaRotulosPrimeraPersona;
using CaeManager.Application.Usuarios.Queries.ObtenerOperadoresCaeDeMiTenant;
using CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera;
using CaeManager.Application.Vehiculos.Commands.CrearVehiculo;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Pages;
using CaeManager.Web.Features.Empresas.Pages;
using CaeManager.Web.Features.Gestiones.Pages;
using CaeManager.Web.Features.Proyectos.Pages;
using CaeManager.Web.Features.Subcontratas.Pages;
using CaeManager.Web.Features.Vehiculos.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Filtros guardados en los seis listados que los reciben con la pieza compartida
/// (<see cref="FiltrosGuardadosDeListado"/>): Empresas, Centros, Subcontratas, Vehículos, Proyectos y
/// Gestiones. Por cada uno:
/// <list type="bullet">
/// <item><description><b>guardar</b> con filtros activos envía los parámetros de vista que la URL lleva, y no los demás;</description></item>
/// <item><description><b>aplicar</b> deja la URL y la consulta con los filtros del filtro guardado, quita los que no trae
/// y lo hace en una sola navegación;</description></item>
/// <item><description>un valor guardado <b>no es autoridad</b>: el que no pasa la validación de la URL se ignora.</description></item>
/// </list>
/// La pieza sola (modal, error de nombre repetido, borrado) está en <see cref="FiltrosGuardadosDeListadoTests"/>.
/// </summary>
public class FiltrosGuardadosEnListadosTests : BunitContext
{
    private static readonly Guid ClienteA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClienteB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid EmpresaE = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SubcontrataS = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly MediatorDeListado _mediador = new();

    public FiltrosGuardadosEnListadosTests()
    {
        // Las páginas montan AtajosListaTeclado, que importa ./js/atajos-lista.js: fuera de lo que se observa aquí.
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba());
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearEmpresaCommand>>(_ => new InlineValidator<CrearEmpresaCommand>());
        Services.AddScoped<IValidator<CrearCentroCommand>>(_ => new InlineValidator<CrearCentroCommand>());
        Services.AddScoped<IValidator<CrearSubcontrataCommand>>(_ => new InlineValidator<CrearSubcontrataCommand>());
        Services.AddScoped<IValidator<CrearVehiculoCommand>>(_ => new InlineValidator<CrearVehiculoCommand>());
    }

    /// <summary>Responde a las consultas de los seis listados con listas vacías y apunta todo lo que se le envía.</summary>
    private sealed class MediatorDeListado : IMediator
    {
        public List<object> Enviadas { get; } = [];
        public List<FiltroGuardadoDto> FiltrosGuardados { get; } = [];

        /// <summary>Se llama al recibir cada petición, antes de responderla: para mirar cómo está la página en ese instante.</summary>
        public Action<object>? AlEnviar { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            AlEnviar?.Invoke(request);
            object respuesta = request switch
            {
                ObtenerFiltrosGuardadosQuery => (IReadOnlyList<FiltroGuardadoDto>)FiltrosGuardados.ToList(),
                GuardarFiltroCommand => Result.Exito(Guid.NewGuid()),

                ObtenerEmpresasQuery q => new ResultadoPaginado<EmpresaListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerCentrosQuery q => new ResultadoPaginado<CentroListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerSubcontratasQuery q => new ResultadoPaginado<SubcontrataListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerVehiculosQuery q => new ResultadoPaginado<VehiculoListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerGestionesQuery q => new ResultadoPaginado<GestionListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerProyectosQuery q => new ResultadoPaginado<ProyectoListaDto>([], 0, q.Pagina, q.TamanoPagina),

                ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)
                    [new ClienteSelectorDto(ClienteA, "Refrielectric S.L."), new ClienteSelectorDto(ClienteB, "Frigoríficos Arcos S.A.")],
                ObtenerCentrosParaSelectorQuery => (IReadOnlyList<CentroSelectorDto>)[],
                ObtenerEmpresasParaSelectorQuery => new[] { new EmpresaSelectorDto(EmpresaE, "Montajes Ebro S.L.") },
                ObtenerSubcontratasParaSelectorQuery => new[] { new SubcontrataSelectorDto(SubcontrataS, "Aislamientos Nervión S.L.") },
                ObtenerProximaVisitaPorCentroQuery =>
                    (IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>>)new Dictionary<Guid, IReadOnlyList<VisitaResumenDto>>(),

                ObtenerPerfilVocabularioActualQuery => PerfilVocabularioTenant.Consultora,
                UsaRotulosPrimeraPersonaQuery => false,
                ObtenerClientesAutorizadosQuery => (IReadOnlyList<ClienteAutorizadoDto>)[new(Guid.NewGuid(), "Propia", EsOrigen: true)],
                ObtenerAlcanceCeroQuery => false,
                ObtenerCandidatosIncorporacionCarteraQuery => Result.Exito<IReadOnlyList<CandidatoIncorporacionCarteraDto>>([]),
                ObtenerPersonasConCarteraQuery => (IReadOnlyList<CarterasDeOperacion>)[],
                ObtenerOperadoresCaeDeMiTenantQuery => (IReadOnlyList<CarterasDeOperacion>)[],
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta);
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
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    // ------------------------------------------------------------ ayudantes

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    /// <summary>Los filtros son [SupplyParameterFromQuery]: se llega a ellos navegando a la URI que los lleva, como en el producto.</summary>
    private IRenderedComponent<TPagina> Renderizar<TPagina>(string ruta) where TPagina : IComponent
    {
        Navegacion.NavigateTo(ruta);
        return Render<TPagina>();
    }

    private FiltroGuardadoDto ConFiltroGuardado(string nombre, string valoresJson)
    {
        var filtro = new FiltroGuardadoDto(Guid.NewGuid(), nombre, valoresJson, DateTime.UtcNow);
        _mediador.FiltrosGuardados.Add(filtro);
        return filtro;
    }

    private static IElement Pastilla<TPagina>(IRenderedComponent<TPagina> cut, string etiqueta) where TPagina : IComponent =>
        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla")
            .Single(b => b.GetAttribute("aria-label") is { } nombre && (nombre == etiqueta || nombre.StartsWith(etiqueta + ": ", StringComparison.Ordinal)));

    /// <summary>Abre «Más filtros» y pulsa el ítem con ese texto (un filtro guardado o «Guardar filtro»).</summary>
    private static async Task PulsarEnMasFiltrosAsync<TPagina>(IRenderedComponent<TPagina> cut, string item) where TPagina : IComponent
    {
        await Pastilla(cut, "Más filtros").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".barra-filtros-pastillas [role=menuitem]")
            .Single(i => i.TextContent.Trim() == item).ClickAsync(new MouseEventArgs());
    }

    /// <summary>«Guardar filtro» de la barra, un nombre y «Guardar»: devuelve el comando que salió.</summary>
    private async Task<GuardarFiltroCommand> GuardarFiltroAsync<TPagina>(IRenderedComponent<TPagina> cut, string nombre) where TPagina : IComponent
    {
        await PulsarEnMasFiltrosAsync(cut, "Guardar filtro");
        await cut.Find("[role=dialog] input").InputAsync(new ChangeEventArgs { Value = nombre });
        await cut.FindAll("[role=dialog] .modal-pie button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=dialog]").Should().BeEmpty("guardado el filtro, el modal se cierra");
        return _mediador.Enviadas.OfType<GuardarFiltroCommand>().Should().ContainSingle().Subject;
    }

    /// <summary>
    /// Aplica el filtro guardado desde «Más filtros» y devuelve los parámetros que quedan en la URL. Afirma
    /// de paso que la URL se escribió en UNA navegación: varias seguidas se pisan (ActualizarFiltrosEnUrl).
    /// </summary>
    private async Task<Dictionary<string, string>> AplicarAsync<TPagina>(IRenderedComponent<TPagina> cut, string nombre) where TPagina : IComponent
    {
        // Se cuentan los avisos de cambio de dirección y no el historial: con replace: true el historial
        // de bUnit sustituye su última entrada y mide lo mismo con una navegación que con ninguna.
        var navegaciones = 0;
        void Contar(object? _, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs __) => navegaciones++;
        Navegacion.LocationChanged += Contar;
        try
        {
            await PulsarEnMasFiltrosAsync(cut, nombre);
        }
        finally
        {
            Navegacion.LocationChanged -= Contar;
        }

        navegaciones.Should().Be(1, "aplicar un filtro guardado escribe la URL en una sola navegación");
        return ParametrosDeLaUrl();
    }

    private Dictionary<string, string> ParametrosDeLaUrl()
    {
        var consulta = HttpUtility.ParseQueryString(new Uri(Navegacion.Uri).Query);
        return consulta.AllKeys.ToDictionary(k => k!, k => consulta[k]!);
    }

    private int Consultas<TConsulta>() => _mediador.Enviadas.OfType<TConsulta>().Count();

    private TConsulta Ultima<TConsulta>() => _mediador.Enviadas.OfType<TConsulta>().Last();

    // ------------------------------------------------------------- Empresas

    [Fact]
    public async Task Empresas_guardar_envia_los_parametros_de_vista_de_la_url()
    {
        var cut = Renderizar<Empresas>($"empresas?q=Ebro&estado=Vencido&ClienteId={ClienteA}");

        var comando = await GuardarFiltroAsync(cut, "Vencidas de Ebro");

        comando.Pantalla.Should().Be(PantallasConFiltrosGuardados.Empresas);
        comando.ValoresJson.Should().Be("{\"q\":\"Ebro\",\"estado\":\"Vencido\"}", "ClienteId es la precarga del alta, no la vista");
    }

    [Fact]
    public async Task Empresas_aplicar_deja_url_y_consulta_con_el_filtro_y_quita_lo_que_no_trae()
    {
        ConFiltroGuardado("Vencidas", "{\"estado\":\"Vencido\"}");
        var cut = Renderizar<Empresas>("empresas?q=Ebro&estado=Urgente");
        var consultasAntes = Consultas<ObtenerEmpresasQuery>();

        var url = await AplicarAsync(cut, "Vencidas");

        url.Should().BeEquivalentTo(new Dictionary<string, string> { ["estado"] = "Vencido" }, "la búsqueda no está en el filtro guardado: se quita");
        (Consultas<ObtenerEmpresasQuery>() - consultasAntes).Should().Be(1, "una recarga, no dos");
        Ultima<ObtenerEmpresasQuery>().Should().Match<ObtenerEmpresasQuery>(q => q.Busqueda == null && q.EstadoDocumental == "Vencido" && q.Pagina == 1);
        cut.FindAll(".chip-filtro").Should().BeEmpty("el chip de la búsqueda desaparece con ella");
    }

    [Fact]
    public async Task Empresas_un_estado_guardado_que_ya_no_existe_se_ignora()
    {
        ConFiltroGuardado("Antiguo", "{\"q\":\"Ebro\",\"estado\":\"Inventado\"}");
        var cut = Renderizar<Empresas>("empresas?estado=Vencido");

        var url = await AplicarAsync(cut, "Antiguo");

        url.Should().BeEquivalentTo(new Dictionary<string, string> { ["q"] = "Ebro" });
        Ultima<ObtenerEmpresasQuery>().Should().Match<ObtenerEmpresasQuery>(q => q.Busqueda == "Ebro" && q.EstadoDocumental == null);
    }

    // -------------------------------------------------------------- Centros

    [Fact]
    public async Task Centros_guardar_envia_filtros_agrupacion_y_orden_pero_no_el_Centro_ni_las_precargas_del_alta()
    {
        var cut = Renderizar<Centros>(
            $"centros?q=Nave&estado=Vencido&cliente={ClienteA}&empresa={EmpresaE}&agrupar=no&orden=cumplimiento-desc"
            + $"&CentroId={Guid.NewGuid()}&ClienteId={ClienteB}&EmpresaId={EmpresaE}&Nombre=Nuevo");

        var comando = await GuardarFiltroAsync(cut, "Nave vencida");

        comando.Pantalla.Should().Be(PantallasConFiltrosGuardados.Centros);
        comando.ValoresJson.Should().Be(
            $"{{\"q\":\"Nave\",\"estado\":\"Vencido\",\"cliente\":\"{ClienteA}\",\"empresa\":\"{EmpresaE}\",\"agrupar\":\"no\",\"orden\":\"cumplimiento-desc\"}}");
    }

    [Fact]
    public async Task Centros_aplicar_deja_url_y_consulta_con_el_filtro_y_quita_lo_que_no_trae()
    {
        ConFiltroGuardado("Peores de A", $"{{\"cliente\":\"{ClienteA}\",\"orden\":\"cumplimiento-desc\"}}");
        var cut = Renderizar<Centros>($"centros?q=Nave&estado=Vencido&empresa={EmpresaE}&agrupar=no&orden=cumplimiento");
        var consultasAntes = Consultas<ObtenerCentrosQuery>();

        var url = await AplicarAsync(cut, "Peores de A");

        url.Should().BeEquivalentTo(
            new Dictionary<string, string> { ["cliente"] = ClienteA.ToString(), ["orden"] = "cumplimiento-desc" },
            "búsqueda, estado, Empresa y «agrupar=no» no están en el filtro guardado: se quitan y vuelve la agrupación de fábrica");
        (Consultas<ObtenerCentrosQuery>() - consultasAntes).Should().Be(1, "una recarga, no dos");
        Ultima<ObtenerCentrosQuery>().Should().Match<ObtenerCentrosQuery>(q =>
            q.Busqueda == null && q.ClienteId == ClienteA && q.EmpresaId == null && q.Estados == null
            && q.OrdenarPor == nameof(CentroListaDto.CumplimientoPorcentaje) && q.Descendente && q.Pagina == 1);
    }

    [Fact]
    public async Task Centros_aplicar_conserva_agrupar_no_y_el_orden_ascendente_cuando_el_filtro_los_trae()
    {
        ConFiltroGuardado("Sin agrupar", "{\"estado\":\"Vencido,Urgente\",\"agrupar\":\"no\",\"orden\":\"cumplimiento\"}");
        var cut = Renderizar<Centros>("centros?q=Nave");

        var url = await AplicarAsync(cut, "Sin agrupar");

        url.Keys.Should().BeEquivalentTo(["estado", "agrupar", "orden"]);
        url["estado"].Split(',').Should().BeEquivalentTo(["Vencido", "Urgente"]);
        url["agrupar"].Should().Be("no");
        url["orden"].Should().Be("cumplimiento");
        Ultima<ObtenerCentrosQuery>().Should().Match<ObtenerCentrosQuery>(q =>
            q.Busqueda == null && q.OrdenarPor == nameof(CentroListaDto.CumplimientoPorcentaje) && !q.Descendente);
        Ultima<ObtenerCentrosQuery>().Estados.Should().BeEquivalentTo([EstadoCentro.Vencido, EstadoCentro.Urgente]);
    }

    [Fact]
    public async Task Centros_los_valores_guardados_que_no_pasan_la_validacion_de_la_url_se_ignoran()
    {
        ConFiltroGuardado("Antiguo", "{\"q\":\"Nave\",\"estado\":\"Inventado\",\"cliente\":\"no-es-un-guid\",\"empresa\":\"7\",\"orden\":\"alfabetico\",\"agrupar\":\"si\"}");
        var cut = Renderizar<Centros>($"centros?cliente={ClienteA}&orden=cumplimiento");

        var url = await AplicarAsync(cut, "Antiguo");

        url.Should().BeEquivalentTo(new Dictionary<string, string> { ["q"] = "Nave" },
            "un Id que no es Guid, un estado y un orden desconocidos no filtran; «agrupar» solo viaja cuando es «no»");
        Ultima<ObtenerCentrosQuery>().Should().Match<ObtenerCentrosQuery>(q =>
            q.Busqueda == "Nave" && q.ClienteId == null && q.EmpresaId == null && q.Estados == null && q.OrdenarPor == null);
    }

    // --------------------------------------------------------- Subcontratas

    [Fact]
    public async Task Subcontratas_guardar_envia_los_parametros_de_vista_de_la_url()
    {
        var cut = Renderizar<Subcontratas>("subcontratas?q=Aislamientos&nivel=Supervisada&pagina=2");

        var comando = await GuardarFiltroAsync(cut, "Supervisadas");

        comando.Pantalla.Should().Be(PantallasConFiltrosGuardados.Subcontratas);
        comando.ValoresJson.Should().Be("{\"q\":\"Aislamientos\",\"nivel\":\"Supervisada\"}");
    }

    [Fact]
    public async Task Subcontratas_aplicar_deja_url_y_consulta_con_el_filtro_y_quita_lo_que_no_trae()
    {
        ConFiltroGuardado("Gestionadas", "{\"nivel\":\"Gestionada\"}");
        var cut = Renderizar<Subcontratas>("subcontratas?q=Aislamientos&nivel=Supervisada");
        var consultasAntes = Consultas<ObtenerSubcontratasQuery>();

        var url = await AplicarAsync(cut, "Gestionadas");

        url.Should().BeEquivalentTo(new Dictionary<string, string> { ["nivel"] = "Gestionada" });
        (Consultas<ObtenerSubcontratasQuery>() - consultasAntes).Should().Be(1, "una recarga, no dos");
        Ultima<ObtenerSubcontratasQuery>().Should().Match<ObtenerSubcontratasQuery>(q =>
            q.Busqueda == null && q.NivelServicio == NivelServicioSubcontrata.Gestionada && q.Pagina == 1);
    }

    [Fact]
    public async Task Subcontratas_un_nivel_guardado_que_no_existe_se_ignora()
    {
        ConFiltroGuardado("Antiguo", "{\"q\":\"Nervión\",\"nivel\":\"Premium\"}");
        var cut = Renderizar<Subcontratas>("subcontratas?nivel=Supervisada");

        var url = await AplicarAsync(cut, "Antiguo");

        url.Should().BeEquivalentTo(new Dictionary<string, string> { ["q"] = "Nervión" });
        Ultima<ObtenerSubcontratasQuery>().Should().Match<ObtenerSubcontratasQuery>(q => q.Busqueda == "Nervión" && q.NivelServicio == null);
    }

    // ------------------------------------------------------------ Vehículos

    [Fact]
    public async Task Vehiculos_guardar_envia_los_parametros_de_vista_de_la_url()
    {
        var cut = Renderizar<Vehiculos>($"vehiculos?q=Transit&estado=Vencido&empresa={EmpresaE}");

        var comando = await GuardarFiltroAsync(cut, "Transit vencidas");

        comando.Pantalla.Should().Be(PantallasConFiltrosGuardados.Vehiculos);
        comando.ValoresJson.Should().Be($"{{\"q\":\"Transit\",\"estado\":\"Vencido\",\"empresa\":\"{EmpresaE}\"}}");
    }

    /// <summary>
    /// En Vehículos <c>OnParametersSet</c> sincroniza los campos pero no recarga: si aplicar solo navegara, la
    /// rejilla seguiría enseñando la lista del filtro anterior. Por eso se mira la consulta, no solo la URL.
    /// </summary>
    [Fact]
    public async Task Vehiculos_aplicar_recarga_con_el_filtro_deja_la_url_igual_y_quita_lo_que_no_trae()
    {
        ConFiltroGuardado("De la subcontrata", $"{{\"subcontrata\":\"{SubcontrataS}\"}}");
        var cut = Renderizar<Vehiculos>($"vehiculos?q=Transit&estado=Vencido&empresa={EmpresaE}");
        var consultasAntes = Consultas<ObtenerVehiculosQuery>();

        var url = await AplicarAsync(cut, "De la subcontrata");

        url.Should().BeEquivalentTo(new Dictionary<string, string> { ["subcontrata"] = SubcontrataS.ToString() });
        (Consultas<ObtenerVehiculosQuery>() - consultasAntes).Should().Be(1, "una recarga, no dos");
        Ultima<ObtenerVehiculosQuery>().Should().Match<ObtenerVehiculosQuery>(q =>
            q.Busqueda == null && q.EstadoDocumental == null && q.EmpresaId == null && q.SubcontrataId == SubcontrataS);
    }

    [Fact]
    public async Task Vehiculos_con_Empresa_y_subcontrata_guardadas_gana_la_subcontrata()
    {
        ConFiltroGuardado("Los dos", $"{{\"empresa\":\"{EmpresaE}\",\"subcontrata\":\"{SubcontrataS}\"}}");
        var cut = Renderizar<Vehiculos>("vehiculos?q=Transit");

        var url = await AplicarAsync(cut, "Los dos");

        url.Should().BeEquivalentTo(new Dictionary<string, string> { ["subcontrata"] = SubcontrataS.ToString() },
            "los dos empleadores son excluyentes, como en la URL");
        Ultima<ObtenerVehiculosQuery>().Should().Match<ObtenerVehiculosQuery>(q => q.EmpresaId == null && q.SubcontrataId == SubcontrataS);
    }

    [Fact]
    public async Task Vehiculos_un_Id_que_no_es_Guid_y_un_estado_que_no_existe_se_ignoran()
    {
        ConFiltroGuardado("Roto", "{\"q\":\"Transit\",\"empresa\":\"no-es-un-guid\",\"estado\":\"Inventado\"}");
        var cut = Renderizar<Vehiculos>($"vehiculos?subcontrata={SubcontrataS}");

        var url = await AplicarAsync(cut, "Roto");

        url.Should().BeEquivalentTo(new Dictionary<string, string> { ["q"] = "Transit" });
        Ultima<ObtenerVehiculosQuery>().Should().Match<ObtenerVehiculosQuery>(q =>
            q.Busqueda == "Transit" && q.EstadoDocumental == null && q.EmpresaId == null && q.SubcontrataId == null);
    }

    // ------------------------------------------------------------ Gestiones

    [Fact]
    public async Task Gestiones_guardar_envia_los_parametros_de_vista_de_la_url()
    {
        var cut = Renderizar<Gestiones>("gestiones?q=Salas&estado=Pendiente");

        var comando = await GuardarFiltroAsync(cut, "Pendientes de Salas");

        comando.Pantalla.Should().Be(PantallasConFiltrosGuardados.Gestiones);
        comando.ValoresJson.Should().Be("{\"q\":\"Salas\",\"estado\":\"Pendiente\"}");
    }

    /// <summary>Como en Vehículos: <c>OnParametersSet</c> no recarga, así que se mira la consulta además de la URL.</summary>
    [Fact]
    public async Task Gestiones_aplicar_recarga_con_el_filtro_deja_la_url_igual_y_quita_lo_que_no_trae()
    {
        ConFiltroGuardado("Completadas", "{\"estado\":\"Completada\"}");
        var cut = Renderizar<Gestiones>("gestiones?q=Salas&estado=Pendiente");
        var consultasAntes = Consultas<ObtenerGestionesQuery>();

        var url = await AplicarAsync(cut, "Completadas");

        url.Should().BeEquivalentTo(new Dictionary<string, string> { ["estado"] = "Completada" });
        (Consultas<ObtenerGestionesQuery>() - consultasAntes).Should().Be(1, "una recarga, no dos");
        Ultima<ObtenerGestionesQuery>().Should().Match<ObtenerGestionesQuery>(q => q.Busqueda == null && q.Estado == EstadoGestion.Completada);
    }

    [Fact]
    public async Task Gestiones_un_estado_guardado_que_no_existe_se_ignora()
    {
        ConFiltroGuardado("Antiguo", "{\"q\":\"Vega\",\"estado\":\"Archivada\"}");
        var cut = Renderizar<Gestiones>("gestiones?estado=Pendiente");

        var url = await AplicarAsync(cut, "Antiguo");

        url.Should().BeEquivalentTo(new Dictionary<string, string> { ["q"] = "Vega" });
        Ultima<ObtenerGestionesQuery>().Should().Match<ObtenerGestionesQuery>(q => q.Busqueda == "Vega" && q.Estado == null);
    }

    // ------------------------------------------------------------ Proyectos

    [Fact]
    public async Task Proyectos_guardar_envia_el_Cliente_empresarial_la_busqueda_y_el_estado_de_la_url()
    {
        var cut = Renderizar<Proyectos>($"proyectos?cliente={ClienteA}&q=Nave&estado=abiertos");

        var comando = await GuardarFiltroAsync(cut, "Naves abiertas de A");

        comando.Pantalla.Should().Be(PantallasConFiltrosGuardados.Proyectos);
        comando.ValoresJson.Should().Be($"{{\"cliente\":\"{ClienteA}\",\"q\":\"Nave\",\"estado\":\"abiertos\"}}");
    }

    [Fact]
    public async Task Proyectos_aplicar_cambia_de_Cliente_empresarial_deja_la_url_igual_y_quita_lo_que_no_trae()
    {
        ConFiltroGuardado("Cerrados de B", $"{{\"cliente\":\"{ClienteB}\",\"estado\":\"cerrados\"}}");
        var cut = Renderizar<Proyectos>($"proyectos?cliente={ClienteA}&q=Nave&estado=abiertos");
        Ultima<ObtenerProyectosQuery>().ClienteId.Should().Be(ClienteA, "el enlace abre la lista de ese Cliente empresarial");

        var url = await AplicarAsync(cut, "Cerrados de B");

        url.Should().BeEquivalentTo(new Dictionary<string, string> { ["cliente"] = ClienteB.ToString(), ["estado"] = "cerrados" });
        Ultima<ObtenerProyectosQuery>().ClienteId.Should().Be(ClienteB, "la lista que se pide es la del Cliente empresarial del filtro guardado");
        cut.FindAll(".chip-filtro").Should().BeEmpty("la búsqueda no está en el filtro guardado: se quita");
    }

    /// <summary>
    /// La URL se escribe ANTES de cargar los datos del Cliente empresarial nuevo. Si se escribiera después,
    /// durante la carga seguiría diciendo el anterior: teclear en el buscador en esa ventana navegaba
    /// conservándolo, la página volvía a él y al terminar la carga se escribía el nuevo (dos cargas y parpadeo).
    /// Y con los campos ya puestos, la navegación no dispara una segunda carga.
    /// </summary>
    [Fact]
    public async Task Proyectos_al_aplicar_la_url_ya_dice_el_Cliente_empresarial_nuevo_cuando_empieza_su_carga_y_carga_una_vez()
    {
        ConFiltroGuardado("Cerrados de B", $"{{\"cliente\":\"{ClienteB}\",\"estado\":\"cerrados\"}}");
        var cut = Renderizar<Proyectos>($"proyectos?cliente={ClienteA}&q=Nave&estado=abiertos");
        var urlsAlCargarB = new List<Dictionary<string, string>>();
        _mediador.AlEnviar = peticion =>
        {
            if (peticion is ObtenerProyectosQuery q && q.ClienteId == ClienteB)
                urlsAlCargarB.Add(ParametrosDeLaUrl());
        };

        await AplicarAsync(cut, "Cerrados de B");

        urlsAlCargarB.Should().ContainSingle("una carga del Cliente empresarial nuevo, no dos")
            .Which.Should().BeEquivalentTo(new Dictionary<string, string> { ["cliente"] = ClienteB.ToString(), ["estado"] = "cerrados" },
                "cuando empieza la carga la URL ya es la de la vista guardada");
        _mediador.Enviadas.OfType<ObtenerProyectosQuery>().Count(q => q.ClienteId == ClienteA)
            .Should().Be(1, "la del arranque: el Cliente empresarial anterior no vuelve a cargarse");
    }

    /// <summary>
    /// El mismo Cliente empresarial: cambian búsqueda y estado, que aplica la consulta. Se vuelve a pedir la
    /// lista una vez, con los del filtro guardado.
    /// </summary>
    [Fact]
    public async Task Proyectos_aplicar_con_el_mismo_Cliente_empresarial_vuelve_a_pedir_la_lista_una_vez_con_la_busqueda_y_el_estado_del_filtro()
    {
        ConFiltroGuardado("Cerrados de A", $"{{\"cliente\":\"{ClienteA}\",\"estado\":\"cerrados\"}}");
        var cut = Renderizar<Proyectos>($"proyectos?cliente={ClienteA}&q=Nave&estado=abiertos");
        var cargasAntes = Consultas<ObtenerProyectosQuery>();

        var url = await AplicarAsync(cut, "Cerrados de A");

        url.Should().BeEquivalentTo(new Dictionary<string, string> { ["cliente"] = ClienteA.ToString(), ["estado"] = "cerrados" });
        Consultas<ObtenerProyectosQuery>().Should().Be(cargasAntes + 1, "una carga con los filtros nuevos, no dos");
        Ultima<ObtenerProyectosQuery>().Should().Match<ObtenerProyectosQuery>(
            q => q.ClienteId == ClienteA && q.Busqueda == null && q.SoloAbiertos == false,
            "la búsqueda que el filtro guardado no trae se quita y el estado es el suyo");
        cut.FindAll(".chip-filtro").Should().BeEmpty("la búsqueda no está en el filtro guardado: se quita también de la barra");
        cut.FindComponent<BarraFiltros>().Instance.Busqueda.Should().BeEmpty();
    }

    /// <summary>
    /// Un Id guardado no es autoridad: Proyectos solo elige un Cliente empresarial que el selector ofrece. Uno que
    /// ya no se ofrece cuenta como ausente, y lo ausente se quita: la lista vuelve a la de todos los que alcanza.
    /// </summary>
    [Fact]
    public async Task Proyectos_un_Cliente_empresarial_guardado_que_el_selector_no_ofrece_no_se_elige()
    {
        var ajeno = Guid.NewGuid();
        ConFiltroGuardado("De otro", $"{{\"cliente\":\"{ajeno}\",\"q\":\"Nave\",\"estado\":\"archivados\"}}");
        var cut = Renderizar<Proyectos>($"proyectos?cliente={ClienteA}&estado=abiertos");
        // Control positivo de la aserción de ausencia del final: antes de aplicar, la pastilla sí nombra al elegido.
        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla")
            .Should().Contain(b => (b.GetAttribute("aria-label") ?? string.Empty).Contains("Refrielectric"));

        var url = await AplicarAsync(cut, "De otro");

        url.Should().BeEquivalentTo(new Dictionary<string, string> { ["q"] = "Nave" },
            "ni el Cliente empresarial ajeno ni el estado desconocido llegan a la URL");
        _mediador.Enviadas.OfType<ObtenerProyectosQuery>().Should().NotContain(q => q.ClienteId == ajeno);
        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla").Should().NotBeEmpty("la barra sigue ahí")
            .And.NotContain(b => (b.GetAttribute("aria-label") ?? string.Empty).Contains("Refrielectric"), "ya no hay Cliente empresarial elegido");
    }

    // ------------------------------------- Lo ausente se quita, parámetro a parámetro
    //
    // Los «aplicar» de arriba quitan los parámetros que su filtro no trae, pero cada uno deja alguno
    // puesto. Un filtro guardado sin ningún parámetro parte de la URL con TODA la lista blanca y exige
    // que no quede ninguno: así un parámetro que conservara su valor anterior no pasa inadvertido.

    [Fact]
    public async Task Empresas_un_filtro_guardado_sin_parametros_quita_todos_los_de_la_vista()
    {
        ConFiltroGuardado("Todo", "{}");
        var cut = Renderizar<Empresas>("empresas?q=Ebro&estado=Vencido");

        var url = await AplicarAsync(cut, "Todo");

        url.Should().BeEmpty();
        Ultima<ObtenerEmpresasQuery>().Should().Match<ObtenerEmpresasQuery>(q => q.Busqueda == null && q.EstadoDocumental == null);
    }

    [Fact]
    public async Task Centros_un_filtro_guardado_sin_parametros_quita_todos_los_de_la_vista()
    {
        ConFiltroGuardado("Todo", "{}");
        var cut = Renderizar<Centros>($"centros?q=Nave&estado=Vencido&cliente={ClienteA}&empresa={EmpresaE}&agrupar=no&orden=cumplimiento-desc");
        // Control positivo: antes de aplicar, la consulta sí lleva el Cliente empresarial y el orden de la URL.
        Ultima<ObtenerCentrosQuery>().Should().Match<ObtenerCentrosQuery>(q => q.ClienteId == ClienteA && q.Descendente);

        var url = await AplicarAsync(cut, "Todo");

        url.Should().BeEmpty();
        Ultima<ObtenerCentrosQuery>().Should().Match<ObtenerCentrosQuery>(q =>
            q.Busqueda == null && q.ClienteId == null && q.EmpresaId == null && q.Estados == null && !q.Descendente);
    }

    [Fact]
    public async Task Subcontratas_un_filtro_guardado_sin_parametros_quita_todos_los_de_la_vista()
    {
        ConFiltroGuardado("Todo", "{}");
        var cut = Renderizar<Subcontratas>("subcontratas?q=Aislamientos&nivel=Supervisada");

        var url = await AplicarAsync(cut, "Todo");

        url.Should().BeEmpty();
        Ultima<ObtenerSubcontratasQuery>().Should().Match<ObtenerSubcontratasQuery>(q => q.Busqueda == null && q.NivelServicio == null);
    }

    [Fact]
    public async Task Vehiculos_un_filtro_guardado_sin_parametros_quita_todos_los_de_la_vista()
    {
        ConFiltroGuardado("Todo", "{}");
        var cut = Renderizar<Vehiculos>($"vehiculos?q=Transit&estado=Vencido&subcontrata={SubcontrataS}");
        Ultima<ObtenerVehiculosQuery>().SubcontrataId.Should().Be(SubcontrataS, "control positivo: la URL sí filtra por la subcontrata");

        var url = await AplicarAsync(cut, "Todo");

        url.Should().BeEmpty();
        Ultima<ObtenerVehiculosQuery>().Should().Match<ObtenerVehiculosQuery>(q =>
            q.Busqueda == null && q.EstadoDocumental == null && q.EmpresaId == null && q.SubcontrataId == null);
    }

    [Fact]
    public async Task Gestiones_un_filtro_guardado_sin_parametros_quita_todos_los_de_la_vista()
    {
        ConFiltroGuardado("Todo", "{}");
        var cut = Renderizar<Gestiones>("gestiones?q=Salas&estado=Pendiente");

        var url = await AplicarAsync(cut, "Todo");

        url.Should().BeEmpty();
        Ultima<ObtenerGestionesQuery>().Should().Match<ObtenerGestionesQuery>(q => q.Busqueda == null && q.Estado == null);
    }

    [Fact]
    public async Task Proyectos_un_filtro_guardado_sin_parametros_quita_todos_los_de_la_vista()
    {
        ConFiltroGuardado("Todo", "{}");
        var cut = Renderizar<Proyectos>($"proyectos?cliente={ClienteA}&q=Nave&estado=cerrados");

        var url = await AplicarAsync(cut, "Todo");

        url.Should().BeEmpty("sin Cliente empresarial en el filtro, la lista vuelve a «elige un Cliente»");
        cut.FindAll(".chip-filtro").Should().BeEmpty();
    }
}
