using CaeManager.Application.BusquedaGlobal.Commands.RegistrarUsoReciente;
using CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;
using CaeManager.Application.BusquedaGlobal.Queries.ObtenerRecientes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.BusquedaGlobal;

public partial class BuscadorGlobal : ComponentBase
{
    private static readonly TimeSpan RetardoDebounce = TimeSpan.FromMilliseconds(250);

    private IJSObjectReference? _modulo;
    private IJSObjectReference? _suscripcionAtajo;
    private IJSObjectReference? _suscripcionTab;
    private DotNetObjectReference<BuscadorGlobal>? _referenciaDotNet;
    private ElementReference _inputElemento;

    private bool _visible;
    private string _termino = string.Empty;

    /// <summary>
    /// Lo que se pinta en value="@_valorMostrado" del input — deliberadamente
    /// separado de _termino (que sí se actualiza en cada tecla para dirigir
    /// la búsqueda). Reflejar _termino directamente en el value del mismo
    /// input que lo genera es lo que permite que, bajo latencia, un render
    /// en cola de una pulsación anterior se aplique después de una más
    /// reciente y sobrescriba visualmente el campo con una versión más
    /// corta — el navegador ya tiene el valor correcto en su DOM, no hace
    /// falta devolvérselo en cada tecla. Solo se toca al abrir/cerrar el
    /// buscador (reinicio externo legítimo), igual que CampoTexto.
    /// </summary>
    private string _valorMostrado = string.Empty;

    private bool _buscando;
    private ResultadoBusquedaGlobalDto? _resultado;
    private int _indiceSeleccionado = -1;

    /// <summary>
    /// Mensaje de fallo de la búsqueda vigente. Existe porque un fallo no
    /// puede presentarse como un vacío: sin él, una excepción del handler
    /// dejaba <see cref="_resultado"/> en null con <see cref="_buscando"/>
    /// ya apagado, y el panel no pintaba absolutamente nada — el usuario
    /// leía "no hay nada" donde en realidad la búsqueda ni siquiera se
    /// completó.
    /// </summary>
    private string? _errorBusqueda;

    /// <summary>
    /// CancellationTokenSource del ciclo de vida del componente: su token
    /// viaja en TODAS las llamadas al mediador (búsqueda y recientes), y se
    /// cancela y libera una sola vez en <see cref="DisposeAsync"/>, que es el
    /// que Blazor llama de verdad aquí (el componente declara
    /// <c>IAsyncDisposable</c>, no <c>IDisposable</c>).
    ///
    /// Se pone a null ANTES de liberarlo para que cualquier lectura
    /// posterior vea null en vez de un CTS desechado: leer <c>.Token</c> de
    /// un CTS ya dispuesto lanza <see cref="ObjectDisposedException"/>, y en
    /// un continuation posterior a un await no habría nadie para recogerla.
    /// Por eso el token se captura siempre ANTES del primer await de cada
    /// operación, nunca después.
    /// </summary>
    private CancellationTokenSource? _cicloDeVida = new();

    /// <summary>CTS del debounce de la búsqueda en vuelo, enlazado al del ciclo de vida.</summary>
    private CancellationTokenSource? _debounceCts;

    /// <summary>
    /// Número de la búsqueda vigente. Se incrementa antes de cualquier await
    /// y se vuelve a comprobar después: una respuesta lenta de una búsqueda
    /// ya superada no puede pintarse sobre la vigente ni apagar su indicador
    /// de "Buscando…", aunque el handler haya ignorado el token de
    /// cancelación (cancelar es una petición, no una garantía).
    /// </summary>
    private int _generacionBusqueda;

    /// <summary>
    /// Número de la apertura vigente del palette. Cierra la misma carrera
    /// para la carga de "Recientes": si el usuario cierra y vuelve a abrir,
    /// los recientes que llegan tarde pertenecen a un contexto que ya no
    /// existe y no se pintan.
    /// </summary>
    private int _generacionApertura;

    /// <summary>
    /// Guarda de reentrada de <see cref="Seleccionar"/>. Hace falta aunque el
    /// palette se cierre en el mismo método: el segundo clic (o un Enter
    /// mientras el clic ya viajaba hacia el servidor) sale del navegador
    /// antes de que el cierre llegue al DOM, así que "ya no está visible" no
    /// impide una segunda ejecución. Solo se reabre en
    /// <see cref="AbrirAsync"/>, que es el cambio de contexto.
    /// </summary>
    private bool _navegando;

