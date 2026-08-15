using CaeManager.Application.Documentos.Commands.FirmarDocumentoEnCampo;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerEmpresasConSelloGuardado;
using CaeManager.Application.Documentos.Queries.ObtenerFirmaGuardadaUsuario;
using CaeManager.Application.Documentos.Queries.ObtenerFirmasEnCampoDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerSelloEmpresa;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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

    // Firma guardada del usuario (PR 8): si existe, se ofrece por defecto en
    // vez de tener que volver a dibujar en cada Documento.
    private FirmaGuardadaUsuarioDto? _firmaGuardada;
    private bool _usarFirmaGuardada;
    private bool _guardarComoFirmaHabitual;

    // Firma "escrita": el nombre tecleado se renderiza con una tipografía
    // cursiva sobre el mismo canvas del trazo (ver firmaEnCampo.js,
    // escribirNombre) — desde ahí en adelante sigue el mismo camino que un
    // trazo dibujado a mano, exportarPng no distingue el origen.
    private bool _escribirNombre;
    private string _nombreEscrito = string.Empty;

    // Sello de la Empresa relevante — resuelto directamente si el Documento
    // es de ámbito Empresa, o elegido de un selector en cualquier otro
    // ámbito (Trabajador/Cliente/Vehículo/Proyecto no auto-resuelven).
    private bool _seloDirectoDisponible;
    private bool _incluirSello;
    private IReadOnlyList<EmpresaConSelloDto> _empresasConSello = [];
    private Guid? _empresaSeleccionadaEnSelector;

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
            _firmaGuardada = await Mediator.Send(new ObtenerFirmaGuardadaUsuarioQuery());
            _usarFirmaGuardada = _firmaGuardada is not null;
            _guardarComoFirmaHabitual = _firmaGuardada is null;

            if (_documento?.EmpresaId is { } empresaId)
            {
                var sello = await Mediator.Send(new ObtenerSelloEmpresaQuery(empresaId));
                _seloDirectoDisponible = sello is not null;
                _incluirSello = sello is not null;
            }
            else
            {
                _empresasConSello = await Mediator.Send(new ObtenerEmpresasConSelloGuardadoQuery());
            }
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
    /// lienzo ya es visible — el lienzo se renderiza siempre (aunque oculto
    /// tras "usar mi firma"), para no tener que re-atar los listeners cada
    /// vez que se alterna entre firma guardada y dibujo.
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
        _documento is { ArchivoUrl: not null, TipoDocumentoPerfilDocumentoOficial: PerfilDocumentoOficial.Ninguno };

    private bool PuedeFirmarAhora() => _usarFirmaGuardada || _trazoIniciado;

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

    private async Task CambiarAOtraFirmaAsync()
    {
        _usarFirmaGuardada = false;
        _escribirNombre = false;
        _nombreEscrito = string.Empty;
        _trazoIniciado = false;
        if (_modulo is not null)
            await _modulo.InvokeVoidAsync("limpiar", _idCanvas);
    }

    private async Task UsarFirmaGuardadaAsync()
    {
        _usarFirmaGuardada = true;
        _escribirNombre = false;
        _nombreEscrito = string.Empty;
        _trazoIniciado = false;
        if (_modulo is not null)
            await _modulo.InvokeVoidAsync("limpiar", _idCanvas);
    }

    private void AlCambiarEmpresaSeleccionadaParaSello(ChangeEventArgs args) =>
        _empresaSeleccionadaEnSelector = Guid.TryParse(args.Value as string, out var id) ? id : null;

    private Guid? ResolverSelloEmpresaId() =>
        _seloDirectoDisponible ? (_incluirSello ? _documento!.EmpresaId : null) : _empresaSeleccionadaEnSelector;

    private async Task FirmarAsync()
    {
        if (_modulo is null || _firmando || !PuedeFirmarAhora()) return;

        _firmando = true;

        try
        {
            string? trazoPngBase64 = null;
            if (!_usarFirmaGuardada)
            {
                trazoPngBase64 = await _modulo.InvokeAsync<string?>("exportarPng", _idCanvas);
                if (string.IsNullOrEmpty(trazoPngBase64))
                {
                    Toasts.Mostrar("No se pudo capturar la firma. Vuelve a dibujarla.", TonoToast.Error);
                    return;
                }
            }

            var ubicacion = _incluirUbicacion ? await _modulo.InvokeAsync<string?>("obtenerUbicacion") : null;

            var resultado = await Mediator.Send(new FirmarDocumentoEnCampoCommand(
                EntidadId, trazoPngBase64, ubicacion, _usarFirmaGuardada, ResolverSelloEmpresaId(),
                !_usarFirmaGuardada && _guardarComoFirmaHabitual));

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
