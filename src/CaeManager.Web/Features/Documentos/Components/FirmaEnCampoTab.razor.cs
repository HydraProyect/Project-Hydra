using CaeManager.Application.Documentos.Commands.FirmarDocumentoEnCampo;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerFirmasEnCampoDocumento;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.Documentos.Components;

public partial class FirmaEnCampoTab : ComponentBase, IAsyncDisposable
{
    [Parameter, EditorRequired] public Guid EntidadId { get; set; }

    private readonly string _idCanvas = $"firma-en-campo-{Guid.NewGuid():N}";
    private DotNetObjectReference<FirmaEnCampoTab>? _referencia;
    private IJSObjectReference? _modulo;
    private bool _moduloIniciado;

    private DocumentoDetalleDto? _documento;
    private IReadOnlyList<FirmaEnCampoDocumentoDto> _firmas = [];
    private bool _cargando = true;
    private bool _error;
    private bool _trazoIniciado;
    private bool _incluirUbicacion;
    private bool _firmando;
    private Guid _idCargado;

    protected override Task OnParametersSetAsync()
    {
        if (EntidadId == _idCargado)
            return Task.CompletedTask;

        _idCargado = EntidadId;
        return CargarAsync();
    }

    private async Task CargarAsync()
    {
        _cargando = true;
        _error = false;

        try
        {
            _documento = await Mediator.Send(new ObtenerDocumentoPorIdQuery(EntidadId));
            _firmas = await Mediator.Send(new ObtenerFirmasEnCampoDocumentoQuery(EntidadId));
        }
        catch (Exception)
        {
            _error = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    /// <summary>
    /// No se gatea solo por <paramref name="firstRender"/>: la carga de
    /// _documento es asíncrona, así que el primer render real puede caer
    /// todavía en el estado "cargando" (sin &lt;canvas&gt; en el DOM). Se
    /// inicializa el módulo la primera vez que, tras cualquier render, el
    /// lienzo ya es visible.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_moduloIniciado || !PuedeFirmar()) return;

        _moduloIniciado = true;
        _referencia = DotNetObjectReference.Create(this);
        _modulo = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/firmaEnCampo.js");
        await _modulo.InvokeVoidAsync("iniciar", _referencia, _idCanvas);
    }

    private bool PuedeFirmar() =>
        _documento is { ArchivoUrl: not null, TipoDocumentoPerfilDocumentoOficial: Domain.Documentos.PerfilDocumentoOficial.Ninguno };

    [JSInvokable]
    public void MarcarTrazoIniciadoAsync()
    {
        _trazoIniciado = true;
        StateHasChanged();
    }

    private async Task RepetirAsync()
    {
        if (_modulo is null) return;

        await _modulo.InvokeVoidAsync("limpiar", _idCanvas);
        _trazoIniciado = false;
    }

    private async Task FirmarAsync()
    {
        if (_modulo is null || _firmando) return;

        _firmando = true;

        try
        {
            var trazoPngBase64 = await _modulo.InvokeAsync<string?>("exportarPng", _idCanvas);
            if (string.IsNullOrEmpty(trazoPngBase64))
            {
                Toasts.Mostrar("No se pudo capturar la firma. Vuelve a dibujarla.", TonoToast.Error);
                return;
            }

            var ubicacion = _incluirUbicacion ? await _modulo.InvokeAsync<string?>("obtenerUbicacion") : null;

            var resultado = await Mediator.Send(new FirmarDocumentoEnCampoCommand(EntidadId, trazoPngBase64, ubicacion));

            if (resultado.EsFallido)
            {
                Toasts.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            Toasts.Mostrar("Documento firmado.", TonoToast.Exito);
            await _modulo.InvokeVoidAsync("limpiar", _idCanvas);
            _trazoIniciado = false;
            await CargarAsync();
        }
        finally
        {
            _firmando = false;
        }
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