    /// <summary>"Recientes" del estado inicial — cargado una sola vez al abrir el palette, no en cada tecla.</summary>
    private IReadOnlyList<ItemRecienteDto> _recientes = [];

    /// <summary>"En esta pantalla" del estado inicial — resuelto una sola vez al abrir, a partir de la ruta actual.</summary>
    private IReadOnlyList<ItemBusquedaDto> _accionesPantalla = [];

    /// <summary>true mientras la query está vacía (&lt;2 caracteres) — el estado que muestra Recientes + En esta pantalla en vez de resultados de búsqueda.</summary>
    private bool ConsultaVacia => _termino.Trim().Length < 2;

    /// <summary>
    /// Marcador para "Guardar filtro actual" en <see cref="AccionesContextualesPorPantalla"/> — a
    /// diferencia de "Nuevo X"/"Exportar a Excel" (rutas fijas), esta acción
    /// tiene que preservar los filtros que ya haya en la URL actual (?q=,
    /// ?estado=...), así que su ruta real se construye en <see cref="ConstruirAccionesPantalla"/>
    /// con <c>NavigationManager.GetUriWithQueryParameter</c> en vez de ser un literal aquí.
    /// </summary>
    private const string MarcadorGuardarFiltro = "__guardar-filtro__";

    /// <summary>
    /// Acciones contextuales por pantalla del grupo "En esta pantalla" —
    /// mismo estilo literal que <see cref="CoberturaDePaleta.DestinosNavegacion"/>/<see cref="AccionesFijas"/>.
    /// Solo cubre verbos que YA existen de verdad en cada pantalla (ver plan
    /// de implementación): no se inventa "Exportar la vista" ni "Guardar
    /// filtro" donde el propio módulo no lo tiene todavía.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<(string Titulo, string Ruta)>> AccionesContextualesPorPantalla =
        new Dictionary<string, IReadOnlyList<(string, string)>>
        {
            ["clientes"] = [("Nuevo cliente empresarial", "/clientes?accion=crear"), ("Exportar a Excel", "/clientes/exportar.xlsx"), ("Guardar filtro actual", MarcadorGuardarFiltro)],
            ["empresas"] = [("Nueva empresa", "/empresas?accion=crear"), ("Exportar a Excel", "/empresas/exportar.xlsx")],
            ["subcontratas"] = [("Nueva subcontrata", "/subcontratas?accion=crear"), ("Exportar a Excel", "/subcontratas/exportar.xlsx")],
            ["centros"] = [("Nuevo centro", "/centros?accion=crear"), ("Exportar a Excel", "/centros/exportar.xlsx")],
            ["trabajadores"] = [("Nuevo trabajador", "/trabajadores?accion=crear"), ("Exportar a Excel", "/trabajadores/exportar.xlsx"), ("Guardar filtro actual", MarcadorGuardarFiltro)],
            ["documentos"] = [("Nuevo documento", "/documentos?accion=crear"), ("Exportar a Excel", "/documentos/exportar.xlsx"), ("Guardar filtro actual", MarcadorGuardarFiltro)],
        };

    /// <summary>Grupo "Acciones" del palette — verbos que crean algo, nunca navegación pura.</summary>
    private static readonly IReadOnlyList<(string Nombre, string Ruta)> AccionesFijas =
    [
        ("Crear cliente empresarial", "/clientes?accion=crear"),
        ("Alta guiada de cliente empresarial", "/clientes/alta-guiada"),
        ("Crear documento", "/documentos?accion=crear"),
    ];

    private IReadOnlyList<ItemBusquedaDto> IrA
    {
        get
        {
            var termino = _termino.Trim();
            if (termino.Length < 2) return [];

            return CoberturaDePaleta.DestinosNavegacion
                .Where(c => c.Nombre.Contains(termino, StringComparison.OrdinalIgnoreCase))
                .Select(c => new ItemBusquedaDto(Guid.Empty, c.Nombre, null, c.Ruta))
                .ToList();
        }
    }

