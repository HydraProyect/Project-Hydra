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
    private CancellationTokenSource? _debounceCts;
    private int _indiceSeleccionado = -1;

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
            ["clientes"] = [("Nuevo cliente", "/clientes?accion=crear"), ("Exportar a Excel", "/clientes/exportar.xlsx"), ("Guardar filtro actual", MarcadorGuardarFiltro)],
            ["empresas"] = [("Nueva empresa", "/empresas?accion=crear"), ("Exportar a Excel", "/empresas/exportar.xlsx")],
            ["subcontratas"] = [("Nueva subcontrata", "/subcontratas?accion=crear"), ("Exportar a Excel", "/subcontratas/exportar.xlsx")],
            ["centros"] = [("Nuevo centro", "/centros?accion=crear"), ("Exportar a Excel", "/centros/exportar.xlsx")],
            ["trabajadores"] = [("Nuevo trabajador", "/trabajadores?accion=crear"), ("Exportar a Excel", "/trabajadores/exportar.xlsx"), ("Guardar filtro actual", MarcadorGuardarFiltro)],
            ["documentos"] = [("Nuevo documento", "/documentos?accion=crear"), ("Exportar a Excel", "/documentos/exportar.xlsx"), ("Guardar filtro actual", MarcadorGuardarFiltro)],
        };

    /// <summary>Grupo "Acciones" del palette — verbos que crean algo, nunca navegación pura.</summary>
    private static readonly IReadOnlyList<(string Nombre, string Ruta)> AccionesFijas =
    [
        ("Crear cliente", "/clientes?accion=crear"),
        ("Alta guiada de cliente", "/clientes/alta-guiada"),
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
    /// Todos los grupos en el mismo orden en que se renderizan — Recientes →
    /// En esta pantalla con la query vacía; Entidades → Ir a → Acciones con
    /// texto escrito (Parte XVI PROMPT 05) — para navegar con ↑↓/Tab + Enter.
    /// </summary>
    private IReadOnlyList<ItemBusquedaDto> ElementosPlanos => ConsultaVacia
        ? [.. _recientes.Select(RecienteComoItem), .. _accionesPantalla]
        : _resultado is null
            ? [.. IrA, .. Acciones]
            : [.. _resultado.Clientes, .. _resultado.Empresas, .. _resultado.Subcontratas, .. _resultado.Centros,
               .. _resultado.Trabajadores, .. _resultado.Documentos, .. IrA, .. Acciones];

    /// <summary>
    /// Tipo de historial (para RegistrarUsoRecienteCommand) de cada elemento
    /// de <see cref="ElementosPlanos"/>, en el mismo orden — null donde no se
    /// registra uso ("Ir a": navegación pura, no cuenta como "uso" del
    /// palette). Se recorre en paralelo a ElementosPlanos en vez de fundir el
    /// tipo dentro de ItemBusquedaDto porque ese DTO es de Application y lo
    /// consume también BuscarGlobalQuery sin noción de "tipo para historial".
    /// </summary>
    private IReadOnlyList<string?> TiposPlanos => ConsultaVacia
        ? [.. _recientes.Select(r => (string?)r.Tipo), .. Enumerable.Repeat((string?)"Accion", _accionesPantalla.Count)]
        : _resultado is null
            ? [.. Enumerable.Repeat((string?)null, IrA.Count), .. Enumerable.Repeat((string?)"Accion", Acciones.Count)]
            : [.. Enumerable.Repeat((string?)"Cliente", _resultado.Clientes.Count), .. Enumerable.Repeat((string?)"Empresa", _resultado.Empresas.Count),
               .. Enumerable.Repeat((string?)"Subcontrata", _resultado.Subcontratas.Count), .. Enumerable.Repeat((string?)"Centro", _resultado.Centros.Count),
               .. Enumerable.Repeat((string?)"Trabajador", _resultado.Trabajadores.Count), .. Enumerable.Repeat((string?)"Documento", _resultado.Documentos.Count),
               .. Enumerable.Repeat((string?)null, IrA.Count), .. Enumerable.Repeat((string?)"Accion", Acciones.Count)];

    /// <summary>Índice (en ElementosPlanos) del primer elemento de cada grupo no vacío, en orden — usado por Tab para saltar de grupo en vez de elemento a elemento.</summary>
    private IReadOnlyList<int> InicioDeGrupo
    {
        get
        {
            var grupos = ConsultaVacia
                ? new IReadOnlyList<ItemBusquedaDto>[] { [.. _recientes.Select(RecienteComoItem)], _accionesPantalla }
                : _resultado is null
                    ? new[] { IrA, Acciones }
                    : new[] { _resultado.Clientes, _resultado.Empresas, _resultado.Subcontratas, _resultado.Centros, _resultado.Trabajadores, _resultado.Documentos, IrA, Acciones };

            var inicios = new List<int>();
            var acumulado = 0;
            foreach (var grupo in grupos)
            {
                if (grupo.Count > 0) inicios.Add(acumulado);
                acumulado += grupo.Count;
            }

            return inicios;
        }
    }

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
        _visible = true;
        _termino = string.Empty;
        _valorMostrado = string.Empty;
        _resultado = null;
        _indiceSeleccionado = -1;

        // Una sola vez por apertura, no en cada tecla (a diferencia de la
        // búsqueda, que sí se repite con el debounce).
        _accionesPantalla = ConstruirAccionesPantalla();
        _recientes = await Mediator.Send(new ObtenerRecientesQuery());

        StateHasChanged();

        if (_modulo is not null)
        {
            // Espera al siguiente render para que el <input> ya esté en el DOM antes de enfocarlo.
            await Task.Yield();
            await _modulo.InvokeVoidAsync("enfocarElemento", _inputElemento);

            // Se re-registra en cada apertura: el <input> es un elemento del
            // DOM nuevo cada vez (vive dentro del @if (_visible)), así que el
            // listener de la apertura anterior ya se perdió con él.
            if (_suscripcionTab is not null)
                await _suscripcionTab.DisposeAsync();
            _suscripcionTab = await _modulo.InvokeAsync<IJSObjectReference>("registrarSaltoDeGrupo", _inputElemento, _referenciaDotNet);
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
        _debounceCts?.Cancel();
    }

    private void ManejarTeclaAsync(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "Escape":
                Cerrar();
                break;

            case "ArrowDown" when ElementosPlanos.Count > 0:
                _indiceSeleccionado = Math.Min(_indiceSeleccionado + 1, ElementosPlanos.Count - 1);
                break;

            case "ArrowUp" when ElementosPlanos.Count > 0:
                _indiceSeleccionado = _indiceSeleccionado <= 0 ? 0 : _indiceSeleccionado - 1;
                break;

            case "Enter" when _indiceSeleccionado >= 0 && _indiceSeleccionado < ElementosPlanos.Count:
                Seleccionar(ElementosPlanos[_indiceSeleccionado], TiposPlanos[_indiceSeleccionado]);
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
        // best-effort, nunca puede bloquear, cancelar ni alterar la
        // navegación del usuario. Se lanza sin esperar y navega/cierra de
        // inmediato; cualquier fallo (incluida la pérdida del evento si el
        // circuito se destruye antes de completar) se descarta en silencio.
        _ = RegistrarUsoRecienteSilenciosamenteAsync(tipo, item);
    }

    private async Task RegistrarUsoRecienteSilenciosamenteAsync(string tipo, ItemBusquedaDto item)
    {
        try
        {
            await Mediator.Send(new RegistrarUsoRecienteCommand(
                tipo, item.Id == Guid.Empty ? null : item.Id, item.Titulo, item.Subtitulo, item.UrlDestino));
        }
        catch
        {
            // Best-effort: un fallo aquí nunca debe afectar al usuario.
        }
    }

    private async Task ManejarEntradaAsync(ChangeEventArgs e)
    {
        _termino = e.Value?.ToString() ?? string.Empty;

        _debounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;

        _indiceSeleccionado = -1;

        if (_termino.Trim().Length < 2)
        {
            _resultado = null;
            return;
        }

        try
        {
            _buscando = true;
            await Task.Delay(RetardoDebounce, cts.Token);

            _resultado = await Mediator.Send(new BuscarGlobalQuery(_termino), cts.Token);
        }
        catch (TaskCanceledException)
        {
            // Se canceló porque el usuario siguió escribiendo — ignorar.
        }
        finally
        {
            if (!cts.IsCancellationRequested)
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
        _debounceCts?.Cancel();

        // H5 (docs/ux-audit/16-transversales.md): mismo motivo que
        // AtajosListaTeclado.razor — el circuito puede desconectarse antes
        // de que corra este Dispose.
        try
        {
            if (_suscripcionAtajo is not null)
            {
                await _suscripcionAtajo.InvokeVoidAsync("dispose");
                await _suscripcionAtajo.DisposeAsync();
            }

            if (_suscripcionTab is not null)
            {
                await _suscripcionTab.InvokeVoidAsync("dispose");
                await _suscripcionTab.DisposeAsync();
            }

            if (_modulo is not null)
                await _modulo.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }

        _referenciaDotNet?.Dispose();
    }
}
