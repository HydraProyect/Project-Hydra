using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Integraciones.Queries.ObtenerEstructuraBuzon;
using CaeManager.Application.Integraciones.Queries.ObtenerMensajesCarpeta;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace CaeManager.Web.Features.Comunicaciones.Pages;

/// <summary>
/// Navegador de solo lectura del buzón real de Microsoft 365 — a diferencia
/// de <see cref="Bandeja"/>, que muestra <c>Conversacion</c> ya
/// ingeridas (solo Inbox, desde que se conectó el buzón), esta pantalla
/// consulta Graph en vivo: carpetas y mensajes que nunca pasaron por el
/// webhook (Enviados, Archivo, carpetas propias del cliente, historial
/// anterior a la conexión). Cierra "consultar la estructura de carpetas" y
/// "consultar cadena de correos antigua sin usar Outlook" del pedido del
/// usuario. Deliberadamente no muestra el cuerpo completo de un mensaje
/// (solo remitente/asunto/fecha/si tiene adjuntos) — verlo requeriría sanear
/// HTML externo antes de pintarlo (ver el mismo cuidado que
/// ObtenerConversacionPorIdQuery ya exige), que esta primera versión no
/// construye; el usuario puede seguir abriendo el mensaje real en Outlook
/// si necesita el cuerpo completo de un hilo que no está en Hydra todavía.
/// </summary>
public partial class Buzon : ComponentBase
{
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<Buzon> Logger { get; set; } = default!;

    private IReadOnlyList<ConexionIntegracionListaDto> _conexiones = [];
    private Guid? _conexionSeleccionadaId;
    private bool _cargandoConexiones = true;

    private IReadOnlyList<CarpetaGraphDto> _carpetas = [];
    private string? _carpetaSeleccionadaId;
    private bool _cargandoCarpetas;
    private string? _errorCarpetas;

    private IReadOnlyList<MensajeResumenGraphDto> _mensajes = [];
    private int _pagina = 1;
    private bool _cargandoMensajes;
    private string? _errorMensajes;

    private bool _redactarVisible;
    private string _redactarDestinatarios = string.Empty;
    private string _redactarAsunto = string.Empty;
    private string _redactarCuerpo = string.Empty;
    private readonly List<AdjuntoParaEnviarDto> _redactarAdjuntos = [];
    private string? _redactarError;
    private bool _enviandoRedaccion;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _conexiones = await Mediator.Send(new ObtenerConexionesIntegracionQuery(SoloPropiasYGenerales: true));
        }
        finally
        {
            _cargandoConexiones = false;
        }
    }

    private async Task SeleccionarConexionAsync(string conexionIdTexto)
    {
        _conexionSeleccionadaId = Guid.TryParse(conexionIdTexto, out var id) ? id : null;
        _carpetas = [];
        _carpetaSeleccionadaId = null;
        _mensajes = [];
        _errorCarpetas = null;

        if (_conexionSeleccionadaId is null) return;

        _cargandoCarpetas = true;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ObtenerEstructuraBuzonQuery(_conexionSeleccionadaId.Value));
            if (resultado.EsFallido)
            {
                _errorCarpetas = resultado.Error.Mensaje;
                return;
            }

            _carpetas = resultado.Valor;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cargar la estructura de carpetas de la conexión {ConexionId}.", _conexionSeleccionadaId);
            _errorCarpetas = "No pudimos leer las carpetas de este buzón.";
        }
        finally
        {
            _cargandoCarpetas = false;
            StateHasChanged();
        }
    }

    private Task SeleccionarCarpetaAsync(string carpetaId)
    {
        _carpetaSeleccionadaId = carpetaId;
        _pagina = 1;
        return CargarMensajesAsync();
    }

    private async Task CargarMensajesAsync()
    {
        if (_conexionSeleccionadaId is null || _carpetaSeleccionadaId is null) return;

        _cargandoMensajes = true;
        _errorMensajes = null;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ObtenerMensajesCarpetaQuery(_conexionSeleccionadaId.Value, _carpetaSeleccionadaId, _pagina));
            if (resultado.EsFallido)
            {
                _errorMensajes = resultado.Error.Mensaje;
                return;
            }

            _mensajes = resultado.Valor;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cargar mensajes de la carpeta {CarpetaId}.", _carpetaSeleccionadaId);
            _errorMensajes = "No pudimos leer el historial de esta carpeta.";
        }
        finally
        {
            _cargandoMensajes = false;
            StateHasChanged();
        }
    }

    private Task PaginaSiguienteAsync()
    {
        _pagina++;
        return CargarMensajesAsync();
    }

    private Task PaginaAnteriorAsync()
    {
        if (_pagina <= 1) return Task.CompletedTask;
        _pagina--;
        return CargarMensajesAsync();
    }

    private void AbrirRedactar()
    {
        _redactarDestinatarios = string.Empty;
        _redactarAsunto = string.Empty;
        _redactarCuerpo = string.Empty;
        _redactarAdjuntos.Clear();
        _redactarError = null;
        _redactarVisible = true;
    }

    private async Task ManejarArchivosRedactarAsync(InputFileChangeEventArgs e)
    {
        const int maximoArchivos = 5;

        foreach (var archivo in e.GetMultipleFiles(maximoArchivos))
        {
            await using var flujo = archivo.OpenReadStream(LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes);
            using var memoria = new MemoryStream();
            await flujo.CopyToAsync(memoria);
            _redactarAdjuntos.Add(new AdjuntoParaEnviarDto(archivo.Name, archivo.ContentType, memoria.ToArray()));
        }
    }

    private void QuitarAdjuntoRedactar(AdjuntoParaEnviarDto adjunto) => _redactarAdjuntos.Remove(adjunto);

    private async Task EnviarMensajeNuevoAsync()
    {
        if (_conexionSeleccionadaId is null) return;

        var destinatarios = _redactarDestinatarios
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (destinatarios.Count == 0)
        {
            _redactarError = "Indica al menos un destinatario.";
            return;
        }

        _enviandoRedaccion = true;
        _redactarError = null;
        try
        {
            var resultado = await Mediator.Send(new EnviarMensajeNuevoCommand(
                _conexionSeleccionadaId.Value, destinatarios, _redactarAsunto, _redactarCuerpo,
                Adjuntos: _redactarAdjuntos.Count > 0 ? _redactarAdjuntos.ToList() : null));

            if (resultado.EsFallido)
            {
                _redactarError = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Mensaje enviado.", TonoToast.Exito);
            _redactarVisible = false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al enviar un mensaje nuevo desde la conexión {ConexionId}.", _conexionSeleccionadaId);
            _redactarError = "No pudimos enviar el mensaje. Intenta nuevamente.";
        }
        finally
        {
            _enviandoRedaccion = false;
        }
    }

    private static string FormatearFecha(DateTime fechaUtc) => fechaUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
}
