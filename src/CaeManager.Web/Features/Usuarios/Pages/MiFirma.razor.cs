using CaeManager.Application.Documentos.Commands.GuardarFirmaGuardadaUsuario;
using CaeManager.Application.Documentos.Queries.ObtenerFirmaGuardadaUsuario;
using CaeManager.Domain.Common;
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

    /// <summary>
    /// Solo deja de ser null cuando firmaEnCampo.js se importó Y <c>iniciar</c>
    /// enganchó el lienzo: es a la vez la marca de «enganche correcto». Si se
    /// asignara antes, un fallo transitorio dejaría la marca puesta y el lienzo
    /// sin listeners para siempre.
    /// </summary>
    private IJSObjectReference? _modulo;

    /// <summary>
    /// Enganche en curso. Mientras los <c>await</c> del import/iniciar no
    /// vuelven, cualquier otro render dispara OnAfterRenderAsync otra vez; sin
    /// esta guarda el lienzo se engancharía dos veces (listeners duplicados).
    /// </summary>
    private bool _enganchando;

    private FirmaGuardadaUsuarioDto? _firmaActual;
    private bool _cargando = true;
    private bool _errorCarga;
    private bool _trazoIniciado;
    private bool _guardando;

    /// <summary>
    /// La firma se guardó pero la recarga posterior falló: lo que muestra la
    /// tarjeta «Firma guardada» es anterior al guardado (o dice que no hay
    /// firma cuando sí la hay), y la tarjeta tiene que decirlo.
    /// </summary>
    private bool _firmaMostradaDesactualizada;

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
    /// Si el import o <c>iniciar</c> fallan, no queda marca: el siguiente render lo reintenta.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_modulo is not null || _enganchando || _cargando || _errorCarga) return;

        _enganchando = true;
        IJSObjectReference? modulo = null;
        try
        {
            _referencia ??= DotNetObjectReference.Create(this);
            modulo = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/firmaEnCampo.js");
            await modulo.InvokeVoidAsync("iniciar", _referencia, _idCanvas);
            _modulo = modulo;
        }
        catch (Exception ex) when (ex is JSException or OperationCanceledException)
        {
            Logger.LogWarning(ex, "No se pudo enganchar el lienzo de Mi firma; se reintentará en el siguiente render.");
            if (modulo is not null)
                await LiberarModuloAsync(modulo);
        }
        finally
        {
            _enganchando = false;
        }
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
            byte[]? trazoPng;
            try
            {
                var trazoPngBase64 = await _modulo.InvokeAsync<string?>("exportarPng", _idCanvas);
                trazoPng = string.IsNullOrEmpty(trazoPngBase64) ? null : Convert.FromBase64String(trazoPngBase64);
            }
            catch (Exception ex) when (ex is JSException or FormatException)
            {
                Logger.LogWarning(ex, "No se pudo exportar el trazo del lienzo de Mi firma.");
                trazoPng = null;
            }

            if (trazoPng is null)
            {
                Toasts.Mostrar("No se pudo capturar el trazo. Vuelve a dibujarlo.", TonoToast.Error);
                return;
            }

            await GuardarAsync(trazoPng, esImagenDibujada: true);
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
            // Solo la lectura cae en «no se pudo leer»: un fallo al guardar o al
            // recargar lo informa GuardarAsync con su propio mensaje.
            byte[] imagen;
            try
            {
                await using var flujo = args.File.OpenReadStream(TamanoMaximoArchivoBytes);
                using var memoria = new MemoryStream();
                await flujo.CopyToAsync(memoria);
                imagen = memoria.ToArray();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "No se pudo leer el archivo subido en Mi firma.");
                Toasts.Mostrar("No se pudo leer el archivo. Prueba con otra imagen.", TonoToast.Error);
                return;
            }

            await GuardarAsync(imagen, esImagenDibujada: false);
        }
        finally
        {
            _guardando = false;
        }
    }

    /// <summary>
    /// Tres desenlaces con tres mensajes distintos, porque dicen cosas distintas
    /// sobre la firma del usuario: no se guardó (el lienzo conserva el trazo para
    /// reintentar) · se guardó y se muestra · se guardó pero no se pudo recargar
    /// (la tarjeta avisa de que lo que enseña ya no es la firma vigente).
    /// </summary>
    private async Task GuardarAsync(byte[] imagen, bool esImagenDibujada)
    {
        Result resultado;
        try
        {
            resultado = await Mediator.Send(new GuardarFirmaGuardadaUsuarioCommand(imagen, esImagenDibujada));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Fallo al guardar la firma del usuario en Mi firma.");
            Toasts.Mostrar("No pudimos guardar tu firma. Intenta nuevamente en unos segundos.", TonoToast.Error);
            return;
        }

        if (resultado.EsFallido)
        {
            Toasts.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            return;
        }

        // Guardado confirmado: a partir de aquí el trazo ya no hace falta.
        await LimpiarLienzoTrasGuardarAsync();

        if (await RecargarFirmaAsync())
        {
            Toasts.Mostrar("Firma guardada.", TonoToast.Exito);
        }
        else
        {
            Toasts.Mostrar("Tu firma se guardó, pero no pudimos mostrarla. Vuelve a cargarla para ver la nueva.",
                TonoToast.Advertencia);
        }
    }

    private async Task LimpiarLienzoTrasGuardarAsync()
    {
        if (_modulo is not null)
        {
            try
            {
                await _modulo.InvokeVoidAsync("limpiar", _idCanvas);
            }
            catch (JSException ex)
            {
                // La firma ya está guardada; un lienzo que no se vacía no la
                // pone en duda. Se deja el estado del trazo tal cual, que es
                // lo que sigue viendo el usuario.
                Logger.LogWarning(ex, "No se pudo limpiar el lienzo de Mi firma tras guardar.");
                return;
            }
        }

        _trazoIniciado = false;
        _nombreEscrito = string.Empty;
    }

    /// <summary>Devuelve <c>false</c> si la recarga falló; entonces la tarjeta queda marcada como desactualizada.</summary>
    private async Task<bool> RecargarFirmaAsync()
    {
        try
        {
            _firmaActual = await Mediator.Send(new ObtenerFirmaGuardadaUsuarioQuery());
            _firmaMostradaDesactualizada = false;
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Fallo al recargar la firma del usuario en Mi firma.");
            _firmaMostradaDesactualizada = true;
            return false;
        }
    }

    private async Task VolverACargarFirmaAsync()
    {
        if (!await RecargarFirmaAsync())
            Toasts.Mostrar("Seguimos sin poder cargar tu firma. Intenta nuevamente en unos segundos.", TonoToast.Error);
    }

    public async ValueTask DisposeAsync()
    {
        if (_modulo is not null)
            await LiberarModuloAsync(_modulo);

        _referencia?.Dispose();
    }

    private static async Task LiberarModuloAsync(IJSObjectReference modulo)
    {
        try
        {
            await modulo.DisposeAsync();
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException)
        {
            // El circuito ya se cerró, o el módulo ya no existe: no hay nada que liberar.
        }
    }
}
