using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto;
using CaeManager.Application.Comunicaciones.Commands.AsignarClienteConversacion;
using CaeManager.Application.Comunicaciones.Commands.AsignarEjecutivoConversacion;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Comunicaciones.Commands.CambiarEstadoConversacion;
using CaeManager.Application.Comunicaciones.Commands.DescartarSugerenciaGestion;
using CaeManager.Application.Comunicaciones.Commands.DescartarSugerenciaVisita;
using CaeManager.Application.Comunicaciones.Commands.MigrarConversacionACorreo;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacionWhatsApp;
using CaeManager.Application.Comunicaciones.Eventos;
using CaeManager.Application.Comunicaciones.Queries.DetectarActualizacionDocumentoDesdeAdjunto;
using CaeManager.Application.Gestiones.Commands.CrearGestionesParaTrabajador;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Comunicaciones.Queries.ObtenerFormatosRequeridosCentro;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMacros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Integraciones;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Infrastructure.Autorizacion;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.Comunicaciones.Pages;

public record EjecutivoSelectorDto(Guid Id, string NombreCompleto);

/// <summary>
/// Communication Workspace unificado (docs/COMUNICACIONES.md § 16, paso 2
/// del rediseño): fusiona lo que antes eran Bandeja (correo) y Chat
/// (WhatsApp) — una sola bandeja, una sola conversación seleccionada, un
/// único ComposerBar que cambia de modo según Canal. El principio rector
/// (§ 10.2) es que el gestor nunca elige "voy a Correo" o "voy a WhatsApp":
/// entra a Comunicaciones y ve conversaciones.
/// </summary>
public partial class Bandeja : ComponentBase, IDisposable
{
    [Inject] private DirectorioUsuariosTenant DirectorioUsuarios { get; set; } = default!;
    [Inject] private ILogger<Bandeja> Logger { get; set; } = default!;
    [Inject] private IOptions<ComunicacionesOptions> OpcionesComunicaciones { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private INotificadorMensajesTiempoReal Notificador { get; set; } = default!;
    [Inject] private ITenantActual TenantActual { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "estado")] public string? EstadoInicial { get; set; }
    [SupplyParameterFromQuery(Name = "mes")] public string? MesInicial { get; set; }
    [SupplyParameterFromQuery(Name = "cliente")] public string? ClienteInicial { get; set; }
    [SupplyParameterFromQuery(Name = "q")] public string? BusquedaInicial { get; set; }

    // --- Filtros ---
    private string _estadoFiltro = string.Empty;
    private string _mesFiltro = string.Empty; // input type="month" -> "yyyy-MM"
    private string _clienteIdFiltro = string.Empty;
    private bool _soloAsignadasAMi;
    private bool _soloSinAsignar;
    private bool _soloEsperandoCliente;
    private string _busqueda = string.Empty;

    private bool _cargandoLista = true;
    private bool _errorCargaLista;
    private IReadOnlyList<ConversacionListaDto> _conversaciones = [];
    private IReadOnlyList<ClienteSelectorDto> _clientesSelector = [];
    private IReadOnlyList<EjecutivoSelectorDto> _ejecutivosDisponibles = [];

    private readonly HashSet<string> _gruposColapsados = [];

    // --- Detalle / centro ---
    private Guid? _conversacionSeleccionadaId;
    private ConversacionDetalleDto? _detalle;
    private bool _cargandoDetalle;
    private ClienteDetalleDto? _clienteActivo;
    private IReadOnlyList<MacroListaDto> _macrosDisponibles = [];
    private IReadOnlyList<CentroSelectorDto> _centrosClienteActivo = [];

