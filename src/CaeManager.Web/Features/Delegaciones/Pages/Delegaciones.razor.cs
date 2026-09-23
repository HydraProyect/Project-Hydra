using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.AbrirAccesoSoporte;
using CaeManager.Application.Tenants.Commands.CerrarAccesoSoporte;
using CaeManager.Application.Tenants.Commands.CrearClienteDelegante;
using CaeManager.Application.Tenants.Commands.CrearDelegacionTenant;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.RevocarAsignacionOperadorDelegado;
using CaeManager.Application.Tenants.Queries.AutorizarOperadorCaeExterno;
using CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;
using CaeManager.Application.Tenants.Queries.EsTenantOrigenPlataforma;
using CaeManager.Application.Tenants.Queries.ObtenerActividadSoporte;
using CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;
using CaeManager.Domain.Soporte;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Delegaciones.Recursos;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Delegaciones.Pages;

public partial class Delegaciones : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase, IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosAutorizarOperadorCaeExterno> TextosAutorizar { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<Delegaciones> Logger { get; set; } = default!;
    [Inject] private IClienteActivoSeleccionado ClienteActivoSeleccionado { get; set; } = default!;

    private readonly CancellationTokenSource _cicloCarga = new();
    private readonly Dictionary<Guid, string> _nombresPorUsuarioId = [];
    private IReadOnlyList<DelegacionDto> _delegaciones = [];
    private bool _esAdministradorPlataforma;

    /// <summary>
    /// Mitad del criterio real de <c>AbrirAccesoSoporteCommand</c>/
    /// <c>CerrarAccesoSoporteCommand</c> que no viaja en <see cref="DelegacionDto"/>
    /// (la otra mitad es <c>SomosLaConsultora</c>) — ver <see cref="PuedeAdministrarAccesoSoporte"/>.
    /// </summary>
    private bool _esTenantOrigenPlataforma;

    /// <summary>
    /// Por delegación con criterio de reactivación cargado: ¿puede el usuario
    /// actual reactivarla? EXACTAMENTE el predicado de <c>ReactivarDelegacionTenantCommand</c>
    /// (vía <c>PuedeReactivarQuery</c>) — nunca <see cref="OperandoWorkspaceAjeno"/>, que
    /// aquí no basta: reactivar exige ser Administrador del Cliente Delegante,
    /// no solo operar desde el tenant de origen correcto.
    /// </summary>
    private readonly Dictionary<Guid, bool> _puedeReactivarPorEntidad = [];

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

    /// <summary>
    /// Enlace de preselección del Actor de Plataforma TALVEG
    /// (<c>/delegaciones?autorizar={Id}</c>, incremento 1b). Solo sugiere: abre el
    /// modal con ese Operador CAE externo ya resuelto; no escribe nada. La única
    /// escritura es el clic «Autorizar» del Administrador.
    /// </summary>
    [SupplyParameterFromQuery(Name = "autorizar")]
    private Guid? OperadorSugeridoId { get; set; }

    /// <summary>
    /// Tenant propietario que la persona administra (su Tenant de origen), o null.
    /// Lo decide <see cref="ObtenerTenantPropietarioAutorizanteQuery"/> con el mismo
    /// predicado que el comando; es el <c>TenantClienteId</c> que se le envía.
    /// </summary>
    private Guid? _tenantPropietarioAutorizante;
    private bool _sugerenciaAtendida;
    private bool _mostrarAutorizarOperador;
    private bool _operadorSugerido;
    private string _busquedaOperador = string.Empty;
    private OperadorCaeExternoAutorizableDto? _operadorCandidato;
    private bool _operadorSeleccionado;
    private bool _busquedaSinResultado;
    private int _versionBusquedaOperador;
    private bool _autorizandoOperador;
    private string? _errorAutorizarOperador;

    private bool OperandoWorkspaceAjeno => ClienteActivoSeleccionado.TenantIdSeleccionado is not null;
    private bool PuedeGestionar => _esAdministradorPlataforma && !OperandoWorkspaceAjeno;

    /// <summary>
    /// Fuera de la organización propia no: la operación delegada se escribe con el
    /// Tenant propietario como workspace activo, y RLS la rechazaría desde otro.
    /// </summary>
    private bool PuedeAutorizarOperador => _tenantPropietarioAutorizante is not null && !OperandoWorkspaceAjeno;

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
            var esTenantOrigenPlataforma = await Mediator.Send(new EsTenantOrigenPlataformaQuery(), token);
            var tenantPropietarioAutorizante = await Mediator.Send(new ObtenerTenantPropietarioAutorizanteQuery(), token);
            var delegaciones = await Mediator.Send(new ObtenerDelegacionesQuery(), token);
            await CargarNombresDeOperadoresAsync(delegaciones.SelectMany(d => d.Operadores).Select(o => o.UsuarioId), token);
            await CargarCriteriosDeReactivacionAsync(
                delegaciones.Where(e => !e.EsSoporte && !e.Activa).Select(e => (e.Id, e.TenantClienteId)), token);
            if (_desechado || version != _versionCarga)
            {
                return;
            }

            _esAdministradorPlataforma = esAdministrador;
            _esTenantOrigenPlataforma = esTenantOrigenPlataforma;
            _tenantPropietarioAutorizante = tenantPropietarioAutorizante;
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

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // La sugerencia se atiende una sola vez, cuando ya se sabe si la persona
        // puede autorizar. Quien no puede no ve modal ni aprende nada del Id.
        if (_sugerenciaAtendida || _cargando || OperadorSugeridoId is not { } operadorId)
        {
            return;
        }