    /// <summary>
    /// Acciones fijas (filtradas por coincidencia de nombre) más, cuando la
    /// búsqueda de una categoría no encontró nada, un atajo para crearla con
    /// el término ya escrito precargado como nombre — pedido explícito: "si
    /// introduce una empresa/centro/trabajador que no se encuentre, dar la
    /// opción de crearlo con el nombre ya prellenado".
    /// </summary>
    private IReadOnlyList<ItemBusquedaDto> Acciones
    {
        get
        {
            var termino = _termino.Trim();
            if (termino.Length < 2) return [];

            var acciones = AccionesFijas
                .Where(c => c.Nombre.Contains(termino, StringComparison.OrdinalIgnoreCase))
                .Select(c => new ItemBusquedaDto(Guid.Empty, c.Nombre, null, c.Ruta))
                .ToList();

            if (_resultado is not null)
            {
                var terminoCodificado = Uri.EscapeDataString(termino);

                if (_resultado.Empresas.Count == 0)
                    acciones.Add(new ItemBusquedaDto(Guid.Empty, $"Crear empresa «{termino}»", null, $"/empresas?accion=crear&nombre={terminoCodificado}"));
                if (_resultado.Centros.Count == 0)
                    acciones.Add(new ItemBusquedaDto(Guid.Empty, $"Crear centro «{termino}»", null, $"/centros?accion=crear&nombre={terminoCodificado}"));
                if (_resultado.Trabajadores.Count == 0)
                    acciones.Add(new ItemBusquedaDto(Guid.Empty, $"Crear trabajador «{termino}»", null, $"/trabajadores?accion=crear&nombre={terminoCodificado}"));
            }

            return acciones;
        }
    }

    /// <summary>
    /// Una fila del palette, ya resuelta para pintar: el DTO, el tipo con el
    /// que se registra su uso (null = "Ir a", navegación pura que nunca
    /// cuenta como uso), el icono, el subtítulo mostrado y la pista de Enter
    /// del mockup Gen 2 ("↵ abrir ficha" / "↵ ir" / "↵ ejecutar").
    /// </summary>
    private sealed record ItemPaleta(ItemBusquedaDto Item, string? TipoHistorial, string Icono, string Subtitulo, string PistaEnter);

    /// <summary>Una sección del palette, con el título en versalitas del mockup.</summary>
    private sealed record GrupoPaleta(string Titulo, IReadOnlyList<ItemPaleta> Items);

    /// <summary>
    /// Etiqueta de tipo de cada categoría de entidad, en terminología
    /// canónica. El subtítulo que devuelve BuscarGlobalQueryHandler para
    /// Cliente/Empresa/Subcontrata/Centro es el literal del tipo, así que
    /// mostrarlo tal cual repetiría la etiqueta y, en el caso de "Cliente",
    /// incumpliría además el contrato de lenguaje: la contraparte de una
    /// Relación Empresarial es el Cliente empresarial, nunca "cliente" a
    /// secas. Para Trabajador (DNI) y Documento (propietario) el subtítulo
    /// del DTO sí aporta información distinta y se concatena.
    /// </summary>
    private static readonly HashSet<string> EtiquetasDeTipoDelHandler =
        new(StringComparer.OrdinalIgnoreCase) { "Cliente", "Empresa", "Subcontrata", "Centro" };

    private static string ComponerSubtitulo(string etiquetaTipo, string? subtituloDto) =>
        string.IsNullOrWhiteSpace(subtituloDto) || EtiquetasDeTipoDelHandler.Contains(subtituloDto.Trim())
            ? etiquetaTipo
            : $"{etiquetaTipo} · {subtituloDto}";

