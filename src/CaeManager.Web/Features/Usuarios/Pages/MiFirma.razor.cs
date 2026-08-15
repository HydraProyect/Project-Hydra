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
    private bool _trazoIniciado;
    private bool _guardando;

    protected override async Task OnInitializedAsync()
    {
        _firmaActual = await Mediator.Send(new ObtenerFirmaGuardadaUsuarioQuery());
        _cargando = false;
    }

    /// <summary>Ver comentario equivalente en FirmaEnCampoTab.razor.cs — no se gatea solo por firstRender.</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_moduloIniciado || _cargando) return;

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
