using CaeManager.Application.Clientes.Commands.GuardarAccesoPlataformaCae;
using CaeManager.Application.Clientes.Commands.GuardarPlataformaCaeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerAccesosPlataformaCaeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerPlataformaCaeCliente;
using CaeManager.Application.Clientes.Queries.RevelarContrasenaAccesoPlataformaCae;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Services;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Clientes.Components;

/// <summary>
/// Caja «Plataforma CAE» de Cliente 360. Es contexto del lateral: no bloquea la cabecera ni el cuerpo si tarda o
/// falla. Sus consultas comparten el DbContext del circuito con las de la página, así que no lee hasta que la página
/// pone <see cref="Cargar"/> (mismo patrón que <c>GestorCaeCliente</c>).
/// </summary>
public partial class CajaPlataformaCaeCliente : IDisposable
{
    /// <summary>La contraseña revelada con «Mostrar» se vuelve a ocultar sola (decisión del 18-10: 30 s).</summary>
    public static readonly TimeSpan TiempoContrasenaVisible = TimeSpan.FromSeconds(30);

    [Parameter, EditorRequired] public Guid ClienteEmpresarialId { get; set; }

    /// <summary>
    /// La página lo pone cuando terminó su cadena de lecturas y la fila «Gestor CAE» avisó: hasta entonces la caja no
    /// pregunta nada, porque dos consultas en paralelo sobre el DbContext del circuito fallan
    /// («A second operation was started on this context»).
    /// </summary>
    [Parameter] public bool Cargar { get; set; }

