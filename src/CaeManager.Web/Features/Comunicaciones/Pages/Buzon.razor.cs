using CaeManager.Domain.Common;
using CaeManager.Application.Common;
using Npgsql;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.ReconocerAvisoHueco;
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
/// webhook (Enviados, Archivo, carpetas propias del Cliente empresarial, historial
/// anterior a la conexión). Cierra "consultar la estructura de carpetas" y
/// "consultar cadena de correos antigua sin usar Outlook" del pedido del
/// usuario. Deliberadamente no muestra el cuerpo completo de un mensaje
/// (solo remitente/asunto/fecha/si tiene adjuntos) — verlo requeriría sanear
/// HTML externo antes de pintarlo (ver el mismo cuidado que
/// ObtenerConversacionPorIdQuery ya exige), que esta primera versión no
/// construye; el usuario puede seguir abriendo el mensaje real en Outlook
/// si necesita el cuerpo completo de un hilo que no está en TALVEG todavía.
/// </summary>
public partial class Buzon : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private IActorAuditoria ActorAuditoria { get; set; } = default!;
    [Inject] private ILogger<Buzon> Logger { get; set; } = default!;
    [Inject] private Microsoft.Extensions.Localization.IStringLocalizer<CaeManager.Web.Features.Comunicaciones.Recursos.TextosErroresConversaciones> ErroresTextos { get; set; } = default!;

    /// <summary>
    /// Los textos del aviso del hueco y del diálogo de Confirmar salen de <c>TextosConexiones</c>, los mismos que en Conexiones,
    /// sin copiarlos. El rótulo del botón Confirmar de esta página no: sale de <c>TextosComunes.AccionConfirmar</c> (abajo),
    /// mientras que Conexiones y <c>ModalConfirmarAvisoHueco</c> usan <c>TextosConexiones.AccionConfirmarAvisoHueco</c>. El
    /// verbo es el mismo en las dos fuentes (en los dos idiomas), pero hay que cambiarlo en los dos sitios.
    /// </summary>
    [Inject] private Microsoft.Extensions.Localization.IStringLocalizer<CaeManager.Web.Features.Integraciones.Recursos.TextosConexiones> TextosAviso { get; set; } = default!;

    // El rótulo del botón Confirmar sale de TextosComunes: la regla de Architecture exige que el texto de un disparador de escritura viva en un .resx del módulo (Comunicaciones) o en TextosComunes.
    [Inject] private Microsoft.Extensions.Localization.IStringLocalizer<CaeManager.Web.Recursos.TextosComunes> Comunes { get; set; } = default!;

    private IReadOnlyList<ConexionIntegracionListaDto> _conexiones = [];
    private Guid? _conexionSeleccionadaId;
    private bool _cargandoConexiones = true;
    private CancellationTokenSource? _cicloConexiones;
    private long _generacionConexiones;

    private IReadOnlyList<CarpetaGraphDto> _carpetas = [];
    private string? _carpetaSeleccionadaId;
    private bool _cargandoCarpetas;
    private string? _errorCarpetas;
    private CancellationTokenSource? _cicloCarpetas;
    private long _generacionCarpetas;

    private IReadOnlyList<MensajeResumenGraphDto> _mensajes = [];
    private int _pagina = 1;
    private bool _cargandoMensajes;
    private string? _errorMensajes;
    private bool _sinMasMensajes;
    private CancellationTokenSource? _cicloMensajes;
    private long _generacionMensajes;

    private bool _redactarVisible;
    private string _redactarDestinatarios = string.Empty;
    private string _redactarAsunto = string.Empty;
    private string _redactarCuerpo = string.Empty;
    private readonly List<AdjuntoParaEnviarDto> _redactarAdjuntos = [];
    private readonly AdjuntosCorreo _cargaAdjuntos = new();
    private string? _redactarError;
    private bool _enviandoRedaccion;

    private bool? _puedeConfirmarAviso;
    private ConexionIntegracionListaDto? _avisoHuecoAConfirmar;
    private bool _confirmandoAvisoHueco;

    protected override async Task OnInitializedAsync()
    {
        var (generacion, token) = IniciarCargaConexiones();
        try
        {
            await CargarConexionesAsync(generacion, token);
        }
        finally
        {
            if (EsCargaConexionesVigente(generacion, token))
                _cargandoConexiones = false;
        }
    }

    [CascadingParameter] private Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>? EstadoAutenticacion { get; set; }

    /// <summary>
    /// El listado del selector y, solo si alguna conexión tiene el aviso del hueco, si quien mira puede confirmarlo
    /// (<see cref="PuedeReconocerAvisoHuecoQuery"/>, la misma regla que el comando). Se pregunta una vez por página: no
    /// depende de la conexión. No se pregunta en Sesión Privilegiada (Soporte TALVEG es de solo lectura y el comando no
    /// llegaría al handler) ni con un rol sin escritura (<see cref="SoloConEscritura"/>), para que el texto del aviso no
    /// nombre un Confirmar que no se ofrece.
    /// </summary>
    private async Task CargarConexionesAsync(long generacion, CancellationToken token)
    {
        var conexiones = await Mediator.Send(new ObtenerConexionesIntegracionQuery(SoloPropiasYGenerales: true), token);
        if (!EsCargaConexionesVigente(generacion, token)) return;

        if (_puedeConfirmarAviso is null && conexiones.Any(c => c.CorreoNoRecuperadoDesdeUtc is not null))
        {
            var puede = await PreguntarSiPuedeConfirmarAsync(token);
            if (!EsCargaConexionesVigente(generacion, token)) return;
            _puedeConfirmarAviso = puede;
        }

        _conexiones = conexiones;
    }

    /// <summary>
    /// La pregunta es accesoria: si falla, falla cerrada (sin botón y con el texto que no nombra Confirmar) y el resto de la
    /// página sigue. Queda sin responder (null) para volver a preguntar en la siguiente recarga.
    /// </summary>
    private async Task<bool?> PreguntarSiPuedeConfirmarAsync(CancellationToken token)
    {
        try
        {
            var actor = await ActorAuditoria.ObtenerAsync();
            if (actor.Via == TipoViaAcceso.SesionPrivilegiada || !await SoloConEscritura.PuedeEscribirAsync(EstadoAutenticacion))
                return false;
            return await Mediator.Send(new PuedeReconocerAvisoHuecoQuery(), token);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Logger.LogError(ex, "Error al comprobar si se puede confirmar el aviso del hueco en Buzón.");
            return null;
        }
    }

    private ConexionIntegracionListaDto? ConexionSeleccionada =>
        _conexionSeleccionadaId is { } id ? _conexiones.FirstOrDefault(c => c.Id == id) : null;

    /// <summary>
    /// Confirmar sobre el aviso: quien mira puede (rol y cartera) y la conexión está en el estado en que el comando lo
    /// acepta (<see cref="ConexionIntegracionListaDto.AvisoHuecoReconocible"/>). Sin eso el aviso se ve, sin botón.
    /// </summary>
    private bool OfreceConfirmar(ConexionIntegracionListaDto conexion) =>
        _puedeConfirmarAviso == true && conexion.AvisoHuecoReconocible;

    private void AbrirConfirmacionAvisoHueco(ConexionIntegracionListaDto conexion)
    {
        if (_confirmandoAvisoHueco) return;
        _avisoHuecoAConfirmar = conexion;
    }

    private void CerrarConfirmacionAvisoHueco()
    {
        if (!_confirmandoAvisoHueco) _avisoHuecoAConfirmar = null;
    }

    /// <summary>
    /// Envía la fecha del aviso que se ve en el diálogo, como Conexiones: si la marca ya es otra, el servidor no la reconoce
    /// y su mensaje sale tal cual. Con respuesta, el diálogo se cierra y el listado se recarga para mostrar el estado real;
    /// la conexión elegida, sus carpetas y su historial no cambian.
    /// </summary>
    private async Task ConfirmarAvisoHuecoAsync()
    {
        if (_avisoHuecoAConfirmar is not { CorreoNoRecuperadoDesdeUtc: { } marca } conexion || _confirmandoAvisoHueco) return;

        _confirmandoAvisoHueco = true;
        try
        {
            var resultado = await Mediator.Send(new ReconocerAvisoHuecoCommand(conexion.Id, marca));
            if (resultado.EsFallido)
                ToastService.MostrarError(resultado.Error);
            else
                ToastService.Mostrar(TextosAviso["AvisoHuecoConfirmado"], TonoToast.Exito);

            _avisoHuecoAConfirmar = null;
            await RecargarConexionesAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al confirmar el aviso del hueco de la conexión {ConexionId}.", conexion.Id);
            ToastService.Mostrar(TextosAviso["ErrorConfirmarAvisoHueco"], TonoToast.Error);
        }
        finally
        {
            _confirmandoAvisoHueco = false;
        }
    }

    private async Task RecargarConexionesAsync()
    {
        if (_desechada) return;
        var (generacion, token) = IniciarCargaConexiones();
        try
        {
            await CargarConexionesAsync(generacion, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // La página se cerró o empezó otra carga: no hay nada que avisar.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al recargar las conexiones de Buzón.");
            ToastService.Mostrar(TextosAviso["AvisoRecargaFallida"], TonoToast.Error);
        }
    }

    private async Task SeleccionarConexionAsync(string conexionIdTexto)
    {
        _conexionSeleccionadaId = Guid.TryParse(conexionIdTexto, out var id) ? id : null;
        _carpetas = [];
        _carpetaSeleccionadaId = null;
        _mensajes = [];
        _sinMasMensajes = false;
        _errorCarpetas = null;
        CancelarCargaCarpetas();
        CancelarCargaMensajes();

        if (_conexionSeleccionadaId is null) return;

        var (generacion, token) = IniciarCargaCarpetas();
        var conexionId = _conexionSeleccionadaId.Value;
        _cargandoCarpetas = true;
        StateHasChanged();

        try
        {
            var actor = await ActorAuditoria.ObtenerAsync();
            if (!EsCargaCarpetasVigente(generacion, token)) return;
            if (actor.Via == TipoViaAcceso.SesionPrivilegiada)
            {
                _errorCarpetas = "No pudimos leer las carpetas de este buzón.";
                return;
            }

            var resultado = await Mediator.Send(new ObtenerEstructuraBuzonQuery(conexionId), token);
            if (!EsCargaCarpetasVigente(generacion, token)) return;
            if (resultado.EsFallido)
            {
                _errorCarpetas = resultado.Error.Mensaje;
                return;
            }

            _carpetas = resultado.Valor;
        }
        catch (Exception ex)
        {
            if (!EsCargaCarpetasVigente(generacion, token)) return;
            if (EsPermisoInsuficiente(ex))
                Logger.LogWarning(ex, "Permisos insuficientes al cargar la estructura de carpetas de la conexión {ConexionId}.", conexionId);
            else
                Logger.LogError(ex, "Error al cargar la estructura de carpetas de la conexión {ConexionId}.", conexionId);
            _errorCarpetas = "No pudimos leer las carpetas de este buzón.";
        }
        finally
        {
            if (EsCargaCarpetasVigente(generacion, token))
            {
                _cargandoCarpetas = false;
                StateHasChanged();
            }
        }
    }

    private static bool EsPermisoInsuficiente(Exception excepcion)
    {
        for (Exception? actual = excepcion; actual is not null; actual = actual.InnerException)
            if (actual is PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege }) return true;
        return false;
    }

    private Task SeleccionarCarpetaAsync(string carpetaId)
    {
        _carpetaSeleccionadaId = carpetaId;
        _pagina = 1;
        _sinMasMensajes = false;
        CancelarCargaMensajes();
        return CargarMensajesAsync();
    }

    private async Task CargarMensajesAsync()
    {
        if (_conexionSeleccionadaId is null || _carpetaSeleccionadaId is null) return;

        var conexionId = _conexionSeleccionadaId.Value;
        var carpetaIdSeleccionada = _carpetaSeleccionadaId;
        var pagina = _pagina;
        var (generacion, token) = IniciarCargaMensajes();
        _cargandoMensajes = true;
        _errorMensajes = null;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ObtenerMensajesCarpetaQuery(conexionId, carpetaIdSeleccionada, pagina), token);
            if (!EsCargaMensajesVigente(generacion, token)) return;
            if (resultado.EsFallido)
            {
                _errorMensajes = resultado.Error.Mensaje;
                return;
            }

            _mensajes = resultado.Valor;
            _sinMasMensajes = _mensajes.Count == 0 && pagina > 1;
        }
        catch (Exception ex)
        {
            if (!EsCargaMensajesVigente(generacion, token)) return;
            Logger.LogError(ex, "Error al cargar mensajes de la carpeta {CarpetaId}.", _carpetaSeleccionadaId);
            _errorMensajes = "No pudimos leer el historial de esta carpeta.";
        }
        finally
        {
            if (EsCargaMensajesVigente(generacion, token))
            {
                _cargandoMensajes = false;
                StateHasChanged();
            }
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
        _instantanea.Fijar(ValoresFormulario());
        _redactarVisible = true;
    }

    private async Task ManejarArchivosRedactarAsync(InputFileChangeEventArgs e)
    {
        _redactarError = AdjuntosCorreo.Mensaje(await _cargaAdjuntos.AgregarAsync(e, _redactarAdjuntos), ErroresTextos);
    }

    private void QuitarAdjuntoRedactar(AdjuntoParaEnviarDto adjunto)
    {
        _redactarAdjuntos.Remove(adjunto);
        _redactarError = null;
    }

    private async Task EnviarMensajeNuevoAsync()
    {
        if (_conexionSeleccionadaId is null || _enviandoRedaccion) return;

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

    private static string FormatearFecha(DateTime fechaUtc) => fechaUtc.EnHoraPeninsular().ToString("dd/MM/yyyy HH:mm");

    private static string TextoConexion(ConexionIntegracionListaDto conexion) =>
        $"{conexion.Nombre} ({conexion.BuzonEmail})" + (conexion.ClienteNombre is null ? string.Empty : $" — {conexion.ClienteNombre}");

    private (long Generacion, CancellationToken Token) IniciarCargaConexiones()
    {
        _cicloConexiones?.Cancel();
        _cicloConexiones?.Dispose();
        _cicloConexiones = new CancellationTokenSource();
        return (++_generacionConexiones, _cicloConexiones.Token);
    }

    private (long Generacion, CancellationToken Token) IniciarCargaCarpetas()
    {
        CancelarCargaCarpetas();
        _cicloCarpetas = new CancellationTokenSource();
        return (++_generacionCarpetas, _cicloCarpetas.Token);
    }

    private (long Generacion, CancellationToken Token) IniciarCargaMensajes()
    {
        CancelarCargaMensajes();
        _cicloMensajes = new CancellationTokenSource();
        return (++_generacionMensajes, _cicloMensajes.Token);
    }

    private bool EsCargaConexionesVigente(long generacion, CancellationToken token) =>
        generacion == _generacionConexiones && !token.IsCancellationRequested;

    private bool EsCargaCarpetasVigente(long generacion, CancellationToken token) =>
        generacion == _generacionCarpetas && !token.IsCancellationRequested;

    private bool EsCargaMensajesVigente(long generacion, CancellationToken token) =>
        generacion == _generacionMensajes && !token.IsCancellationRequested;

    private void CancelarCargaCarpetas()
    {
        _cicloCarpetas?.Cancel();
        _cicloCarpetas?.Dispose();
        _cicloCarpetas = null;
        _generacionCarpetas++;
        _cargandoCarpetas = false;
    }

    private void CancelarCargaMensajes()
    {
        _cicloMensajes?.Cancel();
        _cicloMensajes?.Dispose();
        _cicloMensajes = null;
        _generacionMensajes++;
        _cargandoMensajes = false;
    }

    /// <summary>La página se ha cerrado: un Confirmar que vuelve después no recarga ni crea otro ciclo de carga.</summary>
    private bool _desechada;

    public void Dispose()
    {
        _desechada = true;
        _cicloConexiones?.Cancel();
        _cicloConexiones?.Dispose();
        CancelarCargaCarpetas();
        CancelarCargaMensajes();
    }

    private readonly InstantaneaFormulario _instantanea = new();

    /// <summary>
    /// P1-E2b: único punto de verdad de «hay cambios» en la página: el mensaje que se está
    /// redactando (adjuntos incluidos) comparado con cómo se abrió. Lo lee DrawerFormulario:
    /// la X, Escape, el fondo, «Cancelar» y salir de la página.
    /// </summary>
    private bool HayCambiosSinGuardar => _redactarVisible && _instantanea.Difiere(ValoresFormulario());

    private object?[] ValoresFormulario() =>
        [_redactarDestinatarios, _redactarAsunto, _redactarCuerpo, _redactarAdjuntos.Select(a => a.NombreArchivo).ToList(), _redactarAdjuntos.Count];
}
