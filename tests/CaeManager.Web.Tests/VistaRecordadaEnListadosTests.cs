using System.Text.Json;
using System.Web;
using Bunit;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Configuracion.Commands.GuardarVistaRecordada;
using CaeManager.Application.Configuracion.Commands.OlvidarVistaRecordada;
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
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
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
using CaeManager.Web.Features.Trabajadores.Pages;
using CaeManager.Web.Features.Vehiculos.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La vista recordada (<see cref="VistaRecordadaDeListado"/>) conectada a cada página. La pieza sola
/// —cuándo restaura, cuándo escribe, el rebote— está en <see cref="VistaRecordadaDeListadoTests"/>; aquí se
/// mira que cada página le pasa su lista blanca completa y aplica lo que recibe:
/// <list type="bullet">
/// <item><description>al llegar <b>sin parámetros</b> y con una vista recordada, la URL y la consulta quedan con
/// todos los valores de su lista blanca;</description></item>
/// <item><description><b>«Restablecer vista»</b> deja la URL sin parámetros de vista y olvida lo recordado;</description></item>
/// <item><description>donde el orden de columna viaja en la URL, <c>?orden=</c> ordena la consulta y un valor que
/// no existe se ignora.</description></item>
/// </list>
/// Siete de las nueve páginas van aquí. Clientes y Documentos necesitan un arnés más caro y llevan los
/// mismos casos en <c>ClientesListaGen2Tests.VistaRecordada.cs</c> y <c>DocumentosGen2Tests.VistaRecordada.cs</c>.
/// </summary>
public class VistaRecordadaEnListadosTests : BunitContext
{
    private static readonly Guid ClienteA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClienteB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid EmpresaE = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SubcontrataS = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid CentroC = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly MediatorDeListado _mediador = new();

