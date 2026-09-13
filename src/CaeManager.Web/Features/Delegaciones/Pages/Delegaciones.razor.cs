using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.AbrirAccesoSoporte;
using CaeManager.Application.Tenants.Commands.CerrarAccesoSoporte;
using CaeManager.Application.Tenants.Commands.CrearClienteDelegante;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.RevocarAsignacionOperadorDelegado;
using CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;
using CaeManager.Application.Tenants.Queries.ObtenerActividadSoporte;
using CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;
using CaeManager.Domain.Soporte;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Delegaciones.Pages;

public partial class Delegaciones : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase, IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<Delegaciones> Logger { get; set; } = default!;
    [Inject] private IClienteActivoSeleccionado ClienteActivoSeleccionado { get; set; } = default!;

    private readonly CancellationTokenSource _cicloCarga = new();
    private readonly Dictionary<Guid, string> _nombresPorUsuarioId = [];
    private IReadOnlyList<DelegacionDto> _delegaciones = [];
    private bool _esAdministradorPlataforma;
    private bool _cargando = true;
    private bool _error;
    private bool _desechado;
    private int _versionCarga;
    private int _generacionEntidad;
    private Guid? _entidadActiva;
    private readonly HashSet<Guid> _operacionesEnCurso = [];

    private DelegacionDto? _delegacionARevocar;
    private DelegacionDto? _verActividadDe;
    private IReadOnlyList<ActividadSoporteDto> _actividad = [];
    private bool _cargandoActividad;
    private DelegacionDto? _delegacionSoporteAAbrir;
    private string _motivoSoporte = string.Empty;
    private string _horasSoporte = "4";
    private string _rolSoporte = RolesSoporte.SoloLectura;
    private string? _errorSoporte;
    private bool _mostrarNuevaDelegacion;
    private string _nombreClienteNuevo = string.Empty;
    private bool _creandoDelegacion;
    private string? _errorNuevaDelegacion;

    private bool OperandoWorkspaceAjeno => ClienteActivoSeleccionado.TenantIdSeleccionado is not null;
    private bool PuedeGestionar => _esAdministradorPlataforma && !OperandoWorkspaceAjeno;

    /// <summary>Nombre canónico para la guarda de reentrada del alta — evita repetir el campo legacy en cada punto de lectura.</summary>
    private bool CreacionEnCurso => _creandoDelegacion;

    private void OcultarFormularioNueva() => _mostrarNuevaDelegacion = false;
    private string MensajeRevocacion => _delegacionARevocar is not { } aRevocar
        ? string.Empty
        : $"Se retirará el acceso de «{TituloDe(aRevocar.SomosLaConsultora, aRevocar.ClienteNombre, aRevocar.ConsultoraNombre)}». " +
          "Los datos no se borran y la delegación se puede reactivar.";

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        if (_desechado)
        {
            return;
        }

        var version = ++_versionCarga;
        var token = _cicloCarga.Token;
        _cargando = true;
        _error = false;
        StateHasChanged();

        try
        {
            token.ThrowIfCancellationRequested();
            var esAdministrador = await Mediator.Send(new EsAdministradorPlataformaQuery(), token);
            var delegaciones = await Mediator.Send(new ObtenerDelegacionesQuery(), token);
            await CargarNombresDeOperadoresAsync(delegaciones.SelectMany(d => d.Operadores).Select(o => o.UsuarioId), token);
            if (_desechado || version != _versionCarga)
            {
                return;
            }

            _esAdministradorPlataforma = esAdministrador;
            _delegaciones = delegaciones;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_desechado || version != _versionCarga)
            {
                return;
            }

            Logger.LogError(ex, "Error al cargar las delegaciones del tenant de origen.");
            _error = true;
        }
        finally
        {
            if (!_desechado && version == _versionCarga)
            {
                _cargando = false;
            }
        }
    }

    private Task CargarNombresDeOperadoresAsync(IEnumerable<Guid> usuarioIds, CancellationToken cancellationToken) =>
        PuertaAccesoDatos.EjecutarAsync(async () =>
        {
            foreach (var usuarioId in usuarioIds.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_nombresPorUsuarioId.ContainsKey(usuarioId))
                {
                    continue;
                }

                var usuario = await UserManager.FindByIdAsync(usuarioId.ToString());
                _nombresPorUsuarioId[usuarioId] = usuario is null
                    ? "Persona no encontrada"
                    : $"{usuario.NombreCompleto} ({usuario.Email})";
            }
        });

    private string NombreDeUsuario(Guid usuarioId) =>
        _nombresPorUsuarioId.GetValueOrDefault(usuarioId, "…");

    private bool EsUsuarioNoResuelto(Guid usuarioId) =>
        _nombresPorUsuarioId.TryGetValue(usuarioId, out var nombre) && nombre == "Persona no encontrada";

    private static string NombreRol(string rol) => Roles.NombreVisible(rol);
    private static string TituloDe(bool somosLaConsultora, string clienteNombre, string consultoraNombre) =>
        somosLaConsultora ? $"Gestionamos a {clienteNombre}" : $"{consultoraNombre} nos gestiona";

    private static string TextoEstado(bool esSoporte, bool accesoVigente, bool ventanaCaducada, bool activa) => esSoporte
        ? accesoVigente
            ? "Acceso abierto"
            : ventanaCaducada
                ? "Ventana caducada"
                : "Sin abrir"
        : activa
            ? "Activa"
            : "Revocada";

    private static TonoBadge TonoEstado(bool esSoporte, bool accesoVigente, bool ventanaCaducada, bool activa) => esSoporte
        ? accesoVigente
            ? TonoBadge.Exito
            : ventanaCaducada
                ? TonoBadge.Advertencia
                : TonoBadge.Neutro
        : activa
            ? TonoBadge.Exito
            : TonoBadge.Neutro;

    private int PrepararEntidad(Guid id)
    {
        if (_entidadActiva != id)
        {
            _entidadActiva = id;
            _generacionEntidad++;
            _cargandoActividad = false;
            _delegacionARevocar = null;
            _delegacionSoporteAAbrir = null;
            _verActividadDe = null;
            _actividad = [];
        }
        return _generacionEntidad;
    }

    private bool EsEntidadVigente(int generacion, Guid id) =>
        !_desechado && _generacionEntidad == generacion && _entidadActiva == id;

    private bool EstaProcesando(Guid? operacionId) =>
        operacionId is { } id && _operacionesEnCurso.Contains(id);

    private void CerrarRevocacion(bool visible)
    {
        if (!visible && !EstaProcesando(_delegacionARevocar?.Id))
        {
            _delegacionARevocar = null;
        }
    }

    private void CerrarActividad(bool visible)
    {
        if (!visible && !_cargandoActividad)
        {
            _verActividadDe = null;
            _actividad = [];
        }
    }

    private void CerrarFormularioSoporte(bool visible)
    {
        if (!visible && !EstaProcesando(_delegacionSoporteAAbrir?.Id))
        {
            _delegacionSoporteAAbrir = null;
        }
    }

    private void CerrarFormularioNueva(bool visible)
    {
        if (!visible && !CreacionEnCurso)
        {
            OcultarFormularioNueva();
        }
    }

    private async Task RevocarAsync()
    {
        if (_delegacionARevocar is not { } delegacion || EstaProcesando(delegacion.Id))
        {
            return;
        }

        var generacion = PrepararEntidad(delegacion.Id);
        _operacionesEnCurso.Add(delegacion.Id);
        StateHasChanged();
        try
        {
            var resultado = await Mediator.Send(new DesactivarDelegacionTenantCommand(delegacion.Id));
            var fallido = resultado.EsFallido;
            var mensaje = fallido ? resultado.Error.Mensaje : null;
            if (!EsEntidadVigente(generacion, delegacion.Id))
            {
                return;
            }

            if (fallido)
            {
                ToastService.Mostrar(mensaje!, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Acceso revocado.", TonoToast.Exito);
            _delegacionARevocar = null;
            await CargarAsync();
        }
        finally
        {
            _operacionesEnCurso.Remove(delegacion.Id);
        }
    }

    private async Task VerActividadAsync(DelegacionDto delegacion)
    {
        var generacion = PrepararEntidad(delegacion.Id);
        var token = _cicloCarga.Token;
        _verActividadDe = delegacion;
        _actividad = [];
        _cargandoActividad = true;
        StateHasChanged();
        try
        {
            var actividad = await Mediator.Send(new ObtenerActividadSoporteQuery(delegacion.Id), token);
            if (EsEntidadVigente(generacion, delegacion.Id))
            {
                _actividad = actividad;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (EsEntidadVigente(generacion, delegacion.Id))
            {
                Logger.LogError(ex, "Error al cargar la actividad de soporte de la delegación {DelegacionId}.", delegacion.Id);
                ToastService.Mostrar("No pudimos cargar la actividad registrada.", TonoToast.Error);
            }
        }
        finally
        {
            if (EsEntidadVigente(generacion, delegacion.Id))
            {
                _cargandoActividad = false;
            }
        }
    }

    private static string DescribirTipo(TipoActividadSoporte tipo) => tipo switch
    {
        TipoActividadSoporte.AccesoConcedido => "Acceso concedido",
        TipoActividadSoporte.WorkspaceActivado => "Entró en la organización",
        TipoActividadSoporte.Navegacion => "Navegó a",
        TipoActividadSoporte.Interaccion => "Pulsó",
        TipoActividadSoporte.AccesoRevocado => "Acceso cerrado",
        _ => tipo.ToString()
    };

    private void AbrirFormularioSoporte(DelegacionDto delegacion)
    {
        if (EstaProcesando(delegacion.Id))
        {
            return;
        }

        PrepararEntidad(delegacion.Id);
        _delegacionSoporteAAbrir = delegacion;
        _motivoSoporte = string.Empty;
        _horasSoporte = "4";
        _rolSoporte = RolesSoporte.SoloLectura;
        _errorSoporte = null;
    }
    private async Task AbrirAccesoSoporteAsync()
    {
        if (_delegacionSoporteAAbrir is not { } delegacion || EstaProcesando(delegacion.Id))
        {
            return;
        }

        var generacion = PrepararEntidad(delegacion.Id);
        _operacionesEnCurso.Add(delegacion.Id);
        _errorSoporte = null;
        StateHasChanged();
        try
        {
            if (!int.TryParse(_horasSoporte, out var horas))
            {
                _errorSoporte = "Indica las horas de acceso como un número.";
                return;
            }

            var resultado = await Mediator.Send(new AbrirAccesoSoporteCommand(delegacion.Id, _motivoSoporte, horas, _rolSoporte));
            var fallido = resultado.EsFallido;
            var mensaje = fallido ? resultado.Error.Mensaje : null;
            if (!EsEntidadVigente(generacion, delegacion.Id))
            {
                return;
            }

            if (fallido)
            {
                _errorSoporte = mensaje;
                return;
            }

            ToastService.Mostrar("Acceso de soporte abierto.", TonoToast.Exito);
            _delegacionSoporteAAbrir = null;
            await CargarAsync();
        }
        catch (ValidationException ex)
        {
            if (EsEntidadVigente(generacion, delegacion.Id))
            {
                _errorSoporte = string.Join(" ", ex.Errors.Select(e => e.ErrorMessage));
            }
        }
        finally
        {
            _operacionesEnCurso.Remove(delegacion.Id);
        }
    }

    private async Task CerrarAccesoSoporteAsync(DelegacionDto delegacion) =>
        await EjecutarParaEntidadAsync(
            delegacion.Id,
            delegacion.Id,
            () => Mediator.Send(new CerrarAccesoSoporteCommand(delegacion.Id)),
            "Acceso de soporte cerrado.");

    private async Task ReactivarAsync(DelegacionDto delegacion) =>
        await EjecutarParaEntidadAsync(
            delegacion.Id,
            delegacion.Id,
            () => Mediator.Send(new ReactivarDelegacionTenantCommand(delegacion.Id)),
            "Delegación reactivada.");

    private async Task RetirarOperadorAsync(Guid delegacionId, OperadorDelegadoDto operador) =>
        await EjecutarParaEntidadAsync(
            delegacionId,
            operador.AsignacionId,
            () => Mediator.Send(new RevocarAsignacionOperadorDelegadoCommand(operador.AsignacionId)),
            "Persona retirada de la delegación.");

    private async Task EjecutarParaEntidadAsync(Guid delegacionId, Guid operacionId, Func<Task<CaeManager.Domain.Common.Result>> enviar, string exito)
    {
        if (EstaProcesando(operacionId))
        {
            return;
        }

        var generacion = PrepararEntidad(delegacionId);
        _operacionesEnCurso.Add(operacionId);
        StateHasChanged();
        try
        {
            var resultado = await enviar();
            var fallido = resultado.EsFallido;
            var mensaje = fallido ? resultado.Error.Mensaje : null;
            if (!EsEntidadVigente(generacion, delegacionId))
            {
                return;
            }

            if (fallido)
            {
                ToastService.Mostrar(mensaje!, TonoToast.Error);
                return;
            }

            ToastService.Mostrar(exito, TonoToast.Exito);
            await CargarAsync();
        }
        finally
        {
            _operacionesEnCurso.Remove(operacionId);
        }
    }

    private void AbrirFormularioNuevaDelegacion()
    {
        if (CreacionEnCurso)
        {
            return;
        }

        _mostrarNuevaDelegacion = true;
        _nombreClienteNuevo = string.Empty;
        _errorNuevaDelegacion = null;
    }

    private async Task CrearDelegacionAsync()
    {
        if (CreacionEnCurso)
        {
            return;
        }

        _creandoDelegacion = true;
        _errorNuevaDelegacion = null;
        StateHasChanged();
        try
        {
            var resultado = await Mediator.Send(new CrearClienteDeleganteCommand(_nombreClienteNuevo));
            if (_desechado)
            {
                return;
            }

            if (resultado.EsFallido)
            {
                _errorNuevaDelegacion = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Organización creada y delegación activa.", TonoToast.Exito);
            OcultarFormularioNueva();
            await CargarAsync();
        }
        catch (ValidationException ex)
        {
            if (!_desechado)
            {
                _errorNuevaDelegacion = string.Join(" ", ex.Errors.Select(e => e.ErrorMessage));
            }
        }
        finally
        {
            if (!_desechado)
            {
                _creandoDelegacion = false;
            }
        }
    }

    public void Dispose()
    {
        _desechado = true;
        _cicloCarga.Cancel();
        _cicloCarga.Dispose();
    }
}
