using CaeManager.Domain.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Components.Layout;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Proyectos.Commands.ActualizarProyecto;
using CaeManager.Application.Proyectos.Commands.AsignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.CerrarProyecto;
using CaeManager.Application.Proyectos.Commands.ReabrirProyecto;
using CaeManager.Application.Proyectos.Commands.CrearProyecto;
using CaeManager.Application.Proyectos.Commands.DesasignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.EliminarProyecto;
using CaeManager.Application.Proyectos.Commands.RestaurarProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectoPorId;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Application.Proyectos.Queries.ObtenerTecnicosProyecto;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Features.Proyectos.Pages;

public partial class Proyectos : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    /// <summary>
    /// Se cancela al salir de la página: la resolución de la empresa activa que siga en vuelo deja de trabajar
    /// para nadie y su respuesta tardía no repinta un componente ya retirado.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    /// <summary>La empresa gestionada activa, solo para quien ve el selector de la barra lateral.</summary>
    private ClienteAutorizadoDto? _empresaActiva;

    /// <summary>Estado 4a del mockup del selector: hay que elegir una empresa de la cartera antes de ver la lista.</summary>
    private bool _sinEmpresaSeleccionada;

    /// <summary>La empresa activa aún no se ha resuelto: se pinta una carga en vez de la lista.</summary>
    private bool _resolviendoEmpresa = true;

    [Inject] private ITenantActual TenantActual { get; set; } = default!;
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<Proyectos> Logger { get; set; } = default!;

    private bool _cargando = true;
    private bool _errorCarga;
    private bool _cargandoProyectos;
    private bool _errorProyectos;

    private IReadOnlyList<ClienteSelectorDto> _clientes = [];

    /// <summary>
    /// Cliente empresarial del filtro. <see cref="Guid.Empty"/>: sin filtro, los Proyectos de todos los
    /// Clientes empresariales que el usuario alcanza.
    /// </summary>
    private Guid _clienteSeleccionadoId = Guid.Empty;

    /// <summary>
    /// La PÁGINA de proyectos que se enseña: ObtenerProyectosQuery pagina, busca y filtra por estado en
    /// servidor. Cuántos cumplen los filtros en total lo dice <see cref="_totalElementos"/>, y cuántos hay
    /// por estado, <see cref="_recuentosPorEstado"/>; de esta lista no se deduce ninguna de las dos cosas.
    /// </summary>
    private List<ProyectoListaDto> _proyectos = [];

    /// <summary>Proyectos que cumplen todos los filtros, contando todas las páginas.</summary>
    private int _totalElementos;

    /// <summary>
    /// Proyectos por estado con el Cliente empresarial y la búsqueda aplicados y sin el filtro de estado.
    /// <c>null</c> mientras no ha llegado una carga: la franja se pinta igual, sin cifras.
    /// </summary>
    private IReadOnlyDictionary<string, int>? _recuentosPorEstado;

    /// <summary>
    /// Ha llegado la respuesta de la lista vigente. Hasta entonces (primera carga, cambio de Cliente
    /// empresarial) se pinta la carga; recargar la misma lista deja las filas a la vista mientras llegan.
    /// </summary>
    private bool _listaCargada;

    private int _paginaActual = 1;
    /// <summary>El tamaño de página más pequeño que ofrece el paginador: hasta ahí, la lista cabe sin él.</summary>
    private const int TamanoPaginaMinimo = 20;

    private int _tamanoPagina = TamanoPaginaMinimo;

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalElementos / (double)_tamanoPagina));

    /// <summary>La lista y sus acciones (alta, exportar) existen: hay empresa activa y el selector de Clientes empresariales cargó.</summary>
    private bool ListaDisponible => !_resolviendoEmpresa && !_sinEmpresaSeleccionada && !_cargando && !_errorCarga;

    private string _pestanaDetalle = "informacion";

    private static DateOnly Hoy => DiaDeNegocio.Hoy();

    private static int? PorcentajeDelPlazo(ProyectoListaDto proyecto) =>
        PlazoProyecto.PorcentajeDelPlazo(proyecto.FechaInicio, proyecto.FechaFinPrevista, proyecto.FechaCierreReal, Hoy);

    private string EtiquetaPlazoTranscurrido(ProyectoListaDto proyecto) =>
        PorcentajeDelPlazo(proyecto) is { } porcentaje
            ? Textos["EtiquetaPlazoTranscurrido", porcentaje]
            : Textos["EtiquetaPlazoSinMedida"];

    /// <summary>
    /// El motivo bajo la pastilla de estado: cuánto lleva abierto el Proyecto o cuánto duró, con la cuenta
    /// inclusiva de <see cref="PlazoProyecto.DiasAbiertos"/> (la de la ficha 360 y la facturación por días).
    /// </summary>
    private string MotivoEstado(ProyectoListaDto proyecto)
    {
        if (PlazoProyecto.DiasAbiertos(proyecto.FechaInicio, proyecto.FechaCierreReal, Hoy) is not { } dias)
            return proyecto.EstaAbierto ? Textos["PlazoSinEmpezar"] : string.Empty;

        return proyecto.EstaAbierto
            ? Textos[dias == 1 ? "PlazoDiasAbiertoUno" : "PlazoDiasAbiertoVarios", dias]
            : Textos[dias == 1 ? "MotivoDuroUno" : "MotivoDuroVarios", dias];
    }

    /// <summary>
    /// Proyecto abierto pasado de su fin previsto: el mismo texto con que lo dice la ficha 360. La barra
    /// de plazo se queda en el 100 % y por sí sola no distingue «acaba hoy» de «lleva un mes de retraso».
    /// </summary>
    private string? TextoFinPrevistoSuperado(ProyectoListaDto proyecto) =>
        PlazoProyecto.DiasDeRetraso(proyecto.FechaInicio, proyecto.FechaFinPrevista, proyecto.FechaCierreReal, Hoy) is { } dias
            ? Textos[dias == 1 ? "PlazoSuperadoUno" : "PlazoSuperadoVarios", dias].Value
            : null;

    protected override async Task OnInitializedAsync()
    {
        // Hasta resolver la empresa activa no se monta la lista ni sus acciones: con la consulta en
        // vuelo el render saldría con «hay empresa» y lanzaría la carga del Tenant de origen.
        try
        {
            var contexto = await ContextoEmpresaActiva.ResolverAsync(Mediator, TenantActual, _ciclo.Token);
            _empresaActiva = contexto.Activa;
            _sinEmpresaSeleccionada = contexto.SinSeleccion;
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested)
        {
            // La página se retiró con la resolución en vuelo: no queda nadie a quien pintar.
            return;
        }
        finally
        {
            _resolviendoEmpresa = false;
        }

        // Retirada la página, una resolución que vuelva sin lanzar (contexto «Ninguno») no es 4a: no hay
        // a quién pintarle la lista, y seguir pediría los datos de una página que ya no existe.
        if (_desechado)
            return;

        // Sin empresa elegida no se piden los datos de la organización de origen.
        if (_sinEmpresaSeleccionada)
            return;

        // La búsqueda y el estado de la URL se leen antes de la primera carga: OnParametersSetAsync no
        // corre hasta que esto termina, y la primera página tiene que salir ya filtrada.
        SincronizarFiltrosConLaUrl();
        await CargarAsync();
    }

    /// <summary>
    /// Primera carga, y lo que repite «Reintentar» si falla: el selector de Clientes empresariales y,
    /// con él, la primera página de la lista. Sin Cliente empresarial en la URL se listan los de todos.
    /// </summary>
    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            _clientes = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
        }

        if (_errorCarga)
            return;

        // Un enlace o una recarga con ?cliente= abre la lista filtrada por ese Cliente empresarial.
        _clienteSeleccionadoId = ClienteDeLaUrl();
        await CargarProyectosAsync();
    }

    /// <summary>
    /// Cliente empresarial del filtro, en la URL (<c>?cliente=</c>) como en Centros: recargar o
    /// compartir el enlace conserva el filtro.
    /// </summary>
    [SupplyParameterFromQuery(Name = "cliente")]
    public string? ClienteInicial { get; set; }

    /// <summary>
    /// El Cliente empresarial que pide la URL, si es uno de los que este usuario puede elegir. Un Id
    /// ajeno al selector cuenta como ausente (<see cref="Guid.Empty"/>, todos): la URL no puede filtrar
    /// por un Cliente empresarial que el selector no ofrece.
    /// </summary>
    private Guid ClienteDeLaUrl() =>
        Guid.TryParse(ClienteInicial, out var id) && _clientes.Any(c => c.Id == id) ? id : Guid.Empty;

    private void EscribirClienteEnUrl() =>
        NavigationManager.ActualizarFiltroEnUrl(
            "cliente", _clienteSeleccionadoId == Guid.Empty ? null : _clienteSeleccionadoId.ToString());

    private Task OnClienteSeleccionadoAsync(string valor) =>
        SeleccionarClienteAsync(Guid.TryParse(valor, out var id) ? id : Guid.Empty);

    /// <summary>
    /// El selector cambia el Cliente empresarial del filtro («Todos» es <see cref="Guid.Empty"/>) y pide
    /// su lista. Cuando quien lo cambia es la URL, ver <see cref="AtenderOtroClienteEnLaUrlAsync"/>.
    ///
    /// <para>
    /// La URL se escribe con el panel ya cerrado (sin nada pendiente de guardar que detenga la navegación)
    /// y ANTES de la carga, para que no diga el Cliente empresarial anterior mientras llegan los datos:
    /// teclear en el buscador en esa ventana navegaba conservándolo y lo devolvía a la pantalla.
    /// </para>
    /// </summary>
    private async Task SeleccionarClienteAsync(Guid nuevo)
    {
        // Cambiar de Cliente empresarial cierra el panel de detalle: si tenía algo escrito, se pregunta
        // antes y, si se sigue editando, la selección vuelve al Cliente empresarial anterior y se
        // renueva el selector.
        if (nuevo == _clienteSeleccionadoId)
            return;

        if (!await _ambitoDetalle.ConfirmarAbandonoAsync())
        {
            // No se navega: la URL no ha cambiado, y una navegación con el panel todavía sin guardar
            // volvería a preguntar «¿Salir sin guardar?».
            _versionSelectorCliente++;
            _focoSelectorClienteEmpresarialPendiente = true;
            return;
        }

        _clienteSeleccionadoId = nuevo;
        DescartarListaPorCambioDeCliente();
        EscribirClienteEnUrl();
        await CargarProyectosAsync();
    }

    /// <summary>
    /// La URL ha pasado a decir otro Cliente empresarial y la navegación ya ocurrió. Con el panel de detalle
    /// a medias, lo normal es que no llegue hasta aquí: «atrás» y «adelante» del navegador, los enlaces y
    /// <c>NavigateTo</c> los detiene antes el aviso de cambios sin guardar (lo fija el E2E
    /// <c>ProyectosFase1SelectorTests</c>), y aquí solo llegan ya descartados. Queda como segunda línea para
    /// la navegación que el aviso no llegue a ver (un circuito que no contesta a tiempo al navegador):
    /// cambiar de Cliente empresarial cierra el panel de detalle, así que con algo escrito en él se pregunta,
    /// y se pregunta ANTES de tomar nada de la URL: mientras la pregunta está abierta, las pastillas, la
    /// franja y la lista siguen siendo las de la vista que hay en pantalla.
    ///
    /// <para>
    /// «Seguir editando» deja esa vista entera y devuelve la URL a ella (Cliente empresarial, búsqueda y
    /// estado, en una sola navegación). La búsqueda y el estado los filtra la consulta: tomarlos de la URL
    /// nueva sin pedir la lista dejaba las pastillas de una vista sobre las filas, el contador y la franja
    /// de otra. «Salir y descartar» toma los tres de la URL y pide la lista una vez.
    /// </para>
    /// </summary>
    private async Task AtenderOtroClienteEnLaUrlAsync()
    {
        if (!await _ambitoDetalle.ConfirmarAbandonoAsync())
        {
            DevolverLaUrlALaVista();
            return;
        }

        // Tras la espera se lee la URL otra vez: es la que haya ahora, no la que había al preguntar.
        SincronizarFiltrosConLaUrl();
        var clienteDeLaUrl = ClienteDeLaUrl();
        if (clienteDeLaUrl != _clienteSeleccionadoId)
        {
            _clienteSeleccionadoId = clienteDeLaUrl;
            DescartarListaPorCambioDeCliente();
        }

        await RecargarDesdeLaPrimeraPaginaAsync();
    }

    /// <summary>
    /// La página reescribe su propia URL para que diga la vista que sigue en pantalla. No es una salida y
    /// no se pierde nada de lo escrito: el aviso de cambios sin guardar no pregunta por la escritura de
    /// filtros de la propia página (<see cref="CaeManager.Web.Components.NavigationManagerExtensions.EstadoEscrituraDeFiltros"/>).
    /// </summary>
    private void DevolverLaUrlALaVista() =>
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["cliente"] = _clienteSeleccionadoId == Guid.Empty ? null : _clienteSeleccionadoId.ToString(),
            ["q"] = _busqueda,
            ["estado"] = _estadoFiltro
        });

    private int _versionSelectorCliente;
    private PastillaFiltro? _selectorClienteEmpresarial;
    private bool _focoSelectorClienteEmpresarialPendiente;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        if (_focoSelectorClienteEmpresarialPendiente && !_desechado
            && !_resolviendoEmpresa && !_sinEmpresaSeleccionada && !_cargando && !_errorCarga
            && _selectorClienteEmpresarial is { } selector)
        {
            _focoSelectorClienteEmpresarialPendiente = false;
            await selector.EnfocarAsync();
        }
    }

    /// <summary>Los Clientes empresariales del filtro. «Todos» lo añade la pastilla.</summary>
    private IReadOnlyList<OpcionEstado> OpcionesClienteListado =>
        _clientes.Select(c => new OpcionEstado(c.Id.ToString(), c.RazonSocial)).ToList();

    /// <summary>
    /// Lo que deja de valer al cambiar el Cliente empresarial del filtro: la carga en vuelo (su respuesta
    /// tardía no puede pintar la lista del anterior bajo el nuevo), las filas a la vista, la página y el
    /// panel de detalle, que era de un Proyecto de la lista anterior.
    /// </summary>
    private void DescartarListaPorCambioDeCliente()
    {
        _versionCarga++;
        _cargandoProyectos = false;
        _listaCargada = false;
        _proyectos = [];
        _totalElementos = 0;
        _recuentosPorEstado = null;
        _errorProyectos = false;
        _paginaActual = 1;
        CerrarDetalle();
    }

    /// <summary>
    /// Número de la carga de la lista vigente. Cada carga (cambio de Cliente empresarial, de filtro o de
    /// página, «Reintentar», recarga tras crear o cerrar) toma uno nuevo y, tras cada <c>await</c>, solo
    /// escribe si sigue siendo la vigente: sin esto, cambiar de Cliente empresarial A→B con la carga de A
    /// en curso dejaba que la respuesta tardía de A pintase sus proyectos o su error bajo B.
    /// </summary>
    private int _versionCarga;

    /// <summary>
    /// Pide la página vigente con el Cliente empresarial, la búsqueda y el estado vigentes. Es también lo
    /// que repite «Reintentar».
    /// </summary>
    private async Task CargarProyectosAsync()
    {
        var version = ++_versionCarga;
        // Todo lo que define la pregunta se lee antes del await.
        var clienteId = _clienteSeleccionadoId;
        var consulta = new ObtenerProyectosQuery(
            ClienteId: clienteId == Guid.Empty ? null : clienteId,
            SoloAbiertos: FiltroProyectos.SoloAbiertos(_estadoFiltro),
            Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
            Pagina: _paginaActual,
            TamanoPagina: _tamanoPagina,
            ConRecuentosPorEstado: true);
        _cargandoProyectos = true;
        _errorProyectos = false;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(consulta);
            if (version != _versionCarga) return;

            // La página pedida se quedó sin filas pero quedan Proyectos (se eliminó o dejó de cumplir el
            // filtro la última fila de la última página): se enseña la que ahora es la última.
            if (resultado.Elementos.Count == 0 && resultado.TotalElementos > 0 && consulta.Pagina > 1)
            {
                _paginaActual = Math.Max(1, resultado.TotalPaginas);
                resultado = await Mediator.Send(consulta with { Pagina = _paginaActual });
                if (version != _versionCarga) return;
            }

            _proyectos = resultado.Elementos.ToList();
            _totalElementos = resultado.TotalElementos;
            _recuentosPorEstado = resultado.RecuentosPorEstado;
            _listaCargada = true;

            // Si la fila enfocada por teclado ya no está en la página, el foco se descarta. Conservarlo
            // escondido lo haría reaparecer al quitar el filtro, sobre una fila que el usuario ya no
            // tenía delante.
            if (_idEnfocado is { } idEnfocado && !_proyectos.Any(p => p.Id == idEnfocado))
                _idEnfocado = null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "No se pudieron cargar los proyectos (Cliente empresarial del filtro: {ClienteId}).", clienteId);
            if (version != _versionCarga) return;
            _proyectos = [];
            _totalElementos = 0;
            _recuentosPorEstado = null;
            _listaCargada = false;
            _errorProyectos = true;
        }
        finally
        {
            if (version == _versionCarga)
                _cargandoProyectos = false;
        }
    }

    // ---- Paginación (en servidor) ----

    private Task CambiarPaginaAsync(int pagina)
    {
        _paginaActual = Math.Clamp(pagina, 1, TotalPaginas);
        return CargarProyectosAsync();
    }

    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _tamanoPagina = tamano;
        return RecargarDesdeLaPrimeraPaginaAsync();
    }

    /// <summary>Un filtro nuevo cambia qué filas hay: la página en la que se estaba deja de significar nada.</summary>
    private Task RecargarDesdeLaPrimeraPaginaAsync()
    {
        _paginaActual = 1;
        return CargarProyectosAsync();
    }

    // ---- Filtros (Cliente empresarial, estado y búsqueda, en la URL) ----

    private const string EstadoAbiertos = FiltroProyectos.EstadoAbiertos;
    private const string EstadoCerrados = FiltroProyectos.EstadoCerrados;

    // De instancia, no static: las etiquetas salen del localizador inyectado.
    private IReadOnlyList<OpcionFranjaEstado> OpcionesEstado =>
        [new(Textos["FiltroAbiertos"], TonoBadge.Exito, EstadoAbiertos), new(Textos["FiltroCerrados"], TonoBadge.Neutro, EstadoCerrados)];

    /// <summary>
    /// La selección de estados que llega de la URL reducida a los dos que existen; lo demás se descarta.
    /// Cadena vacía si no queda ninguno.
    /// </summary>
    private static string EstadosValidos(string? seleccion) => FiltroProyectos.EstadosValidos(seleccion);

    private string _busqueda = string.Empty;
    private string _estadoFiltro = string.Empty;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    [SupplyParameterFromQuery(Name = "estado")]
    public string? EstadoInicial { get; set; }

    /// <summary>Copia la búsqueda y el estado de la URL a los campos. Dice si alguno cambió.</summary>
    private bool SincronizarFiltrosConLaUrl()
    {
        var cambio = false;

        var busquedaDeLaUrl = TerminoBusquedaInicial ?? string.Empty;
        if (busquedaDeLaUrl != _busqueda)
        {
            _busqueda = busquedaDeLaUrl;
            cambio = true;
        }

        var estadoDeLaUrl = EstadosValidos(EstadoInicial);
        if (estadoDeLaUrl != _estadoFiltro)
        {
            _estadoFiltro = estadoDeLaUrl;
            cambio = true;
        }

        return cambio;
    }

    /// <summary>
    /// La URL es la fuente de verdad de los tres filtros, no solo su semilla: se re-sincroniza en cada
    /// navegación dentro de la página (atrás y adelante del navegador, un enlace a la misma pantalla), y
    /// lo que cambie pide la lista otra vez. Los cambios que hace la propia página ya dejan sus campos
    /// puestos antes de navegar, así que aquí se encuentran iguales y no se carga dos veces. Mientras se
    /// resuelve la empresa activa, en el estado 4a y con la página retirada la URL no se sincroniza.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        // Retirada la página con la resolución en vuelo, ComponentBase aún invoca esto: no se procesan
        // parámetros de la URL de un componente que ya no existe, ni en el estado 4a.
        if (_desechado || _resolviendoEmpresa || _sinEmpresaSeleccionada)
            return;

        // Con el selector aún cargando (o caído) no hay lista: la primera carga leerá estos campos.
        if (_cargando || _errorCarga)
        {
            SincronizarFiltrosConLaUrl();
            return;
        }

        // Otro Cliente empresarial cierra el panel de detalle: pregunta antes de tomar nada de la URL, y su
        // carga lleva la búsqueda y el estado que la URL diga entonces.
        if (ClienteDeLaUrl() != _clienteSeleccionadoId)
        {
            await AtenderOtroClienteEnLaUrlAsync();
            return;
        }

        // Solo cambian la búsqueda o el estado: el panel de detalle no se cierra, así que no hay nada que
        // preguntar.
        if (SincronizarFiltrosConLaUrl())
            await RecargarDesdeLaPrimeraPaginaAsync();
    }

    /// <summary>Hay búsqueda o estado: lo que hace que «ninguna fila» no signifique «no hay proyectos».</summary>
    private bool HayBusquedaOEstado =>
        !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_estadoFiltro);

    private bool HayFiltrosActivos => HayBusquedaOEstado || _clienteSeleccionadoId != Guid.Empty;

    /// <summary>
    /// El único filtro puesto es el Cliente empresarial. Con cero filas, eso es un Cliente empresarial sin
    /// Proyectos (se invita a crear el primero), no un vacío por filtro.
    /// </summary>
    private bool SoloFiltraElCliente => _clienteSeleccionadoId != Guid.Empty && !HayBusquedaOEstado;

    private Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        return RecargarDesdeLaPrimeraPaginaAsync();
    }

    private Task CambiarEstadoAsync(string? valor)
    {
        _estadoFiltro = valor ?? string.Empty;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        return RecargarDesdeLaPrimeraPaginaAsync();
    }

    private Task QuitarBusquedaAsync() => BuscarAsync(string.Empty);

    /// <summary>
    /// Quita los tres filtros, y los tres TAMBIÉN de la URL en una sola navegación:
    /// <see cref="OnParametersSetAsync"/> re-sincroniza desde la URL, así que dejarlos allí los
    /// devolvería en cuanto el router volviera a pasar. Quitar el Cliente empresarial cierra el panel de
    /// detalle, igual que elegir otro: si tiene algo escrito se pregunta antes, y «seguir editando» deja
    /// los filtros como estaban.
    /// </summary>
    private async Task LimpiarFiltrosAsync()
    {
        var quitaElCliente = _clienteSeleccionadoId != Guid.Empty;
        if (quitaElCliente && !await _ambitoDetalle.ConfirmarAbandonoAsync())
            return;

        _busqueda = string.Empty;
        _estadoFiltro = string.Empty;
        if (quitaElCliente)
        {
            _clienteSeleccionadoId = Guid.Empty;
            DescartarListaPorCambioDeCliente();
        }

        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?> { ["q"] = null, ["estado"] = null, ["cliente"] = null });
        await RecargarDesdeLaPrimeraPaginaAsync();
    }

    // ---- Filtros guardados (pieza compartida FiltrosGuardadosDeListado) ----

    private const string PantallaDeFiltrosGuardados =
        CaeManager.Application.Configuracion.Commands.GuardarFiltro.PantallasConFiltrosGuardados.Proyectos;

    /// <summary>
    /// Lista blanca de los parámetros de VISTA de la URL: lo que guarda y aplica un filtro guardado. El
    /// Cliente empresarial del filtro es parte de la vista; uno guardado sin él lista los de todos.
    /// </summary>
    public static readonly IReadOnlyList<string> ParametrosDeVista = ["cliente", "q", "estado"];

    private readonly ConexionFiltrosGuardados _filtrosGuardados = new();

    /// <summary>
    /// De la vista recordada (<see cref="VistaRecordadaDeListado"/>). El Cliente empresarial es un filtro
    /// más: la lista existe sin él, así que elegirlo cuenta como desviación de la vista de inicio, se
    /// recuerda y «Restablecer vista» lo quita, igual que «Quitar filtros».
    /// </summary>
    private readonly ConexionVistaRecordada _vistaRecordada = new();

    /// <summary>
    /// Un filtro guardado define la vista entera: lo que no trae se quita, también el Cliente empresarial.
    /// Cada valor pasa por la misma validación que el de la URL: el estado por <see cref="EstadosValidos"/>
    /// y el Cliente empresarial solo si es uno de los que el selector ofrece (un Id guardado no es
    /// autoridad; uno que ya no se ofrece cuenta como ausente).
    ///
    /// <para>
    /// Con algo a medias en el panel de detalle se pregunta UNA vez y antes de tocar nada, cambie o no el
    /// Cliente empresarial. La pregunta es de la página: la escritura de filtros en la URL ya no la detiene
    /// el aviso de cambios sin guardar (antes sí, y al descartar repetía la navegación sin reemplazo). «Seguir
    /// editando» deja la vista como estaba.
    /// </para>
    ///
    /// <para>
    /// Después, en este orden: campos, panel cerrado (solo si cambia el Cliente empresarial), URL y, solo
    /// entonces, la carga. La URL va antes de la carga para que no diga el Cliente empresarial anterior
    /// mientras llegan los datos (teclear en el buscador en esa ventana navegaba conservándolo y lo devolvía a
    /// la pantalla), y con los campos ya puestos <see cref="OnParametersSetAsync"/> los encuentra iguales y no
    /// carga otra vez. La búsqueda y el estado los filtra la consulta: si la vista no cambia ninguno de los
    /// tres, la lista que hay ya es la suya y no se vuelve a pedir.
    /// </para>
    /// </summary>
    private async Task AplicarVistaGuardadaAsync(IReadOnlyDictionary<string, string?> vista)
    {
        if (!await _ambitoDetalle.ConfirmarAbandonoAsync())
            return;

        var cliente = Guid.TryParse(vista.GetValueOrDefault("cliente"), out var id) && _clientes.Any(c => c.Id == id)
            ? id
            : Guid.Empty;
        var cambiaDeCliente = cliente != _clienteSeleccionadoId;
        var busqueda = vista.GetValueOrDefault("q") ?? string.Empty;
        var estado = EstadosValidos(vista.GetValueOrDefault("estado"));
        var cambianLosFiltros = busqueda != _busqueda || estado != _estadoFiltro;

        _busqueda = busqueda;
        _estadoFiltro = estado;
        if (cambiaDeCliente)
        {
            _clienteSeleccionadoId = cliente;
            // El panel era de un proyecto de la lista anterior.
            DescartarListaPorCambioDeCliente();
        }

        // Una sola navegación, con todos los parámetros de la vista y sin nada pendiente de guardar.
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["cliente"] = cliente == Guid.Empty ? null : cliente.ToString(),
            ["q"] = _busqueda,
            ["estado"] = _estadoFiltro,
        });

        if (cambiaDeCliente || cambianLosFiltros)
            await RecargarDesdeLaPrimeraPaginaAsync();
    }

    // ---- Nuevo proyecto (Drawer) ----

    private bool _drawerVisible;

    /// <summary>
    /// Cliente empresarial del proyecto nuevo. La lista no obliga a elegir uno, así que lo pide el
    /// formulario: CrearProyectoCommand exige un Cliente empresarial y un Centro vinculado a él. Viene
    /// puesto con el del filtro cuando hay uno.
    /// </summary>
    private string _nuevoClienteId = string.Empty;

    /// <summary>Centros del Cliente empresarial elegido en el formulario de alta.</summary>
    private IReadOnlyList<CentroSelectorDto> _centrosDisponibles = [];

    /// <summary>Número de la carga de centros vigente: la respuesta de un Cliente empresarial anterior no pinta sus centros bajo el nuevo.</summary>
    private int _versionCentrosAlta;

    private string _nuevoCentroId = string.Empty;
    private string _nuevoNombre = string.Empty;
    private string _nuevaFechaInicio = string.Empty;
    private string _nuevaFechaFinPrevista = string.Empty;
    private string _nuevasNotas = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private async Task AbrirNuevoProyectoAsync()
    {
        _nuevoClienteId = _clienteSeleccionadoId == Guid.Empty ? string.Empty : _clienteSeleccionadoId.ToString();
        _centrosDisponibles = [];
        _nuevoCentroId = string.Empty;
        _nuevoNombre = string.Empty;
        _nuevaFechaInicio = Hoy.ToString("yyyy-MM-dd");
        _nuevaFechaFinPrevista = string.Empty;
        _nuevasNotas = string.Empty;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();
        _drawerVisible = true;
        _instantanea.Fijar(ValoresFormulario());
        await CargarCentrosDelAltaAsync();
    }

    private Task OnClienteDelAltaCambiadoAsync(string valor)
    {
        _nuevoClienteId = valor;
        // El Centro elegido era del Cliente empresarial anterior.
        _nuevoCentroId = string.Empty;
        return CargarCentrosDelAltaAsync();
    }

    /// <summary>Centros del Cliente empresarial elegido en el formulario; sin Cliente empresarial, ninguno.</summary>
    private async Task CargarCentrosDelAltaAsync()
    {
        var version = ++_versionCentrosAlta;
        _centrosDisponibles = [];
        if (!Guid.TryParse(_nuevoClienteId, out var clienteId))
            return;

        try
        {
            var centros = await Mediator.Send(new ObtenerCentrosParaSelectorQuery(ClienteId: clienteId));
            if (version != _versionCentrosAlta) return;
            _centrosDisponibles = centros;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "No se pudieron cargar los centros del Cliente empresarial {ClienteId} para el alta de proyecto.", clienteId);
            if (version != _versionCentrosAlta) return;
            _mensajeErrorFormulario = Textos["ErrorCargarCentros"];
        }
    }

    private readonly InstantaneaFormulario _instantanea = new();
    private readonly InstantaneaFormulario _instantaneaCierre = new();

    private readonly InstantaneaFormulario _instantaneaEdicionInfo = new();
    private readonly InstantaneaFormulario _instantaneaTecnico = new();

    /// <summary>
    /// P1-E2b: «hay cambios» del aviso de la página: el drawer de nuevo proyecto y la edición de la información o el alta de
    /// técnico del panel de detalle, comparados con cómo se abrieron (la fecha de hoy que traen puesta no es un cambio). Lo
    /// lee AvisoCambiosSinGuardar; cerrados (también tras guardar) nunca hay nada que perder. El modal de cerrar proyecto
    /// (ModalFormulario) lleva el suyo en <see cref="HayCambiosEnElModalDeCierre"/> y su propio aviso de navegación; cada
    /// drawer o modal pregunta solo por su propio contenido (HayCambiosEnElDrawer, HayCambiosEnElModalDeCierre).
    /// </summary>
    private bool HayCambiosSinGuardar =>
        HayCambiosEnElDrawer || HayCambiosEnElDetalle;

    private bool HayCambiosEnElDrawer => _drawerVisible && _instantanea.Difiere(ValoresFormulario());

    private bool HayCambiosEnElModalDeCierre => _mostrarCerrarConfirm && _instantaneaCierre.Difiere(_fechaCierre);

    /// <summary>
    /// La edición de la información y el alta de técnico del panel de detalle: cerrar el panel,
    /// abrir otro proyecto o cambiar de Cliente empresarial los descartan sin navegar, así que
    /// esas salidas preguntan con el mismo aviso (ConfirmarAbandonoAsync del ámbito).
    /// </summary>
    private bool HayCambiosEnElDetalle =>
        (_editandoInfo && _instantaneaEdicionInfo.Difiere(ValoresEdicionInfo()))
        || (_mostrarFormularioTecnico && _instantaneaTecnico.Difiere(ValoresTecnico()));

    private object?[] ValoresEdicionInfo() => [_editNombre, _editFechaFinPrevista, _editNotas];

    private object?[] ValoresTecnico() => [_nuevoTecnicoTrabajadorId, _nuevoTecnicoFechaAlta];

    /// <summary>
    /// El panel de detalle como zona que se desmonta sin navegar: cerrarlo, abrir otro
    /// proyecto o cambiar de Cliente empresarial preguntan con el aviso antes de hacerlo.
    /// </summary>
    private readonly AmbitoCambiosSinGuardar _ambitoDetalle = new();

    /// <summary>Cierra el panel de detalle (la X) preguntando antes si hay algo escrito que se perdería.</summary>
    private async Task CerrarDetalleConAvisoAsync()
    {
        if (!await _ambitoDetalle.ConfirmarAbandonoAsync()) return;
        CerrarDetalle();
    }

    /// <summary>
    /// Cerrar, reabrir o eliminar el proyecto cuyo detalle está abierto recarga o cierra el panel y tira lo que
    /// haya a medias en él (edición de información, alta de técnico): se pregunta antes de pedir la confirmación
    /// propia de la acción. Sobre otra fila no se pierde nada del panel, así que no pregunta.
    /// </summary>
    private async Task<bool> ConfirmarQueNoSePierdeElDetalleAsync(Guid idDelProyecto) =>
        idDelProyecto != _proyectoSeleccionadoId
        || !HayCambiosEnElDetalle
        || await _ambitoDetalle.ConfirmarAbandonoAsync();

    /// <summary>Abre el detalle de otro proyecto preguntando antes si el panel actual tiene cambios sin guardar.</summary>
    private async Task AbrirDetalleConAvisoAsync(Guid id)
    {
        if (!await _ambitoDetalle.ConfirmarAbandonoAsync()) return;
        await SeleccionarProyectoAsync(id);
    }

    /// <summary>
    /// Clic en cualquier punto de la fila. Sobre el proyecto que ya está abierto no hace nada: la
    /// fila entera es una diana grande, y un clic de más no debe devolver el panel a «Información»
    /// ni sacarlo de la edición.
    /// </summary>
    private Task AbrirDetalleDesdeLaFilaAsync(Guid id) =>
        _proyectoSeleccionadoId == id && _detalle is not null ? Task.CompletedTask : AbrirDetalleConAvisoAsync(id);

    /// <summary>
    /// Un técnico de la ventana de contexto del recuento: abre el panel del proyecto en la pestaña
    /// «Técnicos», que es donde se le da de baja o se asigna otro.
    /// </summary>
    private async Task AbrirDetalleEnTecnicosAsync(Guid id)
    {
        if (!await _ambitoDetalle.ConfirmarAbandonoAsync()) return;

        if (_proyectoSeleccionadoId != id || _detalle is null)
            await SeleccionarProyectoAsync(id);
        if (_proyectoSeleccionadoId != id || _detalle is null) return;

        CancelarEdicionInfo();
        _mostrarFormularioTecnico = false;
        await CambiarPestanaDetalleAsync("tecnicos");
    }

    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }

    /// <summary>
    /// Tecla «e»: el panel del proyecto, ya en edición (lo mismo que su lápiz). A quien no puede
    /// escribir se le abre en lectura: el lápiz tampoco se le ofrece. Si ese proyecto ya se está
    /// editando, no se toca lo escrito; lo demás que haya a medias en el panel (otro proyecto en
    /// edición, un alta de técnico) se pregunta antes de tirarlo.
    /// </summary>
    private async Task AbrirDetalleEnEdicionAsync(Guid id)
    {
        if (_proyectoSeleccionadoId == id && _editandoInfo) return;

        var mismoProyecto = _proyectoSeleccionadoId == id && _detalle is not null;
        if (mismoProyecto && !await SoloConEscritura.PuedeEscribirAsync(EstadoAutenticacion)) return;
        if (!await _ambitoDetalle.ConfirmarAbandonoAsync()) return;
        if (!mismoProyecto)
            await SeleccionarProyectoAsync(id);

        if (_proyectoSeleccionadoId != id || _detalle is null) return;
        if (!await SoloConEscritura.PuedeEscribirAsync(EstadoAutenticacion)) return;

        _mostrarFormularioTecnico = false;
        IniciarEdicionInfo();
    }

    private object?[] ValoresFormulario() => [_nuevoClienteId, _nuevoCentroId, _nuevoNombre, _nuevaFechaInicio, _nuevaFechaFinPrevista, _nuevasNotas];

    private void CerrarFormulariosDescartando()
    {
        _drawerVisible = false;
        _mostrarFormularioTecnico = false;
        CancelarEdicionInfo();
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private async Task CrearProyectoAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();
        StateHasChanged();

        try
        {
            if (!Guid.TryParse(_nuevoClienteId, out var clienteId))
            {
                _mensajeErrorFormulario = Textos["ErrorFaltaCliente"];
                return;
            }

            if (!Guid.TryParse(_nuevoCentroId, out var centroId))
            {
                _mensajeErrorFormulario = Textos["ErrorFaltaCentro"];
                return;
            }

            if (!DateOnly.TryParse(_nuevaFechaInicio, out var fechaInicio))
            {
                _mensajeErrorFormulario = Textos["ErrorFechaInicio"];
                return;
            }

            DateOnly? fechaFinPrevista = DateOnly.TryParse(_nuevaFechaFinPrevista, out var fv) ? fv : null;
            var notas = string.IsNullOrWhiteSpace(_nuevasNotas) ? null : _nuevasNotas;

            var resultado = await Mediator.Send(new CrearProyectoCommand(
                clienteId, centroId, _nuevoNombre, fechaInicio, fechaFinPrevista, notas));

            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(Textos["ToastCreado"], TonoToast.Exito);
            _drawerVisible = false;
            await CargarProyectosAsync();
        }
        catch (ValidationException ex)
        {
            _erroresCampo = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _mensajeErrorFormulario = Textos["ErrorCrear"];
        }
        finally
        {
            _guardando = false;
        }
    }

    // ---- Atajos de lista j/k/Enter (I-8 de AUDITORIA-USUARIO-AVANZADO-POST-GEN2) ----

    /// <summary>
    /// Fila enfocada por teclado. Es distinta de
    /// <see cref="_proyectoSeleccionadoId"/>: el foco recorre la lista sin
    /// abrir nada, y solo <c>Enter</c> lo convierte en selección.
    /// </summary>
    private Guid? _idEnfocado;

    private string ClaseFila(ProyectoListaDto proyecto)
    {
        var seleccionada = _proyectoSeleccionadoId == proyecto.Id;
        var enfocada = _idEnfocado == proyecto.Id;
        return (seleccionada, enfocada) switch
        {
            (true, true) => "fila-seleccionada fila-enfocada",
            (true, false) => "fila-seleccionada",
            (false, true) => "fila-enfocada",
            _ => string.Empty
        } + " fila-pulsable";
    }

    private int IndiceEnfocado(IReadOnlyList<ProyectoListaDto> visibles)
    {
        if (_idEnfocado is null) return -1;
        for (var i = 0; i < visibles.Count; i++)
            if (visibles[i].Id == _idEnfocado) return i;

        // Red de seguridad: el foco de una fila que ya no está en la página se descarta al
        // cargarla, pero si por cualquier camino sobreviviera, aquí se
        // trata como si no hubiera foco en vez de apuntar a algo invisible.
        return -1;
    }

    private async Task ManejarAtajoAsync(string tecla)
    {
        var visibles = _proyectos;

        // «e»: el lápiz del panel sobre la fila enfocada; sin fila enfocada, sobre el proyecto
        // cuyo panel está abierto.
        if (tecla == "e")
        {
            var idEditar = _idEnfocado is not null && IndiceEnfocado(visibles) >= 0 ? _idEnfocado : _proyectoSeleccionadoId;
            if (idEditar is { } id)
                await AbrirDetalleEnEdicionAsync(id);
            return;
        }

        if (visibles.Count == 0) return;

        switch (tecla)
        {
            case "j":
                _idEnfocado = visibles[Math.Min(IndiceEnfocado(visibles) + 1, visibles.Count - 1)].Id;
                break;
            case "k":
                {
                    var indice = IndiceEnfocado(visibles);
                    _idEnfocado = visibles[indice <= 0 ? 0 : indice - 1].Id;
                    break;
                }
            case "Enter":
                // § 6.1 quater del contrato: abrir es lo que hace el botón con
                // el nombre del proyecto, no el enlace de la celda "Centro" —
                // aquí se llama al mismo método que ese botón, así que el bug
                // histórico del enlace equivocado no puede reaparecer.
                if (_idEnfocado is { } idAbrir && IndiceEnfocado(visibles) >= 0)
                {
                    await AbrirDetalleConAvisoAsync(idAbrir);
                    return;
                }
                break;

                // "x" no tiene efecto en Proyectos y es deliberado: la pantalla no
                // tiene selección múltiple —ni casillas, ni BarraAccionesLote—, así
                // que no hay nada que marcar. Darle un significado nuevo sería una
                // decisión de producto, no la reparación de este hueco (mismo caso
                // que I-12 en Estado Comercial).
        }

        StateHasChanged();
    }

    // ---- Detalle de proyecto (panel lateral: Información, Técnicos, Documentos) ----

    private Guid? _proyectoSeleccionadoId;
    private ProyectoDetalleDto? _detalle;
    private bool _cargandoDetalle;

    /// <summary>
    /// Número de la selección de detalle vigente: cada selección y cada cierre
    /// del panel toman uno nuevo. Pulsar A y enseguida B dejaba que la
    /// respuesta tardía de A se pintase en el panel de B —y Editar/Cerrar,
    /// que usan <c>_detalle.Id</c>, operaban sobre A—. También invalida los
    /// técnicos pedidos para un detalle que ya no está abierto.
    /// </summary>
    private int _versionDetalle;

    private async Task SeleccionarProyectoAsync(Guid id)
    {
        var version = ++_versionDetalle;
        _proyectoSeleccionadoId = id;
        _pestanaDetalle = "informacion";
        _editandoInfo = false;
        _mostrarFormularioTecnico = false;
        _cargandoDetalle = true;
        _detalle = null;
        _tecnicos = [];
        _cargandoTecnicos = false;
        StateHasChanged();

        try
        {
            var detalle = await Mediator.Send(new ObtenerProyectoPorIdQuery(id));
            if (version != _versionDetalle) return;

            _detalle = detalle;
            if (_detalle is not null)
            {
                _editNombre = _detalle.Nombre;
                _editFechaFinPrevista = _detalle.FechaFinPrevista?.ToString("yyyy-MM-dd") ?? string.Empty;
                _editNotas = _detalle.Notas ?? string.Empty;
            }
        }
        finally
        {
            if (version == _versionDetalle)
                _cargandoDetalle = false;
        }
    }

    private void CerrarDetalle()
    {
        _versionDetalle++;
        _proyectoSeleccionadoId = null;
        _detalle = null;
        _editandoInfo = false;
        _mostrarFormularioTecnico = false;
        _mostrarCerrarConfirm = false;
    }

    private Task CambiarPestanaDetalleAsync(string pestana)
    {
        _pestanaDetalle = pestana;

        if (pestana == "tecnicos" && _detalle is not null && _tecnicos.Count == 0 && !_cargandoTecnicos)
            return CargarTecnicosAsync();

        return Task.CompletedTask;
    }

    private static int? DiasAbiertos(DateOnly inicio, DateOnly? cierre, DateOnly hoy) =>
        PlazoProyecto.DiasAbiertos(inicio, cierre, hoy);

    private string TextoTecnicosActivos(int tecnicosActivos) =>
        tecnicosActivos == 1
            ? Textos["TecnicosActivosUno", tecnicosActivos].Value
            : Textos["TecnicosActivosVarios", tecnicosActivos].Value;

    private string MetaTecnico(TecnicoProyectoDto tecnico)
    {
        var partes = new List<string>();
        partes.Add(Textos["MetaAltaFecha", tecnico.FechaAlta]);
        if (tecnico.FechaBaja is { } baja)
            partes.Add(Textos["MetaBajaFecha", baja]);
        return string.Join(" · ", partes);
    }

    // ---- Editar información ----

    private bool _editandoInfo;
    private string _editNombre = string.Empty;
    private string _editFechaFinPrevista = string.Empty;
    private string _editNotas = string.Empty;
    private Dictionary<string, string> _editErrores = new();
    private string? _editError;

    /// <summary>El lápiz de la cabecera del panel (y la tecla «e»): lleva a la pestaña Información, que es la que se edita.</summary>
    private void IniciarEdicionInfo()
    {
        _pestanaDetalle = "informacion";
        _editandoInfo = true;
        _instantaneaEdicionInfo.Fijar(ValoresEdicionInfo());
    }

    private void CancelarEdicionInfo()
    {
        _editandoInfo = false;
        _editErrores = new();
        _editError = null;

        if (_detalle is not null)
        {
            _editNombre = _detalle.Nombre;
            _editFechaFinPrevista = _detalle.FechaFinPrevista?.ToString("yyyy-MM-dd") ?? string.Empty;
            _editNotas = _detalle.Notas ?? string.Empty;
        }
    }

    private async Task GuardarEdicionInfoAsync()
    {
        if (_detalle is null) return;

        // El id se fija antes del await: mientras se guarda, el usuario puede
        // abrir otro proyecto y _detalle pasar a ser otro, o null mientras
        // carga (y entonces _detalle.Id reventaba tras un guardado correcto).
        var id = _detalle.Id;
        _editErrores = new();
        _editError = null;
        _guardando = true;
        StateHasChanged();

        try
        {
            DateOnly? fechaFinPrevista = DateOnly.TryParse(_editFechaFinPrevista, out var fv) ? fv : null;
            var notas = string.IsNullOrWhiteSpace(_editNotas) ? null : _editNotas;

            var resultado = await Mediator.Send(
                new ActualizarProyectoCommand(id, _editNombre, fechaFinPrevista, notas, _detalle.Version));

            if (resultado.EsFallido)
            {
                _editError = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(Textos["ToastActualizado"], TonoToast.Exito);
            _editandoInfo = false;

            // Solo se refresca el detalle si sigue siendo el abierto: si el
            // usuario ya eligió otro, recargar este le devolvería el panel.
            if (_proyectoSeleccionadoId == id)
                await SeleccionarProyectoAsync(id);

            await CargarProyectosAsync();
        }
        catch (ValidationException ex)
        {
            _editErrores = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _editError = Textos["ErrorGuardar"];
        }
        finally
        {
            _guardando = false;
        }
    }

    // ---- Cerrar proyecto ----

    private bool _mostrarCerrarConfirm;

    /// <summary>
    /// El proyecto que el modal va a cerrar. Separado de
    /// <see cref="_proyectoSeleccionadoId"/> a propósito: antes el modal
    /// reutilizaba la selección del detalle, y cerrar desde la fila de un
    /// proyecto sin detalle abierto hacía aparecer el detalle vacío con
    /// "No pudimos cargar este proyecto".
    /// </summary>
    private Guid? _idACerrar;
    private string _fechaCierre = string.Empty;
    private string? _errorCierre;

    private async Task AbrirCerrarConfirmAsync(Guid id)
    {
        if (!await ConfirmarQueNoSePierdeElDetalleAsync(id)) return;
        _idACerrar = id;
        _fechaCierre = Hoy.ToString("yyyy-MM-dd");
        _errorCierre = null;
        _mostrarCerrarConfirm = true;
        _instantaneaCierre.Fijar(_fechaCierre);
    }

    private async Task ConfirmarCerrarAsync()
    {
        if (_idACerrar is not { } idACerrar) return;

        if (!DateOnly.TryParse(_fechaCierre, out var fechaCierre))
        {
            _errorCierre = Textos["ErrorFechaCierre"];
            return;
        }

        _guardando = true;
        _errorCierre = null;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new CerrarProyectoCommand(idACerrar, fechaCierre));

            if (resultado.EsFallido)
            {
                _errorCierre = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(Textos["ToastCerrado"], TonoToast.Exito);
            _mostrarCerrarConfirm = false;
            await CargarProyectosAsync();

            if (_detalle is not null && _detalle.Id == idACerrar)
                await SeleccionarProyectoAsync(_detalle.Id);
        }
        finally
        {
            _guardando = false;
        }
    }

    // ---- Reabrir proyecto ----

    /// <summary>
    /// Salida del cierre (FS-12): un cierre con la fecha equivocada afecta a la
    /// facturación por días, así que el proyecto cerrado ofrece «Reabrir». Pide
    /// confirmación nombrando la fecha de cierre, porque reabrir la borra y volver
    /// a cerrar propone la de hoy, no la anterior.
    /// </summary>
    private bool _confirmarReabrirVisible;
    private Guid _idAReabrir;
    private string _nombreAReabrir = string.Empty;
    private DateOnly? _fechaCierreAReabrir;
    private bool _reabriendo;

    private async Task AbrirReabrirConfirmAsync(Guid id)
    {
        if (!await ConfirmarQueNoSePierdeElDetalleAsync(id)) return;
        var fila = _proyectos.FirstOrDefault(p => p.Id == id);
        _idAReabrir = id;
        _nombreAReabrir = fila?.Nombre ?? _detalle?.Nombre ?? string.Empty;
        _fechaCierreAReabrir = fila?.FechaCierreReal ?? _detalle?.FechaCierreReal;
        _confirmarReabrirVisible = true;
    }

    private async Task ConfirmarReabrirAsync()
    {
        if (_reabriendo) return;
        _reabriendo = true;
        var id = _idAReabrir;

        try
        {
            var resultado = await Mediator.Send(new ReabrirProyectoCommand(id));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(Textos["ToastReabierto"], TonoToast.Exito);
            _confirmarReabrirVisible = false;
            await CargarProyectosAsync();

            if (_detalle is not null && _detalle.Id == id)
                await SeleccionarProyectoAsync(_detalle.Id);
        }
        finally
        {
            _reabriendo = false;
        }
    }

    // ---- Eliminar proyecto ----

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _nombreAEliminar = string.Empty;
    private bool _eliminando;

    private async Task AbrirEliminarAsync(Guid id, string nombre)
    {
        if (!await ConfirmarQueNoSePierdeElDetalleAsync(id)) return;
        _idAEliminar = id;
        _nombreAEliminar = nombre;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        _eliminando = true;

        try
        {
            var idEliminado = _idAEliminar;
            var resultado = await Mediator.Send(new EliminarProyectoCommand(idEliminado));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(Textos["ToastEliminado"], TonoToast.Exito, Textos["ToastAccionDeshacer"], () => DeshacerEliminarAsync(idEliminado));
            _confirmarEliminarVisible = false;

            if (_proyectoSeleccionadoId == idEliminado)
                CerrarDetalle();

            await CargarProyectosAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ErrorEliminar"], TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    /// <summary>«Deshacer» del aviso tras eliminar — ver RestaurarProyectoCommand.</summary>
    private async Task DeshacerEliminarAsync(Guid id)
    {
        // Guarda por elemento: dos pulsaciones en «Deshacer» del mismo aviso no mandan dos restauraciones.
        if (!_restaurando.Add(id)) return;

        try
        {
            var resultado = await Mediator.Send(new RestaurarProyectoCommand(id));

            ToastService.Mostrar(
                resultado.EsExitoso ? Textos["ToastRestaurado"].Value : resultado.Error.Mensaje,
                resultado.EsExitoso ? TonoToast.Exito : TonoToast.Error);

            if (resultado.EsExitoso)
                await CargarProyectosAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ErrorRestaurar"], TonoToast.Error);
        }
        finally
        {
            _restaurando.Remove(id);
        }
    }

    private readonly HashSet<Guid> _restaurando = [];

    // ---- Técnicos ----

    private List<TecnicoProyectoDto> _tecnicos = [];
    private bool _cargandoTecnicos;
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private bool _mostrarFormularioTecnico;
    private string _nuevoTecnicoTrabajadorId = string.Empty;
    private string _nuevoTecnicoFechaAlta = string.Empty;
    private string? _errorTecnico;

    private async Task CargarTecnicosAsync()
    {
        if (_detalle is null) return;

        var version = _versionDetalle;
        _cargandoTecnicos = true;
        StateHasChanged();

        try
        {
            var tecnicos = await Mediator.Send(new ObtenerTecnicosProyectoQuery(_detalle.Id));
            if (version != _versionDetalle) return;
            _tecnicos = tecnicos.ToList();
        }
        finally
        {
            if (version == _versionDetalle)
                _cargandoTecnicos = false;
        }
    }

    private async Task AbrirFormularioTecnicoAsync()
    {
        if (_trabajadoresDisponibles.Count == 0)
            _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery(AlcanceSelectorTrabajadores.Cartera));

        _nuevoTecnicoTrabajadorId = string.Empty;
        _nuevoTecnicoFechaAlta = Hoy.ToString("yyyy-MM-dd");
        _errorTecnico = null;
        _mostrarFormularioTecnico = true;
        _instantaneaTecnico.Fijar(ValoresTecnico());
    }

    private async Task AsignarTecnicoAsync()
    {
        if (_detalle is null) return;

        if (!Guid.TryParse(_nuevoTecnicoTrabajadorId, out var trabajadorId))
        {
            _errorTecnico = Textos["ErrorFaltaTecnico"];
            return;
        }

        if (!DateOnly.TryParse(_nuevoTecnicoFechaAlta, out var fechaAlta))
        {
            _errorTecnico = Textos["ErrorFechaAlta"];
            return;
        }

        _guardando = true;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new AsignarTecnicoProyectoCommand(_detalle.Id, trabajadorId, fechaAlta));

            if (resultado.EsFallido)
            {
                _errorTecnico = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(Textos["ToastTecnicoAsignado"], TonoToast.Exito);
            _mostrarFormularioTecnico = false;
            await CargarTecnicosAsync();
        }
        finally
        {
            _guardando = false;
        }
    }

    // Dar de baja a un técnico lo saca de la facturación por días del proyecto
    // y la aplicación no lo deshace: se confirma antes, como eliminar un proyecto.
    private TecnicoProyectoDto? _tecnicoADarDeBaja;
    private bool _dandoDeBajaTecnico;

    private string MensajeConfirmarBajaTecnico => _tecnicoADarDeBaja is null
        ? string.Empty
        : Textos["ConfirmarBajaTecnicoMensaje", _tecnicoADarDeBaja.TrabajadorNombreCompleto];

    private void PedirDarDeBajaTecnico(TecnicoProyectoDto tecnico) => _tecnicoADarDeBaja = tecnico;

    private void CerrarConfirmacionBajaTecnico(bool visible)
    {
        if (!visible && !_dandoDeBajaTecnico)
            _tecnicoADarDeBaja = null;
    }

    private async Task ConfirmarBajaTecnicoAsync()
    {
        if (_tecnicoADarDeBaja is not { } tecnico)
            return;

        _dandoDeBajaTecnico = true;
        try
        {
            await DesasignarTecnicoAsync(tecnico.Id);
        }
        finally
        {
            _dandoDeBajaTecnico = false;
            _tecnicoADarDeBaja = null;
        }
    }

    private async Task DesasignarTecnicoAsync(Guid id)
    {
        try
        {
            var resultado = await Mediator.Send(new DesasignarTecnicoProyectoCommand(id, Hoy));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(Textos["ToastTecnicoDeBaja"], TonoToast.Exito);
            await CargarTecnicosAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ErrorDarDeBaja"], TonoToast.Error);
        }
    }

    // ---- Exportar esta vista ----

    /// <summary>
    /// Los criterios de la vista con los nombres de parámetro de <c>/proyectos/exportar.xlsx</c>:
    /// el Cliente empresarial del filtro, la búsqueda y los estados de la franja. Son los tres
    /// filtros que lleva la consulta de la lista; la página no es un criterio (se exportan todas).
    /// </summary>
    private Dictionary<string, string?> CriteriosExportar => new()
    {
        ["cliente"] = _clienteSeleccionadoId == Guid.Empty ? null : _clienteSeleccionadoId.ToString(),
        ["q"] = _busqueda,
        ["estado"] = _estadoFiltro,
    };
}