    // --- Composer (compartido entre Correo/WhatsApp/fallback — ver ComposerBar) ---
    private string _textoRespuesta = string.Empty;
    private string _macroSeleccionadaId = string.Empty;
    private string _centroFormatosSeleccionado = string.Empty;
    private bool _enviando;
    private readonly List<AdjuntoParaEnviarDto> _adjuntosPendientes = [];
    private string? _errorAdjuntos;
    private string _emailFallback = string.Empty;
    private string _ejecutivoSeleccionado = string.Empty;
    private bool _cambiandoEjecutivo;
    private bool _cambiandoEstado;

    private string _clienteTriageSeleccionado = string.Empty;
    private bool _asignandoCliente;

    // --- Actualizar documentación desde adjunto (§ 12.7) ---
    private bool _modalActualizarDocumentoVisible;
    private Guid _adjuntoParaActualizarDocumentoId;
    private bool _detectandoDocumento;
    private bool _aplicandoDocumento;
    private string? _errorActualizarDocumento;
    private IReadOnlyList<TipoDocumentoListaDto> _tiposDocumentoSelector = [];
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresSelector = [];
    private IReadOnlyList<EmpresaSelectorDto> _empresasSelector = [];
    private string _tipoDocumentoIdFormulario = string.Empty;
    private string _propietarioDocumentoFormulario = nameof(AmbitoAplicacion.Trabajador);
    private string _trabajadorDocumentoIdFormulario = string.Empty;
    private string _empresaDocumentoIdFormulario = string.Empty;
    private string _fechaEmisionDocumentoFormulario = string.Empty;
    private string _fechaVencimientoDocumentoFormulario = string.Empty;
    private string _comentariosDocumentoFormulario = string.Empty;
    private int _confianzaDeteccionDocumento;

    private IDisposable? _suscripcionTiempoReal;

    private bool VentanaAbierta =>
        _detalle?.Canal == CanalConversacion.WhatsApp &&
        _detalle.FechaUltimoMensajeEntranteUtc is { } ultimo &&
        DateTime.UtcNow - ultimo < Conversacion.DuracionVentanaServicio;

    private string CierreVentanaLocal =>
        _detalle?.FechaUltimoMensajeEntranteUtc is { } ultimo
            ? ultimo.Add(Conversacion.DuracionVentanaServicio).ToLocalTime().ToString("dd/MM 'a las' HH:mm")
            : string.Empty;

    protected override async Task OnInitializedAsync()
    {
        // Módulo congelado por defecto (ComunicacionesOptions, P2 #26 de
        // docs/business/MATURITY_REVIEW.md): sin ingesta real detrás, se
        // presenta como si la ruta no existiera en vez de mostrar una
        // bandeja que nadie va a alimentar de verdad.
        if (!OpcionesComunicaciones.Value.Activo)
        {
            NavigationManager.NavigateTo("/not-found");
            return;
        }

        // Los [Parameter] ya están asignados en este punto — se leen aquí y
        // no solo en OnParametersSet porque en el primer render
        // OnInitializedAsync corre ANTES que OnParametersSet, y la carga
        // inicial de abajo necesita los filtros ya resueltos (P1-18 de
        // docs/business/MATURITY_REVIEW.md).
        SincronizarFiltrosDesdeUrl();

        _clientesSelector = await Mediator.Send(new ObtenerClientesParaSelectorQuery());

        // Acotado al tenant activo: GetUsersInRoleAsync devuelve los gestores
        // de todas las organizaciones (AspNetUsers no tiene filtro global),
        // así que el selector listaba nombres de empleados de otros tenants.
        var gestores = await DirectorioUsuarios.ObtenerVisiblesEnRolAsync(Roles.GestorCae);
        _ejecutivosDisponibles = gestores
            .Select(u => new EjecutivoSelectorDto(u.Id, u.NombreCompleto))
            .ToList();

        // La suscripción es del tenant del circuito: los avisos de otros
        // tenants nunca llegan aquí (el notificador ya segrega por tenant).
        // Refresca la bandeja unificada al instante cuando la ingesta de
        // WhatsApp persiste un mensaje nuevo — antes solo lo hacía /chat.
        if (TenantActual.TenantId is { } tenantId)
            _suscripcionTiempoReal = Notificador.Suscribir(tenantId, AlRecibirMensajeAsync);

        await CargarListaAsync();
    }

