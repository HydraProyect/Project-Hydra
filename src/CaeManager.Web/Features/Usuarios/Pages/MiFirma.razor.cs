using CaeManager.Application.Documentos.Commands.GuardarFirmaGuardadaUsuario;
using CaeManager.Application.Documentos.Queries.ObtenerFirmaGuardadaUsuario;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.Usuarios.Pages;

public partial class MiFirma : ComponentBase, IAsyncDisposable
{
    private const long TamanoMaximoArchivoBytes = 5 * 1024 * 1024;

    private readonly string _idCanvas = $"mi-firma-{Guid.NewGuid():N}";
    private DotNetObjectReference<MiFirma>? _referencia;
    private IJSObjectReference? _modulo;
    private bool _moduloIniciado;

    private FirmaGuardadaUsuarioDto? _firmaActual;
    private bool _cargando = true;
    private bool _errorCarga;
    private bool _trazoIniciado;
    private bool _guardando;

    // Firma "escrita" — ver comentario equivalente en FirmaEnCampoTab.razor.cs.
    private bool _escribirNombre;
    private string _nombreEscrito = string.Empty;

    /// <summary>
    /// La URL lleva la fecha de actualización para que cambie al guardar una
    /// firma nueva. Sin eso, el &lt;img&gt; conserva el mismo <c>src</c>, Blazor
    /// no produce diff y el navegador sigue mostrando la firma ANTERIOR aunque
    /// «Actualizada» ya diga la hora nueva. El endpoint ignora la query.
    /// </summary>
    private string? UrlFirmaGuardada => _firmaActual is null
        ? null
        : $"/mi-firma/archivo?v={_firmaActual.ActualizadaEnUtc.Ticks}";

    private string PistaLienzo => _escribirNombre
        ? "Escribe tu nombre arriba y aparecerá aquí, con la misma tipografía que se guardará."
        : "Dibuja aquí tu firma con el ratón, el dedo o el lápiz.";

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            _firmaActual = await Mediator.Send(new ObtenerFirmaGuardadaUsuarioQuery());
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    /// <summary>
    /// Ver comentario equivalente en FirmaEnCampoTab.razor.cs — no se gatea solo por firstRender.
    /// Tampoco se inicia en el estado de error: ahí no hay &lt;canvas&gt; al que engancharse, y
    /// marcarlo como iniciado impediría engancharlo cuando «Reintentar» lo haga aparecer.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_moduloIniciado || _cargando || _errorCarga) return;

        _moduloIniciado = true;
        _referencia = DotNetObjectReference.Create(this);
        _modulo = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/firmaEnCampo.js");
        await _modulo.InvokeVoidAsync("iniciar", _referencia, _idCanvas);
    }

    [JSInvokable]
    public void MarcarTrazoIniciadoAsync()
    {
        _trazoIniciado = true;
        StateHasChanged();
    }

    private async Task LimpiarLienzoAsync()
    {
        if (_modulo is null) return;

        await _modulo.InvokeVoidAsync("limpiar", _idCanvas);
        _trazoIniciado = false;
        _nombreEscrito = string.Empty;
    }

    private async Task CambiarModoEntradaAsync(bool escribir)
    {
        if (_escribirNombre == escribir) return;

        _escribirNombre = escribir;
        _nombreEscrito = string.Empty;
        _trazoIniciado = false;
        if (_modulo is not null)
            await _modulo.InvokeVoidAsync("limpiar", _idCanvas);
    }

    private async Task AlCambiarNombreEscritoAsync(string valor)
    {
        _nombreEscrito = valor;
        if (_modulo is not null)
            await _modulo.InvokeVoidAsync("escribirNombre", _idCanvas, valor);
        _trazoIniciado = !string.IsNullOrWhiteSpace(valor);
    }

    private async Task GuardarDibujadaAsync()
    {
        if (_modulo is null || _guardando) return;

        _guardando = true;
        try
        {
            var trazoPngBase64 = await _modulo.InvokeAsync<string?>("exportarPng", _idCanvas);
            if (string.IsNullOrEmpty(trazoPngBase64))
            {
                Toasts.Mostrar("No se pudo capturar el trazo. Vuelve a dibujarlo.", TonoToast.Error);
                return;
            }

            await GuardarAsync(Convert.FromBase64String(trazoPngBase64), esImagenDibujada: true);
        }
        finally
        {
            _guardando = false;
        }
    }

    private async Task SubirArchivoAsync(InputFileChangeEventArgs args)
    {
        _guardando = true;
        try
        {
            await using var flujo = args.File.OpenReadStream(TamanoMaximoArchivoBytes);
            using var memoria = new MemoryStream();
            await flujo.CopyToAsync(memoria);
            await GuardarAsync(memoria.ToArray(), esImagenDibujada: false);
        }
        catch (Exception)
        {
            Toasts.Mostrar("No se pudo leer el archivo. Prueba con otra imagen.", TonoToast.Error);
        }
        finally
        {
            _guardando = false;
        }
    }

    private async Task GuardarAsync(byte[] imagen, bool esImagenDibujada)
    {
        var resultado = await Mediator.Send(new GuardarFirmaGuardadaUsuarioCommand(imagen, esImagenDibujada));
        if (resultado.EsFallido)
        {
            Toasts.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            return;
        }

        Toasts.Mostrar("Firma guardada.", TonoToast.Exito);

        if (_modulo is not null)
            await _modulo.InvokeVoidAsync("limpiar", _idCanvas);
        _trazoIniciado = false;
        _nombreEscrito = string.Empty;

        _firmaActual = await Mediator.Send(new ObtenerFirmaGuardadaUsuarioQuery());
    }

    public async ValueTask DisposeAsync()
    {
        if (_modulo is not null)
        {
            try
            {
                await _modulo.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // El circuito ya se cerró: no hay módulo que liberar.
            }
        }

        _referencia?.Dispose();
    }
}