    /// <summary>
    /// Los grupos tal y como se pintan, en orden. Es la ÚNICA fuente del
    /// orden: <see cref="ElementosPlanos"/> y <see cref="InicioDeGrupo"/> se
    /// derivan de aquí, así que el índice que resalta el render y el que
    /// mueve ↑↓/Tab/Enter no pueden desincronizarse — antes eran tres listas
    /// escritas a mano en paralelo y cualquier categoría añadida a una y
    /// olvidada en otra habría hecho que Enter abriera un elemento distinto
    /// del resaltado.
    /// </summary>
    private IReadOnlyList<GrupoPaleta> GruposVisibles
    {
        get
        {
            var grupos = new List<GrupoPaleta>();

            if (ConsultaVacia)
            {
                if (_recientes.Count > 0)
                {
                    grupos.Add(new GrupoPaleta("Recientes", [.. _recientes.Select(r => new ItemPaleta(
                        RecienteComoItem(r),
                        r.Tipo,
                        IconoParaTipo(r.Tipo),
                        SubtituloDeReciente(r),
                        r.Tipo == "Accion" ? "ejecutar" : "abrir ficha"))]));
                }

                if (_accionesPantalla.Count > 0)
                    grupos.Add(new GrupoPaleta("En esta pantalla", [.. _accionesPantalla.Select(ComoAccion)]));

                return grupos;
            }

            if (_resultado is not null && _resultado.TieneResultados)
            {
                List<ItemPaleta> entidades =
                [
                    .. ComoEntidades(_resultado.Clientes, "Cliente", "Cliente empresarial", "clientes"),
                    .. ComoEntidades(_resultado.Empresas, "Empresa", "Empresa", "empresas"),
                    .. ComoEntidades(_resultado.Subcontratas, "Subcontrata", "Subcontrata", "subcontratas"),
                    .. ComoEntidades(_resultado.Centros, "Centro", "Centro", "centros"),
                    .. ComoEntidades(_resultado.Trabajadores, "Trabajador", "Trabajador", "trabajadores"),
                    .. ComoEntidades(_resultado.Documentos, "Documento", "Documento", "documentos"),
                ];

                grupos.Add(new GrupoPaleta("Entidades", entidades));
            }

            if (IrA.Count > 0)
            {
                grupos.Add(new GrupoPaleta("Ir a", [.. IrA.Select(i =>
                    new ItemPaleta(i, null, "configuracion", string.Empty, "ir"))]));
            }

            if (Acciones.Count > 0)
                grupos.Add(new GrupoPaleta("Acciones", [.. Acciones.Select(ComoAccion)]));

            return grupos;
        }
    }

    private static ItemPaleta ComoAccion(ItemBusquedaDto item) =>
        new(item, "Accion", "editar", string.Empty, "ejecutar");

    private static IEnumerable<ItemPaleta> ComoEntidades(
        IReadOnlyList<ItemBusquedaDto> items, string tipoHistorial, string etiquetaTipo, string icono) =>
        items.Select(i => new ItemPaleta(i, tipoHistorial, icono, ComponerSubtitulo(etiquetaTipo, i.Subtitulo), "abrir ficha"));

    /// <summary>Todas las filas en el orden en que se pintan, para navegar con ↑↓/Tab + Enter.</summary>
    private IReadOnlyList<ItemPaleta> ElementosPlanos => [.. GruposVisibles.SelectMany(g => g.Items)];

    /// <summary>Índice (en <see cref="ElementosPlanos"/>) de la primera fila de cada grupo, en orden — usado por Tab para saltar de grupo en vez de fila a fila.</summary>
    private IReadOnlyList<int> InicioDeGrupo
    {
        get
        {
            var inicios = new List<int>();
            var acumulado = 0;

            foreach (var grupo in GruposVisibles)
            {
                if (grupo.Items.Count > 0) inicios.Add(acumulado);
                acumulado += grupo.Items.Count;
            }

            return inicios;
        }
    }

    /// <summary>
    /// Un reciente guardado lleva el subtítulo tal y como lo emitió el
    /// handler, y para la contraparte de una Relación Empresarial ese literal
    /// es «Cliente» a secas. Pasa por la misma neutralización que los
    /// resultados directos: si no, abrir un Cliente empresarial y reabrir el
    /// palette lo devolvía escrito mal en «Recientes». Vale también para lo ya
    /// guardado, que no se reescribe.
    /// </summary>
    private static string SubtituloDeReciente(ItemRecienteDto r) =>
        EtiquetaVisibleDeTipo(r.Tipo) is { } etiqueta
            ? ComponerSubtitulo(etiqueta, r.Subtitulo)
            : r.Subtitulo ?? string.Empty;

    /// <summary>Etiqueta canónica de cada tipo de entidad; null para los que no son entidad (una acción no lleva etiqueta de tipo).</summary>
    private static string? EtiquetaVisibleDeTipo(string tipo) => tipo switch
    {
        "Cliente" => "Cliente empresarial",
        "Empresa" => "Empresa",
        "Subcontrata" => "Subcontrata",
        "Centro" => "Centro",
        "Trabajador" => "Trabajador",
        "Documento" => "Documento",
        _ => null
    };

    private static ItemBusquedaDto RecienteComoItem(ItemRecienteDto r) =>
        new(r.EntidadId ?? Guid.Empty, r.Titulo, r.Subtitulo, r.UrlDestino);

