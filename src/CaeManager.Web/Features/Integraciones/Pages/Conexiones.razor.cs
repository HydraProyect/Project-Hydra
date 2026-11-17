using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Integraciones.Commands.ActualizarLineaWhatsApp;
using CaeManager.Application.Integraciones.Commands.CrearLineaWhatsApp;
using CaeManager.Application.Integraciones.Commands.DesconectarBuzon;
using CaeManager.Application.Integraciones.Commands.ReconocerAvisoHueco;
using CaeManager.Application.Integraciones.Commands.RepararMarcaHueco;
using CaeManager.Application.Integraciones.Commands.ReactivarConexion;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Integraciones.Queries.ObtenerLineasWhatsApp;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Infrastructure.Integraciones;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.Integraciones.Pages;

/// <summary>Administración de conexiones de Microsoft 365 (P3-33) y líneas WhatsApp — solo Administrador.</summary>
public partial class Conexiones : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase, IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<Conexiones> Logger { get; set; } = default!;
    [Inject] private DirectorioUsuariosTenant DirectorioUsuarios { get; set; } = default!;

    [Inject] private IOptions<Microsoft365GraphOptions> OpcionesMicrosoft365 { get; set; } = default!;

    [SupplyParameterFromQuery] public bool? Conectado { get; set; }
    [SupplyParameterFromQuery] public string? Error { get; set; }

    private IReadOnlyList<ClienteSelectorDto> _clientes = [];
    private IReadOnlyList<ConexionIntegracionListaDto> _conexiones = [];
    private IReadOnlyList<LineaWhatsAppListaDto> _lineas = [];
    private IReadOnlyList<GestorParaElegir> _gestores = [];
    private bool _cargando = true;

    /// <summary>La última carga del listado falló: no se muestra ningún listado ni acción, solo el aviso.</summary>
    private bool _cargaFallida;

    // --- Modal de línea WhatsApp (alta/edición) ---
    private bool _modalLineaVisible;
    private bool _guardandoLinea;
    private LineaWhatsAppListaDto? _lineaEnEdicion;
    private string _lineaNombre = string.Empty;
    private string _lineaNumero = string.Empty;
    private string _lineaPhoneNumberId = string.Empty;
    private string _lineaWabaId = string.Empty;
    private string _lineaToken = string.Empty;
    private ModoAsignacionLinea _lineaModo = ModoAsignacionLinea.GestorFijo;
    private Guid? _lineaComercialId;
    private readonly HashSet<Guid> _lineaMiembros = [];
    private Guid? _lineaEmpresaId;
    private string _lineaMensajeAutoTriage = string.Empty;
    private long _generacionOperacionLinea;

    // «¿Descartar cambios?» al cerrar el modal con la X, Escape o el fondo (S3): instantánea al abrirlo.
    private readonly InstantaneaFormulario _instantaneaLinea = new();

    private bool HayCambiosEnLinea => _modalLineaVisible && _instantaneaLinea.Difiere(ValoresDeLaLinea());

    /// <summary>Rechazo del servidor (o fallo inesperado) al guardar la línea: va en el aviso fijo del ModalFormulario, junto al formulario que hay que corregir (antes, un toast).</summary>
    private string? _errorLinea;

    private object?[] ValoresDeLaLinea() =>
    [
        _lineaNombre, _lineaNumero, _lineaPhoneNumberId, _lineaWabaId, _lineaToken, _lineaModo,
        _lineaComercialId, _lineaMiembros, _lineaEmpresaId, _lineaMensajeAutoTriage,
    ];

    private ConexionIntegracionListaDto? _conexionADesconectar;
    private bool _desconectando;
    private ConexionIntegracionListaDto? _avisoHuecoAConfirmar;
    private bool _confirmandoAvisoHueco;
    private long _generacionDesconexion;
    private Guid? _procesandoId;
    private readonly CancellationTokenSource _cancelacionCarga = new();
    private CancellationTokenSource? _cicloCarga;
    private long _generacionCarga;
    private long _generacionInicializacion;
    private bool _dispuesto;

    // Las reglas del propietario y la URL de conectar son las mismas que usa el cajón «Añadir buzón» de Buzones.
    private readonly SeleccionPropietarioBuzon _propietario = new();

    private string UrlConectar => _propietario.UrlConectar;

    /// <summary>
    /// Sin la URL pública de la aplicación el endpoint de conectar no puede arrancar el flujo OAuth (no hay redirect_uri): no
    /// se ofrece el enlace y la página lo explica. Es la misma condición que comprueba el endpoint.
    /// </summary>
    private bool ConexionDisponible => !string.IsNullOrWhiteSpace(OpcionesMicrosoft365.Value.UrlPublicaBase);

    /// <summary>Los campos del propietario avisan de cada elección: se repinta el enlace de conectar, que depende de ella.</summary>
    private void Repintar()
    {
    }

    /// <summary>Se conservan para los tests de la elección: la regla vive en <see cref="SeleccionPropietarioBuzon"/>.</summary>
    private void SeleccionarGestorPropietario(string valor) => _propietario.SeleccionarGestorPropietario(valor);

    protected override async Task OnInitializedAsync()
    {
        if (Conectado == true)
            ToastService.Mostrar("Buzón conectado correctamente.", TonoToast.Exito);
        else if (!string.IsNullOrWhiteSpace(Error))
            // Cancelar en Microsoft es una decisión de la persona, no un fallo: tono
            // informativo. El callback solo usa «cancelado» para access_denied.
            ToastService.Mostrar(MensajeError(Error), Error == CodigoCancelado ? TonoToast.Info : TonoToast.Error);

        var generacion = Interlocked.Increment(ref _generacionInicializacion);
        var token = _cancelacionCarga.Token;
        try
        {
            var clientes = await Mediator.Send(new ObtenerClientesParaSelectorQuery(), token);

            var gestores = await DirectorioUsuarios.ObtenerVisiblesEnRolAsync(Roles.GestorCae, token);
            if (generacion != _generacionInicializacion || token.IsCancellationRequested) return;
            _clientes = clientes;
            _gestores = gestores.Select(u => new GestorParaElegir(u.Id, u.NombreCompleto)).ToList();

            await CargarConexionesAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (generacion == _generacionInicializacion)
            {
                // También si falla antes de llegar al listado: «Sin conexiones» diría algo que no sabemos.
                _cargaFallida = true;
                Logger.LogError(ex, "Error al cargar las conexiones de integración.");
                ToastService.Mostrar("No pudimos cargar las conexiones.", TonoToast.Error);
            }
        }
        finally
        {
            if (generacion == _generacionInicializacion && !_dispuesto)
                _cargando = false;
        }
    }

    private async Task CargarConexionesAsync(CancellationToken cancellationToken = default)
    {
        var generacion = Interlocked.Increment(ref _generacionCarga);
        var ciclo = CancellationTokenSource.CreateLinkedTokenSource(_cancelacionCarga.Token, cancellationToken);
        var cicloAnterior = Interlocked.Exchange(ref _cicloCarga, ciclo);
        cicloAnterior?.Cancel();
        var token = ciclo.Token;
        try
        {
            var conexiones = await Mediator.Send(new ObtenerConexionesIntegracionQuery(), token);
            var lineas = await Mediator.Send(new ObtenerLineasWhatsAppQuery(), token);
            if (generacion != _generacionCarga || token.IsCancellationRequested) return;
            _conexiones = conexiones;
            _lineas = lineas;
            _cargaFallida = false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception) when (generacion == _generacionCarga)
        {
            // Una carga que falla no deja a la vista el listado anterior: sus estados pueden estar obsoletos (una
            // Habilitada que ya está Deshabilitada) y sus acciones operarían sobre ellos. Se vacía y la página muestra el
            // aviso en su lugar, sin acciones, hasta la próxima carga que sí termine (Codex, ronda 19, hallazgo medio).
            // La excepción sigue: quien llama decide cómo avisar.
            _conexiones = [];
            _lineas = [];
            _cargaFallida = true;
            throw;
        }
        finally
        {
            Interlocked.CompareExchange(ref _cicloCarga, null, ciclo);
            ciclo.Dispose();
        }
    }

    private string DescribirPropietario(ConexionIntegracionListaDto conexion) => conexion switch
    {
        { GestorPropietarioId: { } gestorId } => $"Personal de {_gestores.FirstOrDefault(g => g.Id == gestorId)?.NombreCompleto ?? "—"}",
        { ClienteNombre: { } clienteNombre } => clienteNombre,
        _ => "Organización propia"
    };

    private string DescribirAsignacion(LineaWhatsAppListaDto linea) => linea.Modo switch
    {
        ModoAsignacionLinea.GestorFijo =>
            $"Gestor CAE fijo: {_gestores.FirstOrDefault(g => g.Id == linea.ComercialAsignadoId)?.NombreCompleto ?? "—"}",
        _ => $"Reparto equitativo ({linea.MiembrosPool.Count} Gestores CAE)",
    };

    private void AbrirAltaLinea()
    {
        if (_guardandoLinea) return;
        _lineaEnEdicion = null;
        _lineaNombre = _lineaNumero = _lineaPhoneNumberId = _lineaWabaId = _lineaToken = string.Empty;
        _lineaModo = ModoAsignacionLinea.GestorFijo;
        _lineaComercialId = null;
        _lineaMiembros.Clear();
        _lineaEmpresaId = null;
        _lineaMensajeAutoTriage = string.Empty;
        _errorLinea = null;
        _modalLineaVisible = true;
        _instantaneaLinea.Fijar(ValoresDeLaLinea());
    }

    private void AbrirEdicionLinea(LineaWhatsAppListaDto linea)
    {
        if (_guardandoLinea) return;
        _lineaEnEdicion = linea;
        _lineaToken = string.Empty;
        _lineaModo = linea.Modo;
        _lineaComercialId = linea.ComercialAsignadoId;
        _lineaMiembros.Clear();
        foreach (var miembro in linea.MiembrosPool) _lineaMiembros.Add(miembro);
        _lineaMensajeAutoTriage = linea.MensajeAutoTriage ?? string.Empty;
        _errorLinea = null;
        _modalLineaVisible = true;
        _instantaneaLinea.Fijar(ValoresDeLaLinea());
    }

    private Task CerrarModalLinea(bool visible)
    {
        if (visible || _guardandoLinea) return Task.CompletedTask;
        _modalLineaVisible = false;
        _lineaEnEdicion = null;
        return Task.CompletedTask;
    }

    private void AlternarMiembroPool(Guid usuarioId, bool marcado)
    {
        _errorLinea = null;
        if (marcado) _lineaMiembros.Add(usuarioId);
        else _lineaMiembros.Remove(usuarioId);
    }

    private async Task GuardarLineaAsync()
    {
        if (_guardandoLinea) return;
        _errorLinea = null;
        _guardandoLinea = true;
        var generacion = Interlocked.Increment(ref _generacionOperacionLinea);
        var lineaEnEdicion = _lineaEnEdicion;
        StateHasChanged();

        try
        {
            var mensajeAutoTriage = string.IsNullOrWhiteSpace(_lineaMensajeAutoTriage) ? null : _lineaMensajeAutoTriage;
            var resultadoError = lineaEnEdicion is null
                ? (await Mediator.Send(new CrearLineaWhatsAppCommand(
                    _lineaNombre, _lineaNumero, _lineaPhoneNumberId, _lineaWabaId, _lineaToken, _lineaModo,
                    _lineaComercialId, _lineaMiembros.ToList(), _lineaEmpresaId, mensajeAutoTriage)))
                    is { EsFallido: true } fallosAlta ? fallosAlta.Error : null
                : (await Mediator.Send(new ActualizarLineaWhatsAppCommand(
                    lineaEnEdicion.LineaId, lineaEnEdicion.Version,
                    string.IsNullOrWhiteSpace(_lineaToken) ? null : _lineaToken, _lineaModo,
                    _lineaComercialId, _lineaMiembros.ToList(), mensajeAutoTriage)))
                    is { EsFallido: true } fallosEdicion ? fallosEdicion.Error : null;

            if (generacion != _generacionOperacionLinea) return;

            if (resultadoError is not null)
            {
                _errorLinea = resultadoError.Mensaje;
                return;
            }

            ToastService.Mostrar(lineaEnEdicion is null ? "Línea creada." : "Línea actualizada.", TonoToast.Exito);
            _modalLineaVisible = false;
            _lineaEnEdicion = null;
            await CargarConexionesAsync();
        }
        catch (Exception ex)
        {
            if (generacion == _generacionOperacionLinea)
            {
                Logger.LogError(ex, "Error al guardar la línea de WhatsApp.");
                _errorLinea = "No pudimos guardar la línea.";
            }
        }
        finally
        {
            if (generacion == _generacionOperacionLinea)
                _guardandoLinea = false;
        }
    }

    private void AbrirDesconexion(ConexionIntegracionListaDto conexion)
    {
        if (_procesandoId is not null || _desconectando) return;
        _conexionADesconectar = conexion;
    }

    private Task CerrarModalDesconexion() => CerrarModalDesconexion(false);

    private Task CerrarModalDesconexion(bool visible)
    {
        if (visible || _desconectando) return Task.CompletedTask;
        _conexionADesconectar = null;
        return Task.CompletedTask;
    }

    private async Task DesconectarAsync()
    {
        if (_conexionADesconectar is null || _desconectando) return;

        var conexion = _conexionADesconectar;
        _desconectando = true;
        var generacion = Interlocked.Increment(ref _generacionDesconexion);
        _procesandoId = conexion.Id;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new DesconectarBuzonCommand(conexion.Id));

            if (generacion != _generacionDesconexion || _conexionADesconectar?.Id != conexion.Id) return;

            if (resultado.EsFallido)
            {
                // Un fallo no implica que nada cambiara: con ConexionIntegracion.MarcaHuecoNoGuardada la conexión ya
                // está Deshabilitada. Se recarga para mostrar el estado real; el aviso de error se conserva y el modal
                // sigue abierto (Codex, ronda 17). Si se cierra, la fila de la Deshabilitada muestra «Hueco sin registrar»
                // con Reparar, que sale de lo guardado en la conexión (Codex, ronda 18). Una recarga que falla no tapa el
                // error: avisa aparte (RecargarSinPropagarAsync).
                ToastService.MostrarError(resultado.Error);
                await RecargarSinPropagarAsync();
                return;
            }

            ToastService.Mostrar("Buzón desconectado.", TonoToast.Exito);
            _conexionADesconectar = null;
            await CargarConexionesAsync();
        }
        finally
        {
            if (generacion == _generacionDesconexion)
            {
                _desconectando = false;
                _procesandoId = null;
            }
        }
    }

    /// <summary>
    /// Recarga tras un fallo de Desconectar o tras Reparar: las consultas pueden fallar también (la misma base que no guardó la
    /// marca). La excepción no sale del manejador, el error del comando sigue visible y un aviso aparte dice que el listado
    /// no se pudo actualizar (Codex, ronda 18, hallazgo medio). El listado anterior no queda a la vista: la carga fallida
    /// lo vacía y la página muestra el aviso sin acciones (Codex, ronda 19).
    /// </summary>
    private async Task RecargarSinPropagarAsync()
    {
        try
        {
            await CargarConexionesAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al recargar las conexiones tras un fallo.");
            ToastService.Mostrar(Textos["AvisoRecargaFallida"], TonoToast.Error);
        }
    }

    /// <summary>
    /// Confirmar en la fila (issue #2124): solo con el aviso «posible correo no recuperado» y en el estado en que el servidor
    /// lo acepta, Habilitada y sin marca del hueco por registrar (<see cref="ReconocerAvisoHuecoCommandHandler"/>). Quién
    /// puede lo fija la página, que es solo de Administrador, y lo vuelve a comprobar el handler. Un buzón personal de un
    /// Gestor CAE no lo ofrece: el handler solo deja verlo a su dueño (la misma visibilidad que Desconectar), así que el
    /// Administrador siempre recibiría «no encontrada».
    /// </summary>
    private static bool PuedeConfirmarAvisoHueco(ConexionIntegracionListaDto conexion) =>
        conexion.AvisoHuecoReconocible && conexion.GestorPropietarioId is null;

    /// <summary>
    /// El aviso en una línea de la fila: el título que Buzón pinta en negrita y su detalle, unidos por el formato del
    /// recurso, con la variante que nombra Confirmar solo si la fila lo ofrece.
    /// </summary>
    private string TextoAvisoCorreoNoRecuperado(ConexionIntegracionListaDto conexion, DateTime noRecuperadoDesdeUtc) =>
        Textos["AvisoCorreoNoRecuperadoEnLinea",
            Textos["AvisoCorreoNoRecuperadoTitulo", noRecuperadoDesdeUtc.EnHoraPeninsular().ToString("dd/MM/yyyy HH:mm")].Value,
            Textos[PuedeConfirmarAvisoHueco(conexion) ? "AvisoCorreoNoRecuperadoConfirmable" : "AvisoCorreoNoRecuperado"].Value];

    private void AbrirConfirmacionAvisoHueco(ConexionIntegracionListaDto conexion)
    {
        if (_procesandoId is not null || _desconectando || _confirmandoAvisoHueco) return;
        _avisoHuecoAConfirmar = conexion;
    }

    private void CerrarConfirmacionAvisoHueco()
    {
        if (!_confirmandoAvisoHueco) _avisoHuecoAConfirmar = null;
    }

    /// <summary>
    /// Envía la fecha del aviso que se ve en el diálogo: si la marca ya es otra, el servidor no la reconoce. Éxito o fallo, el
    /// diálogo se cierra y el listado se recarga para mostrar el estado real.
    /// </summary>
    private async Task ConfirmarAvisoHuecoAsync()
    {
        if (_avisoHuecoAConfirmar is not { CorreoNoRecuperadoDesdeUtc: { } marca } conexion || _confirmandoAvisoHueco) return;

        _confirmandoAvisoHueco = true;
        _procesandoId = conexion.Id;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ReconocerAvisoHuecoCommand(conexion.Id, marca));
            if (resultado.EsFallido)
                ToastService.MostrarError(resultado.Error);
            else
                ToastService.Mostrar(Textos["AvisoHuecoConfirmado"], TonoToast.Exito);

            _avisoHuecoAConfirmar = null;
            await RecargarSinPropagarAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al confirmar el aviso del hueco de la conexión {ConexionId}.", conexion.Id);
            ToastService.Mostrar(Textos["ErrorConfirmarAvisoHueco"], TonoToast.Error);
        }
        finally
        {
            _confirmandoAvisoHueco = false;
            _procesandoId = null;
        }
    }

    /// <summary>
    /// Reparar en la fila de una conexión Deshabilitada con la marca del hueco por registrar: RepararMarcaHuecoCommand, que
    /// registra lo guardado en la conexión solo si sigue Deshabilitada y nunca cambia su estado ni llama a Graph. No es
    /// Desconectar: si otra sesión la reactivó después de cargar la fila, Desconectar la habría desconectado (Codex, ronda
    /// 19, hallazgo alto). Sin modal: no cambia nada más que la marca. Ante un fallo (también si la conexión ya no está
    /// Deshabilitada) se recarga el listado para mostrar su estado real.
    /// </summary>
    private async Task RepararHuecoAsync(ConexionIntegracionListaDto conexion)
    {
        if (_procesandoId is not null || _desconectando) return;
        _procesandoId = conexion.Id;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new RepararMarcaHuecoCommand(conexion.Id));
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                await RecargarSinPropagarAsync();
                return;
            }

            // La reparación ya quedó guardada: si falla la recarga, se avisa de la recarga y no de la reparación.
            ToastService.Mostrar(Textos["HuecoReparado"], TonoToast.Exito);
            await RecargarSinPropagarAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al reparar la marca del hueco de la conexión {ConexionId}.", conexion.Id);
            ToastService.Mostrar(Textos["ErrorRepararHueco"], TonoToast.Error);
        }
        finally
        {
            _procesandoId = null;
        }
    }

    private async Task ReactivarAsync(ConexionIntegracionListaDto conexion)
    {
        if (_procesandoId is not null) return;
        _procesandoId = conexion.Id;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ReactivarConexionCommand(conexion.Id));
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar("Conexión reactivada.", TonoToast.Exito);
            await CargarConexionesAsync();
        }
        finally
        {
            _procesandoId = null;
        }
    }

    private static TonoBadge ObtenerTonoEstado(EstadoConexionIntegracion estado) => estado switch
    {
        EstadoConexionIntegracion.Habilitada => TonoBadge.Exito,
        EstadoConexionIntegracion.ConError => TonoBadge.Peligro,
        _ => TonoBadge.Neutro
    };

    private static string ObtenerEtiquetaEstado(EstadoConexionIntegracion estado) => estado switch
    {
        EstadoConexionIntegracion.Habilitada => "Habilitada",
        EstadoConexionIntegracion.ConError => "Con error",
        _ => "Deshabilitada"
    };

    private const string CodigoCancelado = "cancelado";

    // Códigos cerrados de «?error=» (los pone ConectarMicrosoft365Endpoints): un texto fijo por código, y uno desconocido o
    // manipulado cae en el genérico; nada de la query ni de Microsoft se refleja. «solicitud» (state caducado o de otra
    // cuenta) también cae en el genérico.
    private string MensajeError(string codigo) => codigo switch
    {
        CodigoCancelado => "Conexión cancelada. No se ha conectado ningún buzón.",
        "autenticacion" => Textos["ErrorAutenticacion"],
        "configuracion" => Textos["ErrorConfiguracionIncompleta"],
        "canje-tokens" => Textos["ErrorCanjeTokens"],
        "permisos-microsoft" => Textos["ErrorPermisosMicrosoft"],
        "sin-buzon-exchange" => Textos["ErrorSinBuzonExchange"],
        "error-microsoft" => Textos["ErrorMicrosoft"],
        "suscripcion" => "El buzón se autenticó pero no pudimos activar las notificaciones. Inténtalo de nuevo.",
        "buzon-compartido" => Textos["ErrorBuzonCompartidoInvalido"],
        "sin-acceso-compartido" => Textos["ErrorSinAccesoCompartido"],
        "ya-conectado" => Textos["ErrorBuzonYaConectado"],
        "compartidos-en-pausa" => Textos["ErrorAltaCompartidaEnPausa"],
        "solo-administrador" => Textos["ErrorSoloAdministrador"],
        "url-publica" => Textos["AvisoConexionNoDisponible"],
        _ => "No pudimos completar la conexión."
    };

    public void Dispose()
    {
        _dispuesto = true;
        _cancelacionCarga.Cancel();
        Interlocked.Exchange(ref _cicloCarga, null)?.Cancel();
        _cancelacionCarga.Dispose();
    }

    // ---- Atajos de lista j/k (I-13 c de AUDITORIA-USUARIO-AVANZADO-POST-GEN2, decisión del
    // propietario 2026-09-17, opción C) ----

    /// <summary>Tabla que recorren "j"/"k". Una sola instancia de AtajosListaTeclado para toda la
    /// página (dos no conviven, medido por Codex): el destino de cada tecla depende de cuál de las
    /// dos tablas tiene el foco, no de dónde esté el cursor del ratón.</summary>
    private enum TablaConexiones { Buzones, Lineas }

    /// <summary>Sin foco previo, la tabla activa es Buzones de Microsoft 365 — la primera de la
    /// página, ninguna decisión que tomar al entrar.</summary>
    private TablaConexiones _tablaActiva = TablaConexiones.Buzones;
    private Guid? _idBuzonEnfocado;
    private Guid? _idLineaEnfocado;

    /// <summary>Cambia la tabla activa: la dispara el clic en una tabla o el foco real de teclado
    /// (Tab) aterrizando en cualquiera de sus controles — botones de fila incluidos.</summary>
    private void ActivarTabla(TablaConexiones tabla) => _tablaActiva = tabla;

    private string ClaseFilaBuzon(Guid id) =>
        _tablaActiva == TablaConexiones.Buzones && _idBuzonEnfocado == id ? "fila-enfocada" : string.Empty;

    private string ClaseFilaLinea(Guid id) =>
        _tablaActiva == TablaConexiones.Lineas && _idLineaEnfocado == id ? "fila-enfocada" : string.Empty;

    /// <summary>
    /// Solo "j"/"k", y solo sobre la tabla con foco. "x" y "Enter" sobre una fila no hacen nada —
    /// deliberado: no hay selección múltiple en ninguna de las dos tablas, y Enter solo podría
    /// desconectar, reactivar o editar, y un atajo de fila no dispara escrituras. El Enter nativo
    /// sobre un botón (Reactivar/Desconectar/Editar) lo sigue respetando atajos-lista.js sin pasar
    /// por aquí.
    /// </summary>
    private Task ManejarAtajoAsync(string tecla)
    {
        if (tecla != "j" && tecla != "k") return Task.CompletedTask;

        if (_tablaActiva == TablaConexiones.Buzones)
            _idBuzonEnfocado = SiguienteId(_conexiones.Select(c => c.Id).ToList(), _idBuzonEnfocado, tecla);
        else
            _idLineaEnfocado = SiguienteId(_lineas.Select(l => l.LineaId).ToList(), _idLineaEnfocado, tecla);

        StateHasChanged();
        return Task.CompletedTask;
    }

    private static Guid? SiguienteId(IReadOnlyList<Guid> ids, Guid? actual, string tecla)
    {
        if (ids.Count == 0) return null;

        var indice = -1;
        if (actual is not null)
            for (var i = 0; i < ids.Count; i++)
                if (ids[i] == actual) indice = i;

        return tecla == "j"
            ? ids[Math.Min(indice + 1, ids.Count - 1)]
            : ids[indice <= 0 ? 0 : indice - 1];
    }
}