    public VistaRecordadaEnListadosTests()
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
        Services.AddScoped<IValidator<CrearTrabajadorCommand>>(_ => new InlineValidator<CrearTrabajadorCommand>());
    }

    /// <summary>
    /// Responde a las consultas de los siete listados con listas vacías, guarda una vista recordada por
    /// pantalla y apunta todo lo que se le envía.
    /// </summary>
    private sealed class MediatorDeListado : IMediator
    {
        public List<object> Enviadas { get; } = [];

        /// <summary>Lo que devuelve <see cref="ObtenerVistaRecordadaQuery"/> por pantalla. Sin entrada: nada recordado.</summary>
        public Dictionary<string, string> VistasRecordadas { get; } = [];

        /// <summary>
        /// Con ella, la lectura de la vista recordada no responde hasta que se complete: en el producto es una
        /// consulta a la base y no vuelve en el acto, que es justo cuando la página puede adelantarse a cargar.
        /// </summary>
        public TaskCompletionSource? LecturaRetenida { get; set; }

        /// <summary>Con ella, la lectura de la vista recordada falla.</summary>
        public Exception? FalloDeLectura { get; set; }

        /// <summary>Con ella, la lista de Clientes empresariales del selector no responde hasta que se complete.</summary>
        public TaskCompletionSource? ClientesRetenidos { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (request is ObtenerVistaRecordadaQuery)
            {
                if (LecturaRetenida is { } retenida)
                    return ResponderTrasAsync<TResponse>(retenida.Task, request, cancellationToken);
                if (FalloDeLectura is { } fallo)
                    return Task.FromException<TResponse>(fallo);
            }

            if (request is ObtenerClientesParaSelectorQuery && ClientesRetenidos is { } clientes)
                return ResponderTrasAsync<TResponse>(clientes.Task, request, cancellationToken);

            return Task.FromResult((TResponse)Responder(request)!);
        }

        private async Task<TResponse> ResponderTrasAsync<TResponse>(Task espera, object request, CancellationToken cancellationToken)
        {
            await espera.WaitAsync(cancellationToken);
            return (TResponse)Responder(request)!;
        }

        private object? Responder(object request)
        {
            object? respuesta = request switch
            {
                ObtenerVistaRecordadaQuery v => VistasRecordadas.GetValueOrDefault(v.Pantalla),
                GuardarVistaRecordadaCommand => Result.Exito(),
                OlvidarVistaRecordadaCommand => Result.Exito(),
                ObtenerFiltrosGuardadosQuery => (IReadOnlyList<FiltroGuardadoDto>)[],

                ObtenerEmpresasQuery q => new ResultadoPaginado<EmpresaListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerCentrosQuery q => new ResultadoPaginado<CentroListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerSubcontratasQuery q => new ResultadoPaginado<SubcontrataListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerVehiculosQuery q => new ResultadoPaginado<VehiculoListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerGestionesQuery q => new ResultadoPaginado<GestionListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerTrabajadoresQuery q => new ResultadoPaginado<TrabajadorListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerProyectosQuery => (IReadOnlyList<ProyectoListaDto>)[],

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
            return respuesta;
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

    /// <summary>Los parámetros de vista son [SupplyParameterFromQuery]: se llega a ellos navegando a la URI, como en el producto.</summary>
    private IRenderedComponent<TPagina> Renderizar<TPagina>(string ruta) where TPagina : IComponent
    {
        Navegacion.NavigateTo(ruta);
        return Render<TPagina>();
    }

    private static string Json(params (string Parametro, string Valor)[] vista) =>
        JsonSerializer.Serialize(vista.ToDictionary(p => p.Parametro, p => p.Valor));

    /// <summary>Lo que el Usuario dejó recordado en esa pantalla, y lo mismo como diccionario para comparar con la URL.</summary>
    private Dictionary<string, string> ConVistaRecordada(string pantalla, params (string Parametro, string Valor)[] vista)
    {
        _mediador.VistasRecordadas[pantalla] = Json(vista);
        return vista.ToDictionary(p => p.Parametro, p => p.Valor);
    }

    private Dictionary<string, string> ParametrosDeLaUrl()
    {
        var consulta = HttpUtility.ParseQueryString(new Uri(Navegacion.Uri).Query);
        return consulta.AllKeys.ToDictionary(k => k!, k => consulta[k]!);
    }

    private TConsulta Ultima<TConsulta>() => _mediador.Enviadas.OfType<TConsulta>().Last();

    /// <summary>
    /// La pieza leyó lo recordado de ESA pantalla (una vez) y la página dejó la URL con la vista recordada,
    /// menos la búsqueda libre: las vistas de estos tests la traen, como una fila escrita antes de que
    /// dejara de recordarse, y ninguna página la restaura.
    /// </summary>
    private void LaUrlQuedaCon<TPagina>(IRenderedComponent<TPagina> cut, string pantalla, Dictionary<string, string> recordada)
        where TPagina : IComponent
    {
        recordada.Should().ContainKey("q", "control: la vista recordada del test trae una búsqueda que no debe volver");
        var esperada = recordada.Where(p => p.Key != "q").ToDictionary(p => p.Key, p => p.Value);
        cut.WaitForAssertion(() => ParametrosDeLaUrl().Should().BeEquivalentTo(esperada));
        _mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().Equal([new ObtenerVistaRecordadaQuery(pantalla)]);
    }

    /// <summary>
    /// Pulsa «Restablecer vista» y afirma lo común: la URL sin parámetros de vista y lo recordado olvidado,
    /// no guardado como una vista vacía.
    /// </summary>
    private async Task RestablecerYOlvidarAsync<TPagina>(IRenderedComponent<TPagina> cut, string pantalla) where TPagina : IComponent
    {
        ParametrosDeLaUrl().Should().NotBeEmpty("control: la página se abre con una vista propia");
        _mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().BeEmpty("con parámetros manda la URL: no se lee lo recordado");

        await cut.Find("button.restablecer-vista-barra").ClickAsync(new MouseEventArgs());

        ParametrosDeLaUrl().Should().BeEmpty("«Restablecer vista» quita todos los parámetros de vista, el orden incluido");
        _mediador.Enviadas.OfType<OlvidarVistaRecordadaCommand>().Should().Equal([new OlvidarVistaRecordadaCommand(pantalla)]);
        _mediador.Enviadas.OfType<GuardarVistaRecordadaCommand>().Should().BeEmpty("la vista de inicio se olvida, no se guarda vacía");
        cut.FindAll("button.restablecer-vista-barra").Should().BeEmpty("ya en la vista de inicio no hay nada que restablecer");
    }

    // ------------------------------------------------------------- Empresas

    [Fact]
    public void Empresas_sin_parametros_restaura_la_vista_recordada()
    {
        var recordada = ConVistaRecordada(PantallasConVistaRecordada.Empresas, ("q", "Ebro"), ("estado", "Vencido"));

        var cut = Renderizar<Empresas>("empresas");

        LaUrlQuedaCon(cut, PantallasConVistaRecordada.Empresas, recordada);
        Ultima<ObtenerEmpresasQuery>().Should().Match<ObtenerEmpresasQuery>(q => q.Busqueda == null && q.EstadoDocumental == "Vencido" && q.Pagina == 1);
    }

    [Fact]
    public async Task Empresas_restablecer_vista_limpia_la_url_y_olvida()
    {
        var cut = Renderizar<Empresas>("empresas?q=Ebro&estado=Vencido");

        await RestablecerYOlvidarAsync(cut, PantallasConVistaRecordada.Empresas);

        Ultima<ObtenerEmpresasQuery>().Should().Match<ObtenerEmpresasQuery>(q => q.Busqueda == null && q.EstadoDocumental == null);
    }

    // -------------------------------------------------------------- Centros

    [Fact]
    public void Centros_sin_parametros_restaura_la_vista_recordada()
    {
        var recordada = ConVistaRecordada(PantallasConVistaRecordada.Centros,
            ("q", "Nave"), ("estado", "Vencido"), ("cliente", ClienteA.ToString()), ("empresa", EmpresaE.ToString()),
            ("agrupar", "no"), ("orden", "cumplimiento-desc"));

        var cut = Renderizar<Centros>("centros");

        LaUrlQuedaCon(cut, PantallasConVistaRecordada.Centros, recordada);
        Ultima<ObtenerCentrosQuery>().Should().Match<ObtenerCentrosQuery>(q =>
            q.Busqueda == null && q.ClienteId == ClienteA && q.EmpresaId == EmpresaE
            && q.OrdenarPor == nameof(CentroListaDto.CumplimientoPorcentaje) && q.Descendente && q.Pagina == 1);
        Ultima<ObtenerCentrosQuery>().Estados.Should().Equal(EstadoCentro.Vencido);
    }

    [Fact]
    public async Task Centros_restablecer_vista_limpia_la_url_y_olvida()
    {
        var cut = Renderizar<Centros>($"centros?q=Nave&estado=Vencido&cliente={ClienteA}&agrupar=no&orden=cumplimiento-desc");

        await RestablecerYOlvidarAsync(cut, PantallasConVistaRecordada.Centros);

        Ultima<ObtenerCentrosQuery>().Should().Match<ObtenerCentrosQuery>(q =>
            q.Busqueda == null && q.ClienteId == null && q.Estados == null && q.OrdenarPor == null);
    }

    // --------------------------------------------------------- Subcontratas

    [Fact]
    public void Subcontratas_sin_parametros_restaura_la_vista_recordada()
    {
        var recordada = ConVistaRecordada(PantallasConVistaRecordada.Subcontratas, ("q", "Nervión"), ("nivel", "Gestionada"));

        var cut = Renderizar<Subcontratas>("subcontratas");

        LaUrlQuedaCon(cut, PantallasConVistaRecordada.Subcontratas, recordada);
        Ultima<ObtenerSubcontratasQuery>().Should().Match<ObtenerSubcontratasQuery>(q =>
            q.Busqueda == null && q.NivelServicio == NivelServicioSubcontrata.Gestionada && q.Pagina == 1);
    }

    [Fact]
    public async Task Subcontratas_restablecer_vista_limpia_la_url_y_olvida()
    {
        var cut = Renderizar<Subcontratas>("subcontratas?q=Nervión&nivel=Gestionada");

        await RestablecerYOlvidarAsync(cut, PantallasConVistaRecordada.Subcontratas);

        Ultima<ObtenerSubcontratasQuery>().Should().Match<ObtenerSubcontratasQuery>(q => q.Busqueda == null && q.NivelServicio == null);
    }

    // ------------------------------------------------------------ Vehículos

    /// <summary>Empresa y subcontrata son excluyentes, así que la lista blanca completa se restaura en dos casos.</summary>
    [Theory]
    [InlineData("empresa")]
    [InlineData("subcontrata")]
    public void Vehiculos_sin_parametros_restaura_la_vista_recordada(string empleador)
    {
        Guid? empresa = empleador == "empresa" ? EmpresaE : null;
        Guid? subcontrata = empleador == "subcontrata" ? SubcontrataS : null;
        var recordada = ConVistaRecordada(PantallasConVistaRecordada.Vehiculos,
            ("q", "Transit"), ("estado", "Vencido"), (empleador, (empresa ?? subcontrata).ToString()!), ("orden", "matricula-desc"));

        var cut = Renderizar<Vehiculos>("vehiculos");

        LaUrlQuedaCon(cut, PantallasConVistaRecordada.Vehiculos, recordada);
        cut.WaitForAssertion(() => Ultima<ObtenerVehiculosQuery>().Should().Match<ObtenerVehiculosQuery>(q =>
            q.Busqueda == null && q.EstadoDocumental == "Vencido" && q.EmpresaId == empresa && q.SubcontrataId == subcontrata
            && q.OrdenarPor == nameof(VehiculoListaDto.NumeroPlaca) && q.Descendente));
    }

    [Fact]
    public async Task Vehiculos_restablecer_vista_limpia_la_url_y_olvida()
    {
        var cut = Renderizar<Vehiculos>($"vehiculos?q=Transit&estado=Vencido&empresa={EmpresaE}&orden=matricula-desc");

        await RestablecerYOlvidarAsync(cut, PantallasConVistaRecordada.Vehiculos);

        cut.WaitForAssertion(() => Ultima<ObtenerVehiculosQuery>().Should().Match<ObtenerVehiculosQuery>(q =>
            q.Busqueda == null && q.EstadoDocumental == null && q.EmpresaId == null && q.OrdenarPor == null));
    }

    [Fact]
    public void Vehiculos_el_orden_de_la_url_ordena_la_consulta()
    {
        var cut = Renderizar<Vehiculos>("vehiculos?orden=matricula-desc");

        cut.WaitForAssertion(() => Ultima<ObtenerVehiculosQuery>().Should().Match<ObtenerVehiculosQuery>(q =>
            q.OrdenarPor == nameof(VehiculoListaDto.NumeroPlaca) && q.Descendente));
    }

    [Fact]
    public void Vehiculos_un_orden_que_no_existe_se_ignora()
    {
        var cut = Renderizar<Vehiculos>("vehiculos?orden=inventada-desc");

        cut.WaitForAssertion(() => Ultima<ObtenerVehiculosQuery>().Should().Match<ObtenerVehiculosQuery>(q =>
            q.OrdenarPor == null && !q.Descendente, "sin un orden válido la rejilla nace con el de fábrica: sin ordenar"));
    }

    // ------------------------------------------------------------ Gestiones

    [Fact]
    public void Gestiones_sin_parametros_restaura_la_vista_recordada()
    {
        var recordada = ConVistaRecordada(PantallasConVistaRecordada.Gestiones, ("q", "Salas"), ("estado", "Pendiente"), ("orden", "creada-desc"));

        var cut = Renderizar<Gestiones>("gestiones");

        LaUrlQuedaCon(cut, PantallasConVistaRecordada.Gestiones, recordada);
        cut.WaitForAssertion(() => Ultima<ObtenerGestionesQuery>().Should().Match<ObtenerGestionesQuery>(q =>
            q.Busqueda == null && q.Estado == EstadoGestion.Pendiente
            && q.OrdenarPor == nameof(GestionListaDto.CreadoEnUtc) && q.Descendente));
    }

    [Fact]
    public async Task Gestiones_restablecer_vista_limpia_la_url_y_olvida()
    {
        var cut = Renderizar<Gestiones>("gestiones?q=Salas&estado=Pendiente&orden=creada-desc");

        await RestablecerYOlvidarAsync(cut, PantallasConVistaRecordada.Gestiones);

        cut.WaitForAssertion(() => Ultima<ObtenerGestionesQuery>().Should().Match<ObtenerGestionesQuery>(q =>
            q.Busqueda == null && q.Estado == null && q.OrdenarPor == nameof(GestionListaDto.Estado) && !q.Descendente,
            "vuelve el orden de fábrica: por estado"));
    }

    [Fact]
    public void Gestiones_el_orden_de_la_url_ordena_la_consulta()
    {
        var cut = Renderizar<Gestiones>("gestiones?orden=creada-desc");

        cut.WaitForAssertion(() => Ultima<ObtenerGestionesQuery>().Should().Match<ObtenerGestionesQuery>(q =>
            q.OrdenarPor == nameof(GestionListaDto.CreadoEnUtc) && q.Descendente));
    }

    [Fact]
    public void Gestiones_un_orden_que_no_existe_se_ignora()
    {
        var cut = Renderizar<Gestiones>("gestiones?orden=inventada-desc");

        cut.WaitForAssertion(() => Ultima<ObtenerGestionesQuery>().Should().Match<ObtenerGestionesQuery>(q =>
            q.OrdenarPor == nameof(GestionListaDto.Estado) && !q.Descendente, "sin un orden válido la rejilla nace con el de fábrica: por estado"));
    }

    // ------------------------------------------------------------ Proyectos

    [Fact]
    public void Proyectos_sin_parametros_restaura_el_Cliente_empresarial_y_el_estado_sin_la_busqueda()
    {
        var recordada = ConVistaRecordada(PantallasConVistaRecordada.Proyectos,
            ("cliente", ClienteA.ToString()), ("q", "Nave"), ("estado", "abiertos"));

        var cut = Renderizar<Proyectos>("proyectos");

        LaUrlQuedaCon(cut, PantallasConVistaRecordada.Proyectos, recordada);
        Ultima<ObtenerProyectosQuery>().ClienteId.Should().Be(ClienteA, "la lista que se pide es la del Cliente empresarial recordado");
        cut.FindAll(".chip-filtro").Should().BeEmpty("la búsqueda no se recuerda: no hay chip de búsqueda que enseñar");
    }

    /// <summary>
    /// Guarda: Proyectos valida el Cliente empresarial recordado contra la lista de su selector, y si la pieza
    /// restaurase antes de que llegara lo descartaría. Hoy no puede: la pieza vive bajo la rama que solo se
    /// pinta con la carga terminada (<c>_cargando</c> nace en <c>true</c>), así que no se monta —ni lee lo
    /// recordado— hasta entonces. Si alguien la saca de esa rama, esto se pone en rojo.
    /// </summary>
    [Fact]
    public async Task Proyectos_con_los_Clientes_empresariales_aun_sin_cargar_no_restaura_y_al_cargar_restaura_el_suyo()
    {
        _mediador.VistasRecordadas[PantallasConVistaRecordada.Proyectos] = Json(("cliente", ClienteA.ToString()), ("estado", "abiertos"));
        _mediador.ClientesRetenidos = new TaskCompletionSource();

        var cut = Renderizar<Proyectos>("proyectos");
        cut.Render();

        _mediador.Enviadas.OfType<ObtenerClientesParaSelectorQuery>().Should().ContainSingle("control: la carga está en vuelo");
        ParametrosDeLaUrl().Should().BeEmpty("sin la lista de Clientes empresariales no hay con qué validar el recordado");
        _mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().BeEmpty("la pieza no se monta hasta que la carga termina");

        await cut.InvokeAsync(() => _mediador.ClientesRetenidos.SetResult());

        cut.WaitForAssertion(() => ParametrosDeLaUrl().Should().BeEquivalentTo(
            new Dictionary<string, string> { ["cliente"] = ClienteA.ToString(), ["estado"] = "abiertos" }));
        _mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().ContainSingle();
        // La lista ya no espera a un Cliente empresarial: al terminar el selector se pide la de todos, y la
        // restauración llega con esa petición en vuelo. La página descarta la respuesta que no es de su
        // última carga; aquí se fija que la del Cliente empresarial recordado se pide.
        _mediador.Enviadas.OfType<ObtenerProyectosQuery>().Should().Contain(q => q.ClienteId == ClienteA);
    }

    /// <summary>
    /// El Cliente empresarial es un filtro más (la lista existe sin él): «Restablecer vista» lo quita con
    /// el resto, no queda nada que recordar y la lista vuelve a ser la de todos.
    /// </summary>
    [Fact]
    public async Task Proyectos_restablecer_vista_quita_tambien_el_Cliente_empresarial_y_olvida_lo_recordado()
    {
        var cut = Renderizar<Proyectos>($"proyectos?cliente={ClienteA}&q=Nave&estado=abiertos");

        await cut.Find("button.restablecer-vista-barra").ClickAsync(new MouseEventArgs());

        ParametrosDeLaUrl().Should().BeEmpty();
        _mediador.Enviadas.OfType<OlvidarVistaRecordadaCommand>().Should().Equal(
            [new OlvidarVistaRecordadaCommand(PantallasConVistaRecordada.Proyectos)]);
        _mediador.Enviadas.OfType<GuardarVistaRecordadaCommand>().Should().BeEmpty("no queda nada por recordar");
        Ultima<ObtenerProyectosQuery>().ClienteId.Should().BeNull("sin Cliente empresarial la lista es la de todos");
        cut.FindAll(".chip-filtro").Should().BeEmpty("la búsqueda se quitó con el resto de la vista");
    }

    // --------------------------------------------------------- Trabajadores

    /// <summary>
    /// Empresa y subcontrata son excluyentes, así que la lista blanca completa se restaura en dos casos. El
    /// Centro se compone con cualquiera de los dos.
    /// </summary>
    [Theory]
    [InlineData("empresa")]
    [InlineData("subcontrata")]
    public void Trabajadores_sin_parametros_restaura_la_vista_recordada(string empleador)
    {
        Guid? empresa = empleador == "empresa" ? EmpresaE : null;
        Guid? subcontrata = empleador == "subcontrata" ? SubcontrataS : null;
        var recordada = ConVistaRecordada(PantallasConVistaRecordada.Trabajadores,
            ("q", "Vega"), ("estado", "Vencido"), (empleador, (empresa ?? subcontrata).ToString()!), ("centro", CentroC.ToString()),
            ("orden", "trabajador-desc"));

        var cut = Renderizar<Trabajadores>("trabajadores");

        LaUrlQuedaCon(cut, PantallasConVistaRecordada.Trabajadores, recordada);
        cut.WaitForAssertion(() => Ultima<ObtenerTrabajadoresQuery>().Should().Match<ObtenerTrabajadoresQuery>(q =>
            q.Busqueda == null && q.EstadoDocumental == "Vencido" && q.EmpresaId == empresa && q.SubcontrataId == subcontrata
            && q.CentroId == CentroC && q.OrdenarPor == nameof(TrabajadorListaDto.Apellidos) && q.Descendente));
    }

    [Fact]
    public async Task Trabajadores_restablecer_vista_limpia_la_url_y_olvida()
    {
        var cut = Renderizar<Trabajadores>($"trabajadores?q=Vega&estado=Vencido&empresa={EmpresaE}&centro={CentroC}&orden=trabajador-desc");

        await RestablecerYOlvidarAsync(cut, PantallasConVistaRecordada.Trabajadores);

        cut.WaitForAssertion(() => Ultima<ObtenerTrabajadoresQuery>().Should().Match<ObtenerTrabajadoresQuery>(q =>
            q.Busqueda == null && q.EstadoDocumental == null && q.EmpresaId == null && q.CentroId == null
            && q.OrdenarPor == nameof(TrabajadorListaDto.EstadoDocumental) && !q.Descendente,
            "vuelve el orden de fábrica: por documentación"));
    }

    [Fact]
    public async Task Trabajadores_el_orden_de_la_url_ordena_la_consulta_y_la_exportacion()
    {
        var cut = Renderizar<Trabajadores>("trabajadores?orden=trabajador-desc");

        cut.WaitForAssertion(() => Ultima<ObtenerTrabajadoresQuery>().Should().Match<ObtenerTrabajadoresQuery>(q =>
            q.OrdenarPor == nameof(TrabajadorListaDto.Apellidos) && q.Descendente));

        // «Exportar esta vista» sale en el mismo orden que se ve, venga de un clic o de la URL.
        await cut.Find("header.cabecera-pagina .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        cut.FindAll("header.cabecera-pagina a.menu-acciones-item").First().GetAttribute("href")
            .Should().StartWith("/trabajadores/exportar.xlsx?")
            .And.Contain($"orden={nameof(TrabajadorListaDto.Apellidos)}").And.Contain("desc=true");
    }

    [Fact]
    public void Trabajadores_un_orden_que_no_existe_se_ignora()
    {
        var cut = Renderizar<Trabajadores>("trabajadores?orden=inventada-desc");

        cut.WaitForAssertion(() => Ultima<ObtenerTrabajadoresQuery>().Should().Match<ObtenerTrabajadoresQuery>(q =>
            q.OrdenarPor == nameof(TrabajadorListaDto.EstadoDocumental) && !q.Descendente,
            "sin un orden válido la rejilla nace con el de fábrica: por documentación"));
    }

    /// <summary>La otra dirección: sin el orden en la URL la vista recordada no tendría qué recordar.</summary>
    [Fact]
    public async Task Trabajadores_ordenar_por_una_columna_lo_escribe_en_la_url()
    {
        var cut = Renderizar<Trabajadores>("trabajadores");
        ParametrosDeLaUrl().Should().NotContainKey("orden", "control: el orden de fábrica no viaja");

        await cut.FindAll("thead th button.col-title").First().ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => ParametrosDeLaUrl().Should().Contain("orden", "trabajador"));
        Ultima<ObtenerTrabajadoresQuery>().Should().Match<ObtenerTrabajadoresQuery>(q =>
            q.OrdenarPor == nameof(TrabajadorListaDto.Apellidos) && !q.Descendente);
    }

    // ------------------------------------------- cargas de datos al restaurar
    //
    // Medido el 2026-10-10, con la lectura de lo recordado respondiendo más tarde, como en el producto (es una
    // consulta a la base): al entrar sin parámetros con una vista recordada, la pasada con circuito pide la
    // lista DOS veces —la de fábrica y, cuando vuelve la lectura, la recordada—. No hay compuerta que retenga
    // la primera: la página carga sin esperar a la pieza, así que una lectura lenta o fallida no deja la lista
    // sin cargar. Estos tests fijan ese número en un listado de rejilla (Vehículos) y en uno de acordeón
    // (Centros); si alguien añade la compuerta, bajan a una y hay que cambiarlos a la vez.

    private int Cargas<TConsulta>() => _mediador.Enviadas.OfType<TConsulta>().Count();

    private void LaLecturaDeLoRecordadoEstaEnVuelo<TPagina>(IRenderedComponent<TPagina> cut) where TPagina : IComponent =>
        cut.WaitForAssertion(() => _mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().HaveCount(1));

    /// <summary>
    /// Con otro orden la rejilla se remonta y pide ella los datos; sin él, la página la refresca. Son dos
    /// caminos de <c>AplicarVistaGuardadaAsync</c> y los dos piden la lista una vez más, no dos.
    /// </summary>
    [Theory]
    [InlineData("matricula-desc")]
    [InlineData(null)]
    public void Vehiculos_con_vista_recordada_pide_la_lista_de_fabrica_y_despues_la_recordada(string? orden)
    {
        if (orden is null)
            ConVistaRecordada(PantallasConVistaRecordada.Vehiculos, ("estado", "Vencido"));
        else
            ConVistaRecordada(PantallasConVistaRecordada.Vehiculos, ("estado", "Vencido"), ("orden", orden));
        _mediador.LecturaRetenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var cut = Renderizar<Vehiculos>("vehiculos");

        LaLecturaDeLoRecordadoEstaEnVuelo(cut);
        Cargas<ObtenerVehiculosQuery>().Should().Be(1, "la lista de fábrica no espera a la lectura de lo recordado");
        Ultima<ObtenerVehiculosQuery>().Should().Match<ObtenerVehiculosQuery>(q => q.EstadoDocumental == null && q.OrdenarPor == null);

        _mediador.LecturaRetenida.SetResult();

        cut.WaitForAssertion(() => ParametrosDeLaUrl().Should().ContainKey("estado"));
        var conOrden = orden is not null;
        cut.WaitForAssertion(() => Ultima<ObtenerVehiculosQuery>().Should().Match<ObtenerVehiculosQuery>(q =>
            q.EstadoDocumental == "Vencido" && q.Descendente == conOrden));
        Cargas<ObtenerVehiculosQuery>().Should().Be(2, "restaurar pide la lista una vez más, no dos");
    }

    [Fact]
    public void Centros_con_vista_recordada_pide_la_lista_de_fabrica_y_despues_la_recordada()
    {
        ConVistaRecordada(PantallasConVistaRecordada.Centros, ("estado", "Vencido"), ("agrupar", "no"));
        _mediador.LecturaRetenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var cut = Renderizar<Centros>("centros");

        LaLecturaDeLoRecordadoEstaEnVuelo(cut);
        Cargas<ObtenerCentrosQuery>().Should().Be(1, "la lista de fábrica no espera a la lectura de lo recordado");
        Ultima<ObtenerCentrosQuery>().Estados.Should().BeNull();

        _mediador.LecturaRetenida.SetResult();

        cut.WaitForAssertion(() => ParametrosDeLaUrl().Should().ContainKey("estado"));
        cut.WaitForAssertion(() => Ultima<ObtenerCentrosQuery>().Estados.Should().Equal(EstadoCentro.Vencido));
        Cargas<ObtenerCentrosQuery>().Should().Be(2, "restaurar pide la lista una vez más, no dos");
    }

    /// <summary>Una lectura que falla no deja la lista sin cargar ni toca la URL: queda la de fábrica, pedida una vez.</summary>
    [Fact]
    public void Vehiculos_si_la_lectura_de_lo_recordado_falla_queda_la_lista_de_fabrica()
    {
        ConVistaRecordada(PantallasConVistaRecordada.Vehiculos, ("estado", "Vencido"));
        _mediador.FalloDeLectura = new InvalidOperationException("la base no responde");

        var cut = Renderizar<Vehiculos>("vehiculos");

        cut.WaitForAssertion(() => Cargas<ObtenerVehiculosQuery>().Should().Be(1));
        _mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().HaveCount(1, "control: la lectura se intentó");
        ParametrosDeLaUrl().Should().BeEmpty();
        Ultima<ObtenerVehiculosQuery>().EstadoDocumental.Should().BeNull();
    }

    [Fact]
    public void Centros_si_la_lectura_de_lo_recordado_falla_queda_la_lista_de_fabrica()
    {
        ConVistaRecordada(PantallasConVistaRecordada.Centros, ("estado", "Vencido"));
        _mediador.FalloDeLectura = new InvalidOperationException("la base no responde");

        var cut = Renderizar<Centros>("centros");

        cut.WaitForAssertion(() => Cargas<ObtenerCentrosQuery>().Should().Be(1));
        _mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().HaveCount(1, "control: la lectura se intentó");
        ParametrosDeLaUrl().Should().BeEmpty();
        Ultima<ObtenerCentrosQuery>().Estados.Should().BeNull();
    }
}