    /// <summary>Icono por tipo de "reciente" — mismo Nombre que ya usan las categorías de Entidades; "Accion" reutiliza el icono del grupo Acciones. Cualquier tipo sin icono propio cae en "resultado" (registro genérico), nunca en "editar", que significa acción.</summary>
    private static string IconoParaTipo(string tipo) => tipo switch
    {
        "Cliente" => "clientes",
        "Empresa" => "empresas",
        "Subcontrata" => "subcontratas",
        "Centro" => "centros",
        "Trabajador" => "trabajadores",
        "Documento" => "documentos",
        "Accion" => "editar",
        _ => "resultado"
    };

    /// <summary>
    /// "En esta pantalla" del estado inicial — resuelve la ruta actual contra
    /// <see cref="AccionesContextualesPorPantalla"/>. "Guardar filtro actual"
    /// se construye añadiendo el parámetro a la URL ACTUAL completa (con los
    /// filtros que ya tenga), nunca sustituyéndola por una plantilla fija:
    /// si se perdieran los demás parámetros (?q=, ?estado=...) al navegar,
    /// la página resincronizaría sus filtros desde una URL vacía antes de
    /// abrir el modal, y "guardar filtro actual" acabaría guardando un
    /// filtro vacío.
    /// </summary>
    private IReadOnlyList<ItemBusquedaDto> ConstruirAccionesPantalla()
    {
        var segmento = SegmentoDeRuta(Navigation.Uri);
        if (segmento is null || !AccionesContextualesPorPantalla.TryGetValue(segmento, out var acciones))
            return [];

        return acciones
            .Select(a => new ItemBusquedaDto(
                Guid.Empty,
                a.Titulo,
                null,
                a.Ruta == MarcadorGuardarFiltro
                    ? Navigation.GetUriWithQueryParameter("accion", "guardar-filtro")
                    : a.Ruta))
            .ToList();
    }

    /// <summary>
    /// Segmento de ruta, pero SOLO si es la pantalla de listado exacta
    /// (<c>/trabajadores</c>), nunca una sub-ruta (<c>/trabajadores/{id}</c>
    /// — Trabajador 360, Centro 360). "Guardar filtro actual" navegaría a
    /// esa misma sub-ruta con <c>?accion=guardar-filtro</c> añadido, y esa
    /// página de ficha no tiene ningún código que reaccione a ese parámetro
    /// (vive en la página de listado) — mostrarlo ahí sería una acción que
    /// no hace nada. Sin match exacto, "En esta pantalla" no muestra nada,
    /// que es el comportamiento correcto para una pantalla sin acciones
    /// contextuales definidas.
    /// </summary>
    private static string? SegmentoDeRuta(string uri)
    {
        var segmentos = new Uri(uri).AbsolutePath.Trim('/').Split('/');
        return segmentos.Length == 1 && !string.IsNullOrEmpty(segmentos[0]) ? segmentos[0] : null;
    }

    protected override void OnInitialized()
    {
        BusquedaGlobalService.SolicitudAbrir += AbrirDesdeServicio;

        // La navegación mejorada de Blazor reutiliza esta instancia entre
        // páginas (no se recrea el Layout en cada navegación) — sin esto, al
        // hacer clic en un resultado la superposición se quedaba abierta
        // tapando la página de destino.
        Navigation.LocationChanged += ManejarCambioDeUbicacion;
    }

    private void ManejarCambioDeUbicacion(object? sender, LocationChangedEventArgs e)
    {
        if (!_visible) return;

        // LocationChanged no es un evento de Blazor (no dispara StateHasChanged solo) —
        // hay que marshalear al dispatcher del circuito explícitamente.
        _ = InvokeAsync(() =>
        {
            Cerrar();
            StateHasChanged();
        });
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        _modulo = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/buscador-global.js");
        _referenciaDotNet = DotNetObjectReference.Create(this);
        _suscripcionAtajo = await _modulo.InvokeAsync<IJSObjectReference>("registrarAtajoBuscador", _referenciaDotNet);
    }

    private void AbrirDesdeServicio() => _ = AbrirAsync();

    [JSInvokable]
    public Task AbrirDesdeJs() => AbrirAsync();