        _sugerenciaAtendida = true;
        if (!PuedeAutorizarOperador)
        {
            return;
        }

        AbrirAutorizarOperador();
        _operadorSugerido = true;
        var version = _versionBusquedaOperador;
        var candidato = await Mediator.Send(new BuscarOperadorCaeExternoAutorizableQuery(operadorId, null), _cicloCarga.Token);
        // Sin comprobar la versión, cerrar el modal y reabrirlo (o teclear una
        // búsqueda propia) mientras esta respuesta está en vuelo dejaba que la
        // preselección pisara lo que la persona ya había escrito — hallazgo de
        // la revisión puente del incremento 1b. No escribe nada: es solo la
        // pantalla mostrando el candidato equivocado.
        if (_desechado || !_mostrarAutorizarOperador || version != _versionBusquedaOperador)
        {
            return;
        }

        if (candidato is null)
        {
            _operadorSugerido = false;
            _errorAutorizarOperador = TextosAutorizar["EnlaceNoCorresponde"];
        }
        else
        {
            _operadorCandidato = candidato;
            _operadorSeleccionado = true;
            _busquedaOperador = candidato.Nombre;
        }

        StateHasChanged();
    }

    private void AbrirAutorizarOperador()
    {
        _mostrarAutorizarOperador = true;
        _operadorSugerido = false;
        _busquedaOperador = string.Empty;
        _operadorCandidato = null;
        _operadorSeleccionado = false;
        _busquedaSinResultado = false;
        _errorAutorizarOperador = null;
        _versionBusquedaOperador++;
    }

    private void CerrarAutorizarOperador(bool visible)
    {
        if (!visible && !_autorizandoOperador)
        {
            _mostrarAutorizarOperador = false;
            _versionBusquedaOperador++;
        }
    }

    /// <summary>
    /// Solo por nombre exacto (sin distinguir mayúsculas): la consulta nunca lista
    /// Operadores CAE externos por fragmentos, para no enseñar a quién da servicio
    /// TALVEG. Cambiar el texto descarta la selección anterior.
    /// </summary>
    private async Task BuscarOperadorAsync(string texto)
    {
        var version = ++_versionBusquedaOperador;
        _busquedaOperador = texto;
        _operadorCandidato = null;
        _operadorSeleccionado = false;
        _operadorSugerido = false;
        _busquedaSinResultado = false;
        _errorAutorizarOperador = null;

        if (string.IsNullOrWhiteSpace(texto))
        {
            return;
        }

        var candidato = await Mediator.Send(new BuscarOperadorCaeExternoAutorizableQuery(null, texto), _cicloCarga.Token);
        if (_desechado || version != _versionBusquedaOperador)
        {
            return;
        }

        _operadorCandidato = candidato;
        _busquedaSinResultado = candidato is null;
    }

    private void SeleccionarOperador()
    {
        if (_operadorCandidato is null)
        {
            return;
        }

        _operadorSeleccionado = true;
        _errorAutorizarOperador = null;
    }

    private async Task AutorizarOperadorAsync()
    {
        if (_autorizandoOperador || _tenantPropietarioAutorizante is not { } tenantPropietario)
        {
            return;
        }

        if (_operadorCandidato is not { } operador || !_operadorSeleccionado)
        {
            _errorAutorizarOperador = TextosAutorizar["SeleccionaOperador"];
            return;
        }

        _autorizandoOperador = true;
        _errorAutorizarOperador = null;
        StateHasChanged();
        try
        {
            var resultado = await Mediator.Send(new CrearDelegacionTenantCommand(operador.TenantId, tenantPropietario));
            if (_desechado)
            {
                return;
            }

            if (resultado.EsFallido)
            {
                _errorAutorizarOperador = resultado.Error.Mensaje;
                return;
            }

            _mostrarAutorizarOperador = false;
            ToastService.Mostrar(TextosAutorizar["Autorizado", operador.Nombre], TonoToast.Exito);
            await CargarAsync();
        }
        catch (ValidationException ex)
        {
            if (!_desechado)
            {
                _errorAutorizarOperador = string.Join(" ", ex.Errors.Select(e => e.ErrorMessage));
            }
        }
        finally
        {
            _autorizandoOperador = false;
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

    /// <summary>
    /// Solo para las entidades con botón "Reactivar" visible hoy (no vigentes
    /// de soporte quedan fuera del cálculo: usan otro criterio, ver
    /// <see cref="_esTenantOrigenPlataforma"/>). Fallo cerrado: una entidad sin
    /// entrada en <see cref="_puedeReactivarPorEntidad"/> se trata como "no
    /// autorizado" en <see cref="PuedeReactivar"/>, nunca como "sí".
    /// </summary>
    private Task CargarCriteriosDeReactivacionAsync(IEnumerable<(Guid Id, Guid TenantClienteId)> entidades, CancellationToken token) =>
        PuertaAccesoDatos.EjecutarAsync(async () =>
        {
            foreach (var (id, tenantClienteId) in entidades)
            {
                token.ThrowIfCancellationRequested();
                _puedeReactivarPorEntidad[id] = await Mediator.Send(new PuedeReactivarQuery(tenantClienteId), token);
            }
        });

    /// <summary>Ver el doc-comment de <see cref="_esTenantOrigenPlataforma"/>.</summary>
    private bool PuedeAdministrarAccesoSoporte(bool somosLaConsultora) =>
        _esTenantOrigenPlataforma && somosLaConsultora;

    private bool PuedeReactivar(Guid entidadId) =>
        _puedeReactivarPorEntidad.GetValueOrDefault(entidadId);

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