    public void Dispose() => _suscripcionTiempoReal?.Dispose();

    /// <summary>Llega desde el hilo del job de fondo — todo lo que toque estado del componente va dentro de InvokeAsync.</summary>
    private Task AlRecibirMensajeAsync(MensajeWhatsAppRecibidoEvent aviso) =>
        InvokeAsync(async () =>
        {
            try
            {
                await CargarListaAsync();
                // Refresco ligero: NO pasa por SeleccionarConversacionAsync
                // para no perder lo que el gestor esté escribiendo a mitad
                // de una respuesta cuando llega un mensaje nuevo.
                if (aviso.ConversacionId == _conversacionSeleccionadaId)
                    await CargarDetalleAsync();
                StateHasChanged();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "No se pudo refrescar la bandeja al recibir un aviso de tiempo real.");
            }
        });

    /// <summary>
    /// Re-sincroniza los filtros con la URL en navegaciones posteriores
    /// dentro de la propia página (volver atrás, compartir la URL) — la
    /// recarga la sigue disparando explícitamente AplicarFiltrosAsync, no
    /// este método, para no depender del timing del router.
    /// </summary>
    protected override void OnParametersSet() => SincronizarFiltrosDesdeUrl();

    private void SincronizarFiltrosDesdeUrl()
    {
        _estadoFiltro = EstadoInicial ?? string.Empty;
        _mesFiltro = MesInicial ?? string.Empty;
        _clienteIdFiltro = ClienteInicial ?? string.Empty;
        _busqueda = BusquedaInicial ?? string.Empty;
    }

    private async Task CargarListaAsync()
    {
        _cargandoLista = true;
        _errorCargaLista = false;
        StateHasChanged();

        try
        {
            // El input HTML type="month" entrega "yyyy-MM" — se parsea a mano
            // para no depender de que el formato coincida con la cultura actual.
            int? anio = null;
            int? mes = null;
            var partesMes = _mesFiltro.Split('-');
            if (partesMes.Length == 2 && int.TryParse(partesMes[0], out var anioParseado) && int.TryParse(partesMes[1], out var mesParseado))
            {
                anio = anioParseado;
                mes = mesParseado;
            }

            _conversaciones = await Mediator.Send(new ObtenerConversacionesQuery(
                Estado: string.IsNullOrEmpty(_estadoFiltro) ? null : Enum.Parse<EstadoConversacion>(_estadoFiltro),
                Anio: anio,
                Mes: mes,
                ClienteId: Guid.TryParse(_clienteIdFiltro, out var clienteId) ? clienteId : null,
                SoloAsignadasAMi: _soloAsignadasAMi,
                SoloSinAsignar: _soloSinAsignar,
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda));
            // Sin filtro de Canal a propósito (docs/COMUNICACIONES.md § 10.2):
            // el gestor ve conversaciones, no "correo" o "WhatsApp" por separado.
        }
        catch (Exception ex)
        {
            // _errorCargaLista solo pinta un aviso genérico: sin este log, un
            // fallo al cargar la bandeja no deja ningún rastro que permita
            // diagnosticarlo después.
            Logger.LogError(ex, "Error al cargar la lista de conversaciones de la bandeja.");
            _errorCargaLista = true;
        }
        finally
        {
            _cargandoLista = false;
            StateHasChanged();
        }
    }

    private Task AplicarFiltrosAsync()
    {
        // Los cuatro a la vez en una sola navegación — llamar a
        // ActualizarFiltroEnUrl varias veces seguidas arriesgaría que cada
        // NavigateTo lea la URL todavía sin el cambio del anterior.
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["estado"] = _estadoFiltro,
            ["mes"] = _mesFiltro,
            ["cliente"] = _clienteIdFiltro,
            ["q"] = _busqueda
        });

        return CargarListaAsync();
    }

    private Task FiltrarAsignadasAMiAsync()
    {
        _soloAsignadasAMi = !_soloAsignadasAMi;
        if (_soloAsignadasAMi) _soloSinAsignar = false;
        return CargarListaAsync();
    }

    private Task FiltrarSinAsignarAsync()
    {
        _soloSinAsignar = !_soloSinAsignar;
        if (_soloSinAsignar) _soloAsignadasAMi = false;
        return CargarListaAsync();
    }

    private Task VerTodasAsync()
    {
        _soloAsignadasAMi = false;
        _soloSinAsignar = false;
        return CargarListaAsync();
    }

    /// <summary>
    /// "Esperando cliente" es un filtro puramente cliente (ConversacionListaDto.EsperandoCliente
    /// es un campo derivado, no persistido — § 16.4): no hace falta redondear
    /// contra el servidor, se aplica sobre la lista ya cargada.
    /// </summary>
    private void AlternarEsperandoCliente() => _soloEsperandoCliente = !_soloEsperandoCliente;

    private IEnumerable<ConversacionListaDto> ConversacionesVisibles() =>
        _soloEsperandoCliente ? _conversaciones.Where(c => c.EsperandoCliente) : _conversaciones;

    private IEnumerable<IGrouping<Guid, ConversacionListaDto>> GruposPorCliente() =>
        ConversacionesVisibles().Where(c => c.ClienteId is not null).GroupBy(c => c.ClienteId!.Value);

    private IReadOnlyList<ConversacionListaDto> ConversacionesTriage() =>
        ConversacionesVisibles().Where(c => c.ClienteId is null).OrderByDescending(c => c.FechaUltimoMensajeUtc).ToList();

    private void AlternarGrupo(string clave)
    {
        if (!_gruposColapsados.Add(clave))
            _gruposColapsados.Remove(clave);
    }

    private bool GrupoColapsado(string clave) => _gruposColapsados.Contains(clave);

    private async Task SeleccionarConversacionAsync(Guid id)
    {
        _conversacionSeleccionadaId = id;
        _textoRespuesta = string.Empty;
        _macroSeleccionadaId = string.Empty;
        _clienteTriageSeleccionado = string.Empty;
        _adjuntosPendientes.Clear();
        _errorAdjuntos = null;
        _centroFormatosSeleccionado = string.Empty;
        _emailFallback = string.Empty;

        await CargarDetalleAsync();
    }

    private async Task CargarDetalleAsync()
    {
        if (_conversacionSeleccionadaId is not { } id) return;

        _cargandoDetalle = true;
        StateHasChanged();

        try
        {
            _detalle = await Mediator.Send(new ObtenerConversacionPorIdQuery(id));
            _ejecutivoSeleccionado = _detalle?.EjecutivoAsignadoId?.ToString() ?? string.Empty;

            if (_detalle?.ClienteId is not null)
            {
                _clienteActivo = await Mediator.Send(new ObtenerClientePorIdQuery(_detalle.ClienteId.Value));
                _macrosDisponibles = await Mediator.Send(new ObtenerMacrosQuery(_detalle.ClienteId));
                _centrosClienteActivo = await Mediator.Send(new ObtenerCentrosParaSelectorQuery(ClienteId: _detalle.ClienteId));
            }
            else
            {
                _clienteActivo = null;
                _macrosDisponibles = [];
                _centrosClienteActivo = [];
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al abrir la conversación {ConversacionId}.", id);
            ToastService.Mostrar("No pudimos abrir esta conversación. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _cargandoDetalle = false;
            StateHasChanged();
        }
    }

    private void AplicarMacro(string macroIdTexto)
    {
        _macroSeleccionadaId = macroIdTexto;
        if (Guid.TryParse(macroIdTexto, out var macroId))
        {
            var macro = _macrosDisponibles.FirstOrDefault(m => m.Id == macroId);
            if (macro is not null)
                _textoRespuesta = macro.CuerpoHtml;
        }
    }

    /// <summary>Genera el resumen de documentación exigida por el Centro elegido y lo añade a la respuesta en curso — mismo patrón de prellenado que AplicarMacro.</summary>
    private async Task CompartirFormatosCentroAsync(string centroIdTexto)
    {
        _centroFormatosSeleccionado = centroIdTexto;
        if (!Guid.TryParse(centroIdTexto, out var centroId)) return;

        try
        {
            var formatos = await Mediator.Send(new ObtenerFormatosRequeridosCentroQuery(centroId));
            if (formatos is null)
            {
                ToastService.Mostrar("Este centro no tiene requisitos documentales configurados.", TonoToast.Info);
                return;
            }

            _textoRespuesta = string.IsNullOrWhiteSpace(_textoRespuesta) ? formatos : $"{_textoRespuesta}{formatos}";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al generar los formatos requeridos del centro {CentroId}.", centroId);
            ToastService.Mostrar("No pudimos generar el resumen de documentación. Intenta nuevamente.", TonoToast.Error);
        }
    }

    /// <summary>
    /// Mismo tope que valida `ResponderConversacionCommand` del lado del
    /// servidor (`LimitesAdjuntosCorreo`) — comprobarlo aquí también evita
    /// que el usuario rellene el formulario entero antes de enterarse de
    /// que el conjunto de archivos no cabe.
    /// </summary>
    private async Task ManejarArchivosAdjuntosAsync(InputFileChangeEventArgs e)
    {
        _errorAdjuntos = null;
        const int maximoArchivos = 5;

        foreach (var archivo in e.GetMultipleFiles(maximoArchivos))
        {
            await using var flujo = archivo.OpenReadStream(LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes);
            using var memoria = new MemoryStream();
            await flujo.CopyToAsync(memoria);
            _adjuntosPendientes.Add(new AdjuntoParaEnviarDto(archivo.Name, archivo.ContentType, memoria.ToArray()));
        }

        if (_adjuntosPendientes.Sum(a => a.Contenido.LongLength) > LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes)
            _errorAdjuntos = "Los adjuntos superan los 3 MB en total — quita alguno antes de enviar.";
    }

    private void QuitarAdjuntoPendiente(AdjuntoParaEnviarDto adjunto)
    {
        _adjuntosPendientes.Remove(adjunto);
        if (_adjuntosPendientes.Sum(a => a.Contenido.LongLength) <= LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes)
            _errorAdjuntos = null;
    }

    /// <summary>
    /// Único punto de envío del ComposerBar — decide el Command según el
    /// canal de la conversación abierta (§ 13.3: la respuesta sale por el
    /// canal de origen). El fallback WhatsApp→correo tiene su propio método
    /// (EnviarFallbackCorreoAsync) porque es un Command distinto.
    /// </summary>
    private async Task EnviarAsync()
    {
        if (_conversacionSeleccionadaId is null || string.IsNullOrWhiteSpace(_textoRespuesta)) return;
        if (_detalle?.Canal == CanalConversacion.Correo && _errorAdjuntos is not null) return;

        _enviando = true;
        try
        {
            var resultado = _detalle?.Canal == CanalConversacion.WhatsApp
                ? await Mediator.Send(new ResponderConversacionWhatsAppCommand(_conversacionSeleccionadaId.Value, _textoRespuesta.Trim()))
                : await Mediator.Send(new ResponderConversacionCommand(
                    _conversacionSeleccionadaId.Value, _textoRespuesta, _adjuntosPendientes.Count > 0 ? _adjuntosPendientes.ToList() : null));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            _textoRespuesta = string.Empty;
            _macroSeleccionadaId = string.Empty;
            _adjuntosPendientes.Clear();
            ToastService.Mostrar("Respuesta enviada.", TonoToast.Exito);

            await CargarDetalleAsync();
            await CargarListaAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al responder en la conversación {ConversacionId}.", _conversacionSeleccionadaId);
            ToastService.Mostrar("No pudimos enviar la respuesta. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _enviando = false;
        }
    }

    /// <summary>Fallback de canal (§ 16.5): WhatsApp con la ventana de 24h cerrada continúa el MISMO hilo por correo.</summary>
    private async Task EnviarFallbackCorreoAsync()
    {
        if (_conversacionSeleccionadaId is null || string.IsNullOrWhiteSpace(_textoRespuesta) || string.IsNullOrWhiteSpace(_emailFallback))
            return;

        _enviando = true;
        try
        {
            var resultado = await Mediator.Send(new MigrarConversacionACorreoCommand(
                _conversacionSeleccionadaId.Value, _emailFallback.Trim(), _textoRespuesta));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            _textoRespuesta = string.Empty;
            _emailFallback = string.Empty;
            ToastService.Mostrar("Mensaje enviado por correo — la conversación continúa en este mismo hilo.", TonoToast.Exito);

            await CargarDetalleAsync();
            await CargarListaAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al migrar a correo la conversación {ConversacionId}.", _conversacionSeleccionadaId);
            ToastService.Mostrar("No pudimos enviar el correo. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _enviando = false;
        }
    }

    private async Task CambiarEstadoAsync(EstadoConversacion nuevoEstado)
    {
        if (_conversacionSeleccionadaId is null) return;

        _cambiandoEstado = true;
        try
        {
            var resultado = await Mediator.Send(new CambiarEstadoConversacionCommand(_conversacionSeleccionadaId.Value, nuevoEstado));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            await CargarDetalleAsync();
            await CargarListaAsync();
        }
        finally
        {
            _cambiandoEstado = false;
        }
    }

    private async Task CambiarEjecutivoAsync(string ejecutivoIdTexto)
    {
        _ejecutivoSeleccionado = ejecutivoIdTexto;
        if (_conversacionSeleccionadaId is null) return;

        _cambiandoEjecutivo = true;
        try
        {
            var ejecutivoId = Guid.TryParse(ejecutivoIdTexto, out var id) ? id : (Guid?)null;
            var resultado = await Mediator.Send(new AsignarEjecutivoConversacionCommand(_conversacionSeleccionadaId.Value, ejecutivoId));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            await CargarListaAsync();
        }
        finally
        {
            _cambiandoEjecutivo = false;
        }
    }

    private async Task AsignarClienteTriageAsync()
    {
        if (_conversacionSeleccionadaId is null || !Guid.TryParse(_clienteTriageSeleccionado, out var clienteId)) return;

        _asignandoCliente = true;
        try
        {
            var resultado = await Mediator.Send(new AsignarClienteConversacionCommand(_conversacionSeleccionadaId.Value, clienteId));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Conversación asignada al cliente.", TonoToast.Exito);
            await CargarDetalleAsync();
            await CargarListaAsync();
        }
        finally
        {
            _asignandoCliente = false;
        }
    }

    private void IrACrearVisitaDesdeSugerencia(Guid sugerenciaId) =>
        NavigationManager.NavigateTo($"/visitas?sugerenciaId={sugerenciaId}");

    private async Task DescartarSugerenciaVisitaAsync(Guid sugerenciaId)
    {
        try
        {
            var resultado = await Mediator.Send(new DescartarSugerenciaVisitaCorreoCommand(sugerenciaId));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            if (_conversacionSeleccionadaId is not null)
                await CargarDetalleAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al descartar la sugerencia de visita {SugerenciaId}.", sugerenciaId);
            ToastService.Mostrar("No pudimos descartar la sugerencia. Intenta nuevamente.", TonoToast.Error);
        }
    }

    /// <summary>
    /// A diferencia de "Crear visita" (que abre /visitas a que el Gestor
    /// complete fecha y confirme), aquí no hace falta ningún dato adicional
    /// del Gestor: Trabajador y TipoDocumento ya los resolvió la IA, y los
    /// Centros salen de las Asignaciones activas del propio Trabajador — el
    /// clic en el botón ya es la confirmación explícita exigida.
    /// </summary>
    private async Task GenerarGestionesDesdeSugerenciaAsync(Guid sugerenciaId, Guid trabajadorId, Guid tipoDocumentoId)
    {
        try
        {
            var resultado = await Mediator.Send(new CrearGestionesParaTrabajadorCommand(trabajadorId, tipoDocumentoId, sugerenciaId));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar($"Se generaron {resultado.Valor.Creadas} gestión(es).", TonoToast.Exito);

            if (_conversacionSeleccionadaId is not null)
                await CargarDetalleAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al generar gestiones desde la sugerencia {SugerenciaId}.", sugerenciaId);
            ToastService.Mostrar("No pudimos generar las gestiones. Intenta nuevamente.", TonoToast.Error);
        }
    }

    private async Task DescartarSugerenciaGestionAsync(Guid sugerenciaId)
    {
        try
        {
            var resultado = await Mediator.Send(new DescartarSugerenciaGestionCorreoCommand(sugerenciaId));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            if (_conversacionSeleccionadaId is not null)
                await CargarDetalleAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al descartar la sugerencia de gestión {SugerenciaId}.", sugerenciaId);
            ToastService.Mostrar("No pudimos descartar la sugerencia. Intenta nuevamente.", TonoToast.Error);
        }
    }

    /// <summary>
    /// Abre el flujo "Actualizar documentación desde conversación"
    /// (docs/COMUNICACIONES.md § 12.7) para un adjunto concreto. Carga los
    /// selectores y lanza la detección en el mismo gesto — si
    /// ExtraccionDocumentoAdjuntoOptions está apagado (por defecto), la
    /// detección vuelve vacía y el gestor rellena los campos a mano.
    /// </summary>
    private async Task AbrirActualizarDocumentoAsync(Guid adjuntoId)
    {
        _adjuntoParaActualizarDocumentoId = adjuntoId;
        _modalActualizarDocumentoVisible = true;
        _errorActualizarDocumento = null;
        _tipoDocumentoIdFormulario = string.Empty;
        _propietarioDocumentoFormulario = nameof(AmbitoAplicacion.Trabajador);
        _trabajadorDocumentoIdFormulario = string.Empty;
        _empresaDocumentoIdFormulario = string.Empty;
        _fechaEmisionDocumentoFormulario = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
        _fechaVencimientoDocumentoFormulario = string.Empty;
        _comentariosDocumentoFormulario = string.Empty;
        _confianzaDeteccionDocumento = 0;

        _detectandoDocumento = true;
        StateHasChanged();

        try
        {
            _tiposDocumentoSelector = await Mediator.Send(new ObtenerTiposDocumentoQuery());
            _trabajadoresSelector = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());
            _empresasSelector = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery(_detalle?.ClienteId));

            var deteccion = await Mediator.Send(new DetectarActualizacionDocumentoDesdeAdjuntoQuery(adjuntoId));
            if (deteccion.EsFallido)
            {
                _errorActualizarDocumento = deteccion.Error.Mensaje;
                return;
            }

            var valor = deteccion.Valor;
            _confianzaDeteccionDocumento = valor.ConfianzaGeneral;
            if (valor.TipoDocumentoId is { } tipoDocumentoId) _tipoDocumentoIdFormulario = tipoDocumentoId.ToString();
            if (valor.Ambito == AmbitoAplicacion.Empresa) _propietarioDocumentoFormulario = nameof(AmbitoAplicacion.Empresa);
            if (valor.TrabajadorId is { } trabajadorId) _trabajadorDocumentoIdFormulario = trabajadorId.ToString();
            if (valor.EmpresaId is { } empresaId) _empresaDocumentoIdFormulario = empresaId.ToString();
            if (valor.FechaEmision is { } fechaEmision) _fechaEmisionDocumentoFormulario = fechaEmision.ToString("yyyy-MM-dd");
            if (valor.FechaVencimiento is { } fechaVencimiento) _fechaVencimientoDocumentoFormulario = fechaVencimiento.ToString("yyyy-MM-dd");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al detectar la actualización de documentación del adjunto {AdjuntoId}.", adjuntoId);
            _errorActualizarDocumento = "No pudimos analizar el adjunto. Puedes rellenar los campos a mano.";
        }
        finally
        {
            _detectandoDocumento = false;
            StateHasChanged();
        }
    }

    private void CerrarModalActualizarDocumento() => _modalActualizarDocumentoVisible = false;

    private async Task AplicarActualizacionDocumentoAsync()
    {
        if (!Guid.TryParse(_tipoDocumentoIdFormulario, out var tipoDocumentoId))
        {
            _errorActualizarDocumento = "Selecciona un tipo de documento.";
            return;
        }

        if (!DateOnly.TryParse(_fechaEmisionDocumentoFormulario, out var fechaEmision))
        {
            _errorActualizarDocumento = "Indica la fecha de emisión.";
            return;
        }

        var esTrabajador = _propietarioDocumentoFormulario == nameof(AmbitoAplicacion.Trabajador);
        Guid? trabajadorId = esTrabajador && Guid.TryParse(_trabajadorDocumentoIdFormulario, out var idTrabajador) ? idTrabajador : null;
        Guid? empresaId = !esTrabajador && Guid.TryParse(_empresaDocumentoIdFormulario, out var idEmpresa) ? idEmpresa : null;

        if (trabajadorId is null && empresaId is null)
        {
            _errorActualizarDocumento = esTrabajador ? "Selecciona un trabajador." : "Selecciona una empresa.";
            return;
        }

        DateOnly? fechaVencimientoManual = DateOnly.TryParse(_fechaVencimientoDocumentoFormulario, out var fv) ? fv : null;

        _aplicandoDocumento = true;
        _errorActualizarDocumento = null;
        try
        {
            var resultado = await Mediator.Send(new ActualizarDocumentoDesdeAdjuntoCommand(
                _adjuntoParaActualizarDocumentoId, tipoDocumentoId, trabajadorId, empresaId, fechaEmision, fechaVencimientoManual,
                string.IsNullOrWhiteSpace(_comentariosDocumentoFormulario) ? null : _comentariosDocumentoFormulario));

            if (resultado.EsFallido)
            {
                _errorActualizarDocumento = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Documentación actualizada.", TonoToast.Exito);
            _modalActualizarDocumentoVisible = false;
            await CargarDetalleAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al actualizar la documentación desde el adjunto {AdjuntoId}.", _adjuntoParaActualizarDocumentoId);
            _errorActualizarDocumento = "No pudimos aplicar la actualización. Intenta nuevamente.";
        }
        finally
        {
            _aplicandoDocumento = false;
        }
    }

    private static TonoBadge TonoBadgeDeEstado(EstadoConversacion estado) => estado switch
    {
        EstadoConversacion.Abierta => TonoBadge.Info,
        EstadoConversacion.Pendiente => TonoBadge.Info,
        _ => TonoBadge.Neutro
    };

    /// <summary>Dos letras de la razón social para el avatar de la columna de contexto — mismo criterio que UnifiedTimeline.ObtenerIniciales.</summary>
    private static string ObtenerInicialesCliente(string razonSocial)
    {
        var palabras = razonSocial.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return palabras.Length >= 2
            ? $"{palabras[0][0]}{palabras[1][0]}".ToUpperInvariant()
            : razonSocial.Length >= 2 ? razonSocial[..2].ToUpperInvariant() : razonSocial.ToUpperInvariant();
    }
}