    private async Task AbrirAsync()
    {
        // Nueva apertura = nuevo contexto: nada de lo que quedó preparado
        // para la anterior puede arrastrarse. _recientes se vacía aquí en vez
        // de dejarse hasta que llegue la carga nueva, porque si no el palette
        // reabierto pintaba durante ese hueco los recientes del contexto
        // anterior y una pulsación de Enter los habría abierto.
        var apertura = ++_generacionApertura;
        ++_generacionBusqueda;

        _visible = true;
        _termino = string.Empty;
        _valorMostrado = string.Empty;
        _resultado = null;
        _errorBusqueda = null;
        _buscando = false;
        _indiceSeleccionado = -1;
        _navegando = false;
        _recientes = [];

        // Una sola vez por apertura, no en cada tecla (a diferencia de la
        // búsqueda, que sí se repite con el debounce).
        _accionesPantalla = ConstruirAccionesPantalla();

        StateHasChanged();

        // El token se captura ANTES de cualquier await de este método.
        var token = TokenDeCicloDeVida();
        if (token is null) return;

        // El módulo se captura en una local: entre esta comprobación y su
        // uso hay un await, y DisposeAsync puede haberlo liberado y puesto a
        // null en ese hueco. Tras reanudar se vuelve a mirar el token, que es
        // la señal de que el componente se retiró.
        var modulo = _modulo;
        if (modulo is not null)
        {
            // Espera al siguiente render para que el <input> ya esté en el DOM antes de enfocarlo.
            await Task.Yield();

            if (token.Value.IsCancellationRequested) return;

            await modulo.InvokeVoidAsync("enfocarElemento", _inputElemento);

            // Se re-registra en cada apertura: el <input> es un elemento del
            // DOM nuevo cada vez (vive dentro del @if (_visible)), así que el
            // listener de la apertura anterior ya se perdió con él.
            var suscripcionAnterior = _suscripcionTab;
            _suscripcionTab = null;
            if (suscripcionAnterior is not null)
                await suscripcionAnterior.DisposeAsync();

            if (token.Value.IsCancellationRequested) return;

            _suscripcionTab = await modulo.InvokeAsync<IJSObjectReference>("registrarSaltoDeGrupo", _inputElemento, _referenciaDotNet);
        }

        try
        {
            var recientes = await Mediator.Send(new ObtenerRecientesQuery(), token.Value);

            // Los recientes de una apertura ya superada pertenecen a un
            // contexto que ya no existe: llegan tarde y no se pintan.
            if (apertura != _generacionApertura) return;

            _recientes = recientes;
            StateHasChanged();
        }
        catch (OperationCanceledException)
        {
            // El componente se destruyó o el palette se cerró mientras cargaba.
        }
        catch
        {
            // "Recientes" es un extra del estado inicial, no el servicio del
            // palette: si falla, el palette sigue siendo plenamente usable
            // con "En esta pantalla" y con la búsqueda. Mismo criterio
            // best-effort que RegistrarUsoRecienteSilenciosamenteAsync.
        }
    }

