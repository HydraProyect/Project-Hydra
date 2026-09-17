using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Integraciones.Commands.ActualizarLineaWhatsApp;
using CaeManager.Application.Integraciones.Commands.CrearLineaWhatsApp;
using CaeManager.Application.Integraciones.Commands.DesconectarBuzon;
using CaeManager.Application.Integraciones.Commands.ReactivarConexion;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Integraciones.Queries.ObtenerLineasWhatsApp;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Integraciones.Pages;

/// <summary>Administración de conexiones de Microsoft 365 (P3-33) y líneas WhatsApp — solo Administrador.</summary>
public partial class Conexiones : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase, IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<Conexiones> Logger { get; set; } = default!;
    [Inject] private DirectorioUsuariosTenant DirectorioUsuarios { get; set; } = default!;

    [SupplyParameterFromQuery] public bool? Conectado { get; set; }
    [SupplyParameterFromQuery] public string? Error { get; set; }

    private record GestorSelectorDto(Guid Id, string NombreCompleto);

    private IReadOnlyList<ClienteSelectorDto> _clientes = [];
    private IReadOnlyList<ConexionIntegracionListaDto> _conexiones = [];
    private IReadOnlyList<LineaWhatsAppListaDto> _lineas = [];
    private IReadOnlyList<GestorSelectorDto> _gestores = [];
    private bool _cargando = true;
    private Guid? _clienteSeleccionadoId;
    private Guid? _gestorPropietarioSeleccionadoId;

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
    private Guid? _lineaClienteId;
    private string _lineaMensajeAutoTriage = string.Empty;
    private long _generacionOperacionLinea;

    private ConexionIntegracionListaDto? _conexionADesconectar;
    private bool _desconectando;
    private long _generacionDesconexion;
    private Guid? _procesandoId;
    private readonly CancellationTokenSource _cancelacionCarga = new();
    private CancellationTokenSource? _cicloCarga;
    private long _generacionCarga;
    private long _generacionInicializacion;
    private bool _dispuesto;

    private string UrlConectar
    {
        get
        {
            if (_gestorPropietarioSeleccionadoId is { } gestorId)
                return $"/integraciones/conectar-microsoft365?gestorPropietarioId={gestorId}";
            if (_clienteSeleccionadoId is { } clienteId)
                return $"/integraciones/conectar-microsoft365?clienteId={clienteId}";
            return "/integraciones/conectar-microsoft365";
        }
    }

    /// <summary>Cliente y buzón personal son mutuamente excluyentes — elegir uno limpia el otro.</summary>
    private void SeleccionarCliente(string valor)
    {
        _clienteSeleccionadoId = Guid.TryParse(valor, out var id) ? id : null;
        if (_clienteSeleccionadoId is not null) _gestorPropietarioSeleccionadoId = null;
    }

    private void SeleccionarGestorPropietario(string valor)
    {
        _gestorPropietarioSeleccionadoId = Guid.TryParse(valor, out var id) ? id : null;
        if (_gestorPropietarioSeleccionadoId is not null) _clienteSeleccionadoId = null;
    }

    protected override async Task OnInitializedAsync()
    {
        if (Conectado == true)
            ToastService.Mostrar("Buzón conectado correctamente.", TonoToast.Exito);
        else if (!string.IsNullOrWhiteSpace(Error))
            ToastService.Mostrar(MensajeError(Error), TonoToast.Error);

        var generacion = Interlocked.Increment(ref _generacionInicializacion);
        var token = _cancelacionCarga.Token;
        try
        {
            var clientes = await Mediator.Send(new ObtenerClientesParaSelectorQuery(), token);

            var gestores = await DirectorioUsuarios.ObtenerVisiblesEnRolAsync(Roles.GestorCae, token);
            if (generacion != _generacionInicializacion || token.IsCancellationRequested) return;
            _clientes = clientes;
            _gestores = gestores.Select(u => new GestorSelectorDto(u.Id, u.NombreCompleto)).ToList();

            await CargarConexionesAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (generacion == _generacionInicializacion)
            {
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
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
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
        _ => $"Pool inbound ({linea.MiembrosPool.Count} gestores CAE)",
    };

    private void AbrirAltaLinea()
    {
        if (_guardandoLinea) return;
        _lineaEnEdicion = null;
        _lineaNombre = _lineaNumero = _lineaPhoneNumberId = _lineaWabaId = _lineaToken = string.Empty;
        _lineaModo = ModoAsignacionLinea.GestorFijo;
        _lineaComercialId = null;
        _lineaMiembros.Clear();
        _lineaClienteId = null;
        _lineaMensajeAutoTriage = string.Empty;
        _modalLineaVisible = true;
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
        _modalLineaVisible = true;
    }

    private Task CerrarModalLinea() => CerrarModalLinea(false);

    private Task CerrarModalLinea(bool visible)
    {
        if (visible || _guardandoLinea) return Task.CompletedTask;
        _modalLineaVisible = false;
        _lineaEnEdicion = null;
        return Task.CompletedTask;
    }

    private void AlternarMiembroPool(Guid usuarioId, bool marcado)
    {
        if (marcado) _lineaMiembros.Add(usuarioId);
        else _lineaMiembros.Remove(usuarioId);
    }

    private async Task GuardarLineaAsync()
    {
        if (_guardandoLinea) return;
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
                    _lineaComercialId, _lineaMiembros.ToList(), _lineaClienteId, mensajeAutoTriage)))
                    is { EsFallido: true } fallosAlta ? fallosAlta.Error : null
                : (await Mediator.Send(new ActualizarLineaWhatsAppCommand(
                    lineaEnEdicion.LineaId, lineaEnEdicion.Version,
                    string.IsNullOrWhiteSpace(_lineaToken) ? null : _lineaToken, _lineaModo,
                    _lineaComercialId, _lineaMiembros.ToList(), mensajeAutoTriage)))
                    is { EsFallido: true } fallosEdicion ? fallosEdicion.Error : null;

            if (generacion != _generacionOperacionLinea) return;

            if (resultadoError is not null)
            {
                ToastService.Mostrar(resultadoError.Mensaje, TonoToast.Error);
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
                ToastService.Mostrar("No pudimos guardar la línea.", TonoToast.Error);
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
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
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
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
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

    private static string MensajeError(string codigo) => codigo switch
    {
        "cancelado" => "Conexión cancelada.",
        "autenticacion" => "No pudimos autenticar con Microsoft — revisa Integraciones:Microsoft365 en la configuración.",
        "suscripcion" => "El buzón se autenticó pero no pudimos activar las notificaciones. Inténtalo de nuevo.",
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
