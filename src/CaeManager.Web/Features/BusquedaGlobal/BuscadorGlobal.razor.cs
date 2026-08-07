using CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;
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

    /// <summary>
    /// Acciones fijas del palette (P3-31) — navegación rápida a las listas y
    /// atajos de creación, mezcladas con los resultados de búsqueda. Filtro
    /// simple por substring, sin Query: no son datos, son rutas fijas de la
    /// propia app.
    /// </summary>
    private static readonly IReadOnlyList<(string Nombre, string Ruta)> ComandosNavegacion =
    [
        ("Ir a Clientes", "/clientes"),
        ("Ir a Empresas", "/empresas"),
        ("Ir a Subcontratas", "/subcontratas"),
        ("Ir a Centros", "/centros"),
        ("Ir a Trabajadores", "/trabajadores"),
        ("Ir a Documentos", "/documentos"),
        ("Ir a Dashboard", "/"),
        ("Crear cliente", "/clientes?accion=crear"),
        ("Alta guiada de cliente", "/clientes/alta-guiada"),
        ("Crear documento", "/documentos?accion=crear"),
    ];

    /// <summary>
    /// Comandos de navegación (filtrados por coincidencia de nombre) más,
    /// cuando la búsqueda de una categoría no encontró nada, un atajo para
    /// crearla con el término ya escrito precargado como nombre — pedido
    /// explícito: "si introduce una empresa/centro/trabajador que no se
    /// encuentre, dar la opción de crearlo con el nombre ya prellenado".
    /// </summary>
    private IReadOnlyList<ItemBusquedaDto> Comandos
    {
        get
        {
            var termino = _termino.Trim();
            if (termino.Length < 2) return [];

            var comandos = ComandosNavegacion
                .Where(c => c.Nombre.Contains(termino, StringComparison.OrdinalIgnoreCase))
                .Select(c => new ItemBusquedaDto(Guid.Empty, c.Nombre, null, c.Ruta))
                .ToList();

            if (_resultado is not null)
            {
                var terminoCodificado = Uri.EscapeDataString(termino);

                if (_resultado.Empresas.Count == 0)
                    comandos.Add(new ItemBusquedaDto(Guid.Empty, $"Crear empresa «{termino}»", null, $"/empresas?accion=crear&nombre={terminoCodificado}"));
                if (_resultado.Centros.Count == 0)
                    comandos.Add(new ItemBusquedaDto(Guid.Empty, $"Crear centro «{termino}»", null, $"/centros?accion=crear&nombre={terminoCodificado}"));
                if (_resultado.Trabajadores.Count == 0)
                    comandos.Add(new ItemBusquedaDto(Guid.Empty, $"Crear trabajador «{termino}»", null, $"/trabajadores?accion=crear&nombre={terminoCodificado}"));
            }

            return comandos;
        }
    }

    /// <summary>Todas las categorías en una sola lista, en el mismo orden en que se renderizan — para navegar con ↑↓ + Enter.</summary>
    private IReadOnlyList<ItemBusquedaDto> ElementosPlanos => _resultado is null
        ? Comandos
        : [.. Comandos, .. _resultado.Clientes, .. _resultado.Empresas, .. _resultado.Subcontratas, .. _resultado.Centros, .. _resultado.Trabajadores];

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
        StateHasChanged();

        if (_modulo is not null)
        {
            // Espera al siguiente render para que el <input> ya esté en el DOM antes de enfocarlo.
            await Task.Yield();
            await _modulo.InvokeVoidAsync("enfocarElemento", _inputElemento);
        }
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
                var destino = ElementosPlanos[_indiceSeleccionado].UrlDestino;
                Cerrar();
                Navigation.NavigateTo(destino);
                break;
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

            if (_modulo is not null)
                await _modulo.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }

        _referenciaDotNet?.Dispose();
    }
}