    /// <summary>
    /// Cada vez que <see cref="Cargar"/> pasa a true (o cambia el Cliente empresarial con la carga habilitada), llega
    /// una sola vez cuando la caja terminó su lectura, bien o con error. La página espera a esto para lanzar las
    /// lecturas de sus pestañas.
    /// </summary>
    [Parameter] public EventCallback AlCargar { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private TimeProvider Reloj { get; set; } = default!;

    private readonly CancellationTokenSource _ciclo = new();
    private readonly Dictionary<Guid, string> _reveladas = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _ocultados = [];

    private Guid _clienteCargado;
    private bool _cargarAnterior;
    private PlataformaCaeClienteDto? _plataforma;
    private IReadOnlyList<AccesoPlataformaCaeDto>? _accesos;
    private bool _accesosRequierenDosFactores;
    private bool _error;
    private bool _errorAccesos;

    private bool _modalPlataformaVisible;
    private bool _modalAccesoVisible;
    private bool _guardando;
    private string? _errorModal;
    private string _bNombre = string.Empty;
    private string _bUrl = string.Empty;
    private string _bCodigo = string.Empty;
    private AccesoPlataformaCaeDto? _accesoEnEdicion;
    private string _bUsuario = string.Empty;
    private string _bContrasena = string.Empty;

    private bool HayCambiosPlataforma =>
        _bNombre != (_plataforma?.Nombre ?? string.Empty)
        || _bUrl != (_plataforma?.UrlPortal ?? string.Empty)
        || _bCodigo != (_plataforma?.CodigoCliente ?? string.Empty);

    private bool HayCambiosAcceso =>
        _accesoEnEdicion is not null
        && (_bUsuario != (_accesoEnEdicion.Usuario ?? string.Empty) || _bContrasena.Length > 0);

    protected override async Task OnParametersSetAsync()
    {
        var recienHabilitado = Cargar && !_cargarAnterior;
        _cargarAnterior = Cargar;
        var otroCliente = ClienteEmpresarialId != _clienteCargado;

        if (otroCliente)
        {
            // Lo del Cliente empresarial anterior no se queda a la vista mientras se espera el turno de lectura.
            OcultarTodas();
            _plataforma = null;
            _accesos = null;
            _error = false;
            _errorAccesos = false;
            _accesosRequierenDosFactores = false;
        }

        // Solo en el render interactivo (mismo patrón que CentroDetalle): ClienteDetalle es
        // InteractiveServer, así que este método corre también en el prerender estático. Cargar
        // ahí auditaría dos veces la lectura de los usuarios y los dejaría en el HTML inicial.
        if (!Cargar || !otroCliente || !RendererInfo.IsInteractive)
        {
            if (recienHabilitado)
                await AlCargar.InvokeAsync();
            return;
        }

        _clienteCargado = ClienteEmpresarialId;
        await CargarAsync();
        await AlCargar.InvokeAsync();
    }

    private async Task CargarAsync()
    {
        var clienteEmpresarialId = ClienteEmpresarialId;
        _error = false;
        _errorAccesos = false;
        _accesosRequierenDosFactores = false;
        _accesos = null;

        try
        {
            var plataforma = await Mediator.Send(new ObtenerPlataformaCaeClienteQuery(clienteEmpresarialId), _ciclo.Token);
            if (clienteEmpresarialId != ClienteEmpresarialId) return;
            _plataforma = plataforma;

            if (plataforma is { Registrada: true })
            {
                try
                {
                    var accesos = await Mediator.Send(new ObtenerAccesosPlataformaCaeClienteQuery(clienteEmpresarialId), _ciclo.Token);
                    if (clienteEmpresarialId != ClienteEmpresarialId) return;
                    _accesos = accesos;
                }
                catch (SegundoFactorRequeridoParaCredencialesException)
                {
                    if (clienteEmpresarialId == ClienteEmpresarialId)
                        _accesosRequierenDosFactores = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Un fallo de los accesos no se lleva la plataforma ni «Abrir portal».
                    if (clienteEmpresarialId == ClienteEmpresarialId)
                        _errorAccesos = true;
                }
            }
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (clienteEmpresarialId == ClienteEmpresarialId)
            {
                _plataforma = null;
                _error = true;
            }
        }
    }

    private string EstadoDe(AccesoPlataformaCaeDto acceso)
    {
        if (acceso.ContrasenaCambiadaEnUtc is not { } cambiada)
            return Textos["PlataformaCaeSinContrasena"];

        var fecha = cambiada.EnHoraPeninsular().ToString("dd/MM/yyyy");
        return string.IsNullOrEmpty(acceso.ContrasenaCambiadaPor)
            ? Textos["PlataformaCaeCambiada", fecha]
            : Textos["PlataformaCaeCambiadaPor", fecha, acceso.ContrasenaCambiadaPor];
    }

    // ── Mostrar y copiar la contraseña: siempre una petición auditada, nunca al abrir la pantalla ──

    private async Task<string?> RevelarAsync(Guid empresaId)
    {
        var clienteEmpresarialId = ClienteEmpresarialId;
        ContrasenaAccesoPlataformaCaeDto? respuesta;
        try
        {
            respuesta = await Mediator.Send(new RevelarContrasenaAccesoPlataformaCaeQuery(clienteEmpresarialId, empresaId), _ciclo.Token);
        }
        catch (SegundoFactorRequeridoParaCredencialesException)
        {
            if (clienteEmpresarialId == ClienteEmpresarialId)
                SegundoFactorParaCredenciales.IrAConfigurar(NavigationManager);
            return null;
        }

        if (clienteEmpresarialId != ClienteEmpresarialId) return null;
        if (string.IsNullOrEmpty(respuesta?.Contrasena))
        {
            ToastService.Mostrar(Textos["PlataformaCaeSinContrasenaAviso"], TonoToast.Info);
            return null;
        }

        return respuesta.Contrasena;
    }

    private async Task AlternarContrasenaAsync(Guid empresaId)
    {
        if (_reveladas.ContainsKey(empresaId))
        {
            Ocultar(empresaId);
            return;
        }

        var contrasena = await RevelarAsync(empresaId);
        if (contrasena is null) return;

        _reveladas[empresaId] = contrasena;
        var temporizador = CancellationTokenSource.CreateLinkedTokenSource(_ciclo.Token);
        _ocultados[empresaId] = temporizador;
        _ = OcultarTrasEsperaAsync(empresaId, temporizador.Token);
    }

    private async Task OcultarTrasEsperaAsync(Guid empresaId, CancellationToken token)
    {
        try
        {
            await Task.Delay(TiempoContrasenaVisible, Reloj, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await InvokeAsync(() =>
        {
            Ocultar(empresaId);
            StateHasChanged();
        });
    }

    private void Ocultar(Guid empresaId)
    {
        _reveladas.Remove(empresaId);
        if (_ocultados.Remove(empresaId, out var temporizador))
        {
            temporizador.Cancel();
            temporizador.Dispose();
        }
    }

    private void OcultarTodas()
    {
        foreach (var empresaId in _reveladas.Keys.ToList())
            Ocultar(empresaId);
    }

    private Func<Task<string?>> CopiarContrasena(Guid empresaId) => () => RevelarAsync(empresaId);

    // ── Modales ──

    private void AbrirModalPlataforma()
    {
        _bNombre = _plataforma?.Nombre ?? string.Empty;
        _bUrl = _plataforma?.UrlPortal ?? string.Empty;
        _bCodigo = _plataforma?.CodigoCliente ?? string.Empty;
        _errorModal = null;
        _modalPlataformaVisible = true;
    }

    private async Task GuardarPlataformaAsync()
    {
        if (_guardando) return;
        _guardando = true;
        _errorModal = null;
        try
        {
            var resultado = await Mediator.Send(
                new GuardarPlataformaCaeClienteCommand(ClienteEmpresarialId, _bNombre, NuloSiVacio(_bUrl), NuloSiVacio(_bCodigo)), _ciclo.Token);
            if (resultado.EsFallido)
            {
                _errorModal = resultado.Error.Mensaje;
                await RecargarTrasConflictoAsync(resultado.Error);
                return;
            }

            _modalPlataformaVisible = false;
            ToastService.Mostrar(Textos["PlataformaCaeGuardada"], TonoToast.Exito);
            await CargarAsync();
        }
        finally
        {
            _guardando = false;
        }
    }

    private void AbrirModalAcceso(AccesoPlataformaCaeDto acceso)
    {
        _accesoEnEdicion = acceso;
        _bUsuario = acceso.Usuario ?? string.Empty;
        _bContrasena = string.Empty;
        _errorModal = null;
        _modalAccesoVisible = true;
    }

    private void CerrarModalAcceso(bool visible)
    {
        _modalAccesoVisible = visible;
        if (!visible)
        {
            // La contraseña tecleada no se queda en memoria del circuito al cerrar.
            _bContrasena = string.Empty;
            _accesoEnEdicion = null;
        }
    }

    private async Task GuardarAccesoAsync()
    {
        if (_guardando || _accesoEnEdicion is null) return;
        _guardando = true;
        _errorModal = null;
        try
        {
            var empresaId = _accesoEnEdicion.EmpresaId;
            // El usuario va tal cual: vacío lo quita (null lo conservaría). La contraseña vacía conserva la guardada.
            var resultado = await Mediator.Send(
                new GuardarAccesoPlataformaCaeCommand(ClienteEmpresarialId, empresaId, _bUsuario, NuloSiVacio(_bContrasena)), _ciclo.Token);
            if (resultado.EsFallido)
            {
                _errorModal = resultado.Error.Mensaje;
                await RecargarTrasConflictoAsync(resultado.Error);
                return;
            }

            Ocultar(empresaId);
            CerrarModalAcceso(false);
            ToastService.Mostrar(Textos["PlataformaCaeAccesoGuardado"], TonoToast.Exito);
            await CargarAsync();
        }
        catch (SegundoFactorRequeridoParaCredencialesException)
        {
            SegundoFactorParaCredenciales.IrAConfigurar(NavigationManager);
        }
        finally
        {
            _guardando = false;
        }
    }

    /// <summary>
    /// Otra persona ganó la carrera del índice único: el mensaje pide cerrar y volver a abrir, y para que al reabrir
    /// el modal parta de lo guardado (no del DTO de antes) se vuelve a leer la caja.
    /// </summary>
    private async Task RecargarTrasConflictoAsync(Error error)
    {
        if (error.Codigo is GuardarPlataformaCaeClienteCommand.CodigoConflicto
            or GuardarAccesoPlataformaCaeCommand.CodigoConflicto)
            await CargarAsync();
    }

    private static string? NuloSiVacio(string valor) => string.IsNullOrWhiteSpace(valor) ? null : valor;

    public void Dispose()
    {
        OcultarTodas();
        _ciclo.Cancel();
        _ciclo.Dispose();
    }
}