    /// <summary>
    /// Token del ciclo de vida, o null si el componente ya se está
    /// destruyendo. Se llama SIEMPRE antes del primer await de la operación:
    /// leerlo después permitiría que un <see cref="DisposeAsync"/> intermedio
    /// hubiera desechado el CTS, y <c>.Token</c> sobre un CTS dispuesto lanza
    /// <see cref="ObjectDisposedException"/> dentro de un continuation donde
    /// nadie la recoge.
    /// </summary>
    private CancellationToken? TokenDeCicloDeVida()
    {
        var ciclo = _cicloDeVida;
        if (ciclo is null) return null;

        try
        {
            return ciclo.Token;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Tab/Shift+Tab desde el input — mueve la selección al primer elemento del grupo siguiente/anterior (Parte XVI PROMPT 05, "Tab grupo").</summary>
    [JSInvokable]
    public void SaltarGrupoDesdeJs(bool retroceder)
    {
        var inicios = InicioDeGrupo;
        if (inicios.Count == 0) return;

        if (retroceder)
        {
            var anterior = inicios.LastOrDefault(i => i < _indiceSeleccionado, inicios[^1]);
            _indiceSeleccionado = anterior;
        }
        else
        {
            var siguiente = inicios.FirstOrDefault(i => i > _indiceSeleccionado, inicios[0]);
            _indiceSeleccionado = siguiente;
        }

        StateHasChanged();
    }

    private void Cerrar()
    {
        _visible = false;

        // Cerrar es cambio de contexto: invalida lo que esté en vuelo para
        // que no se pinte sobre la próxima apertura, y borra la selección
        // para que no quede nada preparado para una entidad que ya no se ve.
        ++_generacionBusqueda;
        ++_generacionApertura;
        _indiceSeleccionado = -1;
        _resultado = null;
        _errorBusqueda = null;
        _buscando = false;

        _debounceCts?.Cancel();
    }

    private void ManejarTeclaAsync(KeyboardEventArgs e)
    {
        var elementos = ElementosPlanos;

        switch (e.Key)
        {
            case "Escape":
                Cerrar();
                break;

            case "ArrowDown" when elementos.Count > 0:
                _indiceSeleccionado = Math.Min(_indiceSeleccionado + 1, elementos.Count - 1);
                break;

            case "ArrowUp" when elementos.Count > 0:
                _indiceSeleccionado = _indiceSeleccionado <= 0 ? 0 : _indiceSeleccionado - 1;
                break;

            case "Enter" when _indiceSeleccionado >= 0 && _indiceSeleccionado < elementos.Count:
                Seleccionar(elementos[_indiceSeleccionado].Item, elementos[_indiceSeleccionado].TipoHistorial);
                break;
        }
    }

    /// <summary>
    /// Punto único de navegación del palette — usado tanto por Enter
    /// (teclado) como por el clic de cada <c>&lt;a&gt;</c> del .razor
    /// (interceptado con <c>@onclick:preventDefault</c> en vez de dejar la
    /// navegación nativa del enlace). Hacía falta centralizarlo: un enlace
    /// nativo a <c>?accion=crear</c>/<c>?accion=guardar-filtro</c> disparado
    /// desde la MISMA página que ya está montada (el caso normal de "En esta
    /// pantalla", que solo aparece estando ya en esa pantalla) usa la
    /// navegación mejorada de Blazor y reutiliza la instancia del
    /// componente — <c>OnInitializedAsync</c> nunca vuelve a ejecutarse y el
    /// query string se pierde en silencio (comprobado manualmente: "Nueva
    /// subcontrata" desde /subcontratas no abría el modal). <c>forceLoad</c>
    /// para estas rutas fuerza una recarga real del navegador, que sí
    /// remonta el componente desde cero.
    /// </summary>
    private void Seleccionar(ItemBusquedaDto item, string? tipo)
    {
        if (_navegando) return;
        _navegando = true;

        RegistrarUsoReciente(tipo, item);
        Cerrar();
        Navigation.NavigateTo(item.UrlDestino, forceLoad: RequiereNavegacionCompleta(item.UrlDestino));
    }

    /// <summary>
    /// Descargas (Exportar a Excel) y disparadores <c>?accion=...</c> que una
    /// página solo procesa en su montaje inicial (crear/guardar-filtro,
    /// ver <see cref="Seleccionar"/>) necesitan una recarga real del
    /// navegador, no la navegación SPA de Blazor.
    /// </summary>
    private static bool RequiereNavegacionCompleta(string urlDestino) =>
        urlDestino.Contains("/exportar.xlsx", StringComparison.Ordinal) ||
        urlDestino.Contains("accion=crear", StringComparison.Ordinal) ||
        urlDestino.Contains("accion=guardar-filtro", StringComparison.Ordinal);

    /// <summary>
    /// Registro de uso reciente, best-effort. <paramref name="tipo"/> null
    /// significa "Ir a": navegación pura, nunca cuenta como "uso".
    /// </summary>
    private void RegistrarUsoReciente(string? tipo, ItemBusquedaDto item)
    {
        if (tipo is null) return;

        // Fire-and-forget deliberado: el registro de recientes es
        // best-effort y nunca puede bloquear ni alterar la navegación del
        // usuario. Se lanza sin esperar y se navega/cierra de inmediato;
        // cualquier fallo se descarta en silencio. Lo que NO es opcional es
        // que viaje el token del ciclo: sin él, esta era la única llamada al
        // mediador que seguía trabajando para un componente ya retirado, en
        // contra de lo que promete el comentario de la clase.
        _ = RegistrarUsoRecienteSilenciosamenteAsync(tipo, item);
    }

    private async Task RegistrarUsoRecienteSilenciosamenteAsync(string tipo, ItemBusquedaDto item)
    {
        // El token se lee ANTES del await, como en el resto del componente.
        var token = TokenDeCicloDeVida();
        if (token is null) return;

        try
        {
            await Mediator.Send(new RegistrarUsoRecienteCommand(
                tipo, item.Id == Guid.Empty ? null : item.Id, item.Titulo, item.Subtitulo, item.UrlDestino), token.Value);
        }
        catch
        {
            // Best-effort: un fallo aquí nunca debe afectar al usuario.
        }
    }

    private async Task ManejarEntradaAsync(ChangeEventArgs e)
    {
        _termino = e.Value?.ToString() ?? string.Empty;
        _indiceSeleccionado = -1;
        _errorBusqueda = null;

        // El número de la búsqueda y el término se capturan ANTES de
        // cualquier await. El término, además, se envía desde esta copia y no
        // desde el campo: al despertar del debounce, _termino ya puede ser el
        // de una pulsación posterior, y la query saldría con un texto que no
        // es el que disparó esta búsqueda.
        var generacion = ++_generacionBusqueda;
        var termino = _termino;

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;

        if (termino.Trim().Length < 2)
        {
            _resultado = null;
            _buscando = false;
            return;
        }

        // Igual que el token: se lee antes del primer await, nunca después.
        var tokenCiclo = TokenDeCicloDeVida();
        if (tokenCiclo is null) return;

        var enlazado = CancellationTokenSource.CreateLinkedTokenSource(tokenCiclo.Value);
        _debounceCts = enlazado;
        var token = enlazado.Token;

        try
        {
            _buscando = true;
            await Task.Delay(RetardoDebounce, token);

            var resultado = await Mediator.Send(new BuscarGlobalQuery(termino), token);

            // Comprobación después del await, y deliberadamente NO basada en
            // el token: cancelar es una petición, no una garantía — un
            // handler que ignore el CancellationToken devolvería igualmente
            // el resultado de una búsqueda ya superada, y sin esta guarda se
            // pintaría encima de la vigente.
            if (generacion != _generacionBusqueda) return;

            _resultado = resultado;
        }
        catch (OperationCanceledException)
        {
            // Se canceló porque el usuario siguió escribiendo, cerró el
            // palette o el componente se está destruyendo — ignorar.
        }
        catch (Exception) when (generacion == _generacionBusqueda)
        {
            // Un fallo no puede acabar mostrándose como "sin resultados".
            _resultado = null;
            _errorBusqueda = "No se pudo completar la búsqueda. Vuelve a intentarlo.";
        }
        catch
        {
            // Fallo de una búsqueda ya superada: irrelevante para lo que se
            // ve, pero no puede escapar y tumbar el circuito.
        }
        finally
        {
            // La bandera de "operación en curso" solo se apaga si la
            // operación que la encendió sigue siendo la vigente: si no, el
            // final de una búsqueda superada apagaría el "Buscando…" de la
            // que está de verdad en vuelo.
            if (generacion == _generacionBusqueda)
            {
                _buscando = false;
                StateHasChanged();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        BusquedaGlobalService.SolicitudAbrir -= AbrirDesdeServicio;
        Navigation.LocationChanged -= ManejarCambioDeUbicacion;

        // Se pone a null ANTES de cancelarlo y liberarlo: cualquier lectura
        // posterior (TokenDeCicloDeVida) verá null en vez de tocar un CTS
        // desechado.
        var ciclo = _cicloDeVida;
        _cicloDeVida = null;
        ciclo?.Cancel();

        // También se anula antes de tocarlo: Blazor puede llamar a este
        // Dispose más de una vez, y Cancel() sobre un CTS ya liberado lanza
        // ObjectDisposedException.
        var debounce = _debounceCts;
        _debounceCts = null;
        debounce?.Cancel();

        // H5 (docs/ux-audit/16-transversales.md): mismo motivo que
        // AtajosListaTeclado.razor — el circuito puede desconectarse antes
        // de que corra este Dispose.
        // Cada referencia se anula ANTES de liberarla, igual que el CTS de
        // arriba: sin esto, una segunda destrucción volvía a invocar «dispose»
        // sobre las mismas referencias ya liberadas — justo lo que el
        // comentario de arriba promete que no ocurre.
        var suscripcionAtajo = _suscripcionAtajo;
        var suscripcionTab = _suscripcionTab;
        var modulo = _modulo;
        var referencia = _referenciaDotNet;
        _suscripcionAtajo = null;
        _suscripcionTab = null;
        _modulo = null;
        _referenciaDotNet = null;

        try
        {
            if (suscripcionAtajo is not null)
            {
                await suscripcionAtajo.InvokeVoidAsync("dispose");
                await suscripcionAtajo.DisposeAsync();
            }

            if (suscripcionTab is not null)
            {
                await suscripcionTab.InvokeVoidAsync("dispose");
                await suscripcionTab.DisposeAsync();
            }

            if (modulo is not null)
                await modulo.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }

        referencia?.Dispose();
        debounce?.Dispose();
        ciclo?.Dispose();
    }
}
