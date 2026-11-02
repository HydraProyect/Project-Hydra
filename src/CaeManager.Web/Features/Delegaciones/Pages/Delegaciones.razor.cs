using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Application.Plataforma.Queries.ObtenerAccesosSoporteTalveg;
using CaeManager.Domain.Plataforma;
using CaeManager.Application.Tenants;
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
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Delegaciones.Recursos;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using AsignacionLista = CaeManager.Application.Tenants.Queries.ObtenerDelegaciones.DelegacionDto;

namespace CaeManager.Web.Features.Delegaciones.Pages;

public partial class Delegaciones : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase, IDisposable
{
    private bool HayFiltrosActivos => !string.IsNullOrWhiteSpace(BusquedaLista) || !string.IsNullOrEmpty(EstadoLista) || !string.IsNullOrEmpty(TipoLista);

    public static readonly IReadOnlyList<string> ParametrosDeVista = ["q", "estado", "tipo", "agrupar"];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosAutorizarOperadorCaeExterno> TextosAutorizar { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosAccesosSoporteTalveg> TextosAccesos { get; set; } = default!;
    [Inject] private ILecturaPersonasDelegadas LecturaPersonas { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<Delegaciones> Logger { get; set; } = default!;
    [Inject] private IClienteActivoSeleccionado ClienteActivoSeleccionado { get; set; } = default!;

    private readonly CancellationTokenSource _cicloCarga = new();
    private readonly Dictionary<Guid, string> _nombresPorUsuarioId = [];

    /// <summary>
    /// Por Asignación listada, el rol que la persona ejerce en el Tenant beneficiario, no el
    /// que guarda su cartera (decisión Chris 2026-10-20; hallazgo 16 del 2026-10-21, un
    /// Administrador salía como «Gestor CAE»). Lo decide
    /// <see cref="TenantsBeneficiariosAutorizados.RolEjercidoEnTenantBeneficiario"/>; sin
    /// entrada, el rol guardado.
    /// </summary>
    private readonly Dictionary<Guid, string> _rolMostradoPorAsignacionId = [];
    private IReadOnlyList<AsignacionLista> _delegaciones = [];
    private bool _esAdministradorPlataforma;

    /// <summary>
    /// Mitad del criterio real de <c>AbrirAccesoSoporteCommand</c>/
    /// <c>CerrarAccesoSoporteCommand</c> que no viaja en <see cref="DelegacionDto"/>
    /// (la otra mitad es <c>SomosLaConsultora</c>) — ver <see cref="PuedeAdministrarAccesoSoporte"/>.
    /// </summary>
    private bool _esTenantOrigenPlataforma;

    /// <summary>
    /// Por acceso de Operador CAE externo revocado: ¿puede el usuario actual reactivarlo?
    /// EXACTAMENTE el predicado de <c>ReactivarDelegacionTenantCommand</c> (vía
    /// <c>PuedeReactivarQuery</c>) — nunca <see cref="OperandoWorkspaceAjeno"/>, que aquí no
    /// basta: reactivar es solo del Administrador del Tenant propietario (decisión SEG-7 del
    /// 2026-10-31), nunca del Operador CAE externo ni de cualquiera que opere desde el Tenant de
    /// origen correcto.
    /// </summary>
    private readonly Dictionary<Guid, bool> _puedeReactivarPorEntidad = [];

    /// <summary>
    /// Por acceso de Operador CAE externo activo: ¿puede el usuario actual revocarlo? Mismo
    /// criterio que <see cref="_puedeReactivarPorEntidad"/>, con el predicado de
    /// <c>DesactivarDelegacionTenantCommand</c> (vía <c>PuedeRevocarQuery</c>).
    /// </summary>
    private readonly Dictionary<Guid, bool> _puedeRevocarPorEntidad = [];

    private bool _cargando = true;
    private bool _error;
    private bool _desechado;
    private int _versionCarga;
    private int _generacionEntidad;
    private Guid? _entidadActiva;
    private readonly HashSet<Guid> _operacionesEnCurso = [];

    private AsignacionLista? _delegacionARevocar;
    private (Guid EntidadId, OperadorDelegadoDto Operador)? _operadorARetirar;
    private AsignacionLista? _verActividadDe;
    private IReadOnlyList<ActividadSoporteDto> _actividad = [];
    private bool _cargandoActividad;
    private AsignacionLista? _delegacionSoporteAAbrir;
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
    private Guid? _sugerenciaVista;
    private bool _mostrarAutorizarOperador;

    /// <summary>
    /// Sesiones Privilegiadas de Soporte TALVEG sobre el Tenant propietario;
    /// <c>null</c> si quien mira no es su Administrador, y entonces la sección
    /// no se pinta (ADR-011 § 8.7, incremento 2).
    /// </summary>
    private IReadOnlyList<AccesoSoporteTalvegDto>? _accesosSoporteTalveg;
    private bool _operadorSugerido;
    private string _busquedaOperador = string.Empty;
    private OperadorCaeExternoAutorizableDto? _operadorCandidato;
    private bool _operadorSeleccionado;
    private bool _busquedaSinResultado;
    private int _versionBusquedaOperador;
    private bool _autorizandoOperador;
    private string? _errorAutorizarOperador;

    private Guid? TenantSeleccionado => ClienteActivoSeleccionado.TenantIdSeleccionado;
    private bool OperandoWorkspaceAjeno => TenantSeleccionado is not null;
    private bool PuedeGestionar => _esAdministradorPlataforma && !OperandoWorkspaceAjeno;

    /// <summary>
    /// Si se ofrece «Retirar» a una persona de la delegación. En una de Operador CAE externo el
    /// Command solo la atiende al Administrador del Tenant propietario, así que el botón se pinta
    /// a quien administra su Tenant de origen (<see cref="_tenantPropietarioAutorizante"/>, el mismo
    /// dato que <see cref="PuedeAutorizarOperador"/>) cuando ese Tenant es la otra parte, el
    /// propietario: ni al Operador CAE externo (<c>SomosLaConsultora</c>) ni a un Gestor CAE del
    /// Tenant propietario. La de Soporte TALVEG la atienden las dos partes. Etiqueta de oferta, no
    /// autoridad: la puerta es el handler.
    /// </summary>
    private bool PuedeRetirarPersonas(AsignacionLista delegacion) =>
        !OperandoWorkspaceAjeno
        && (delegacion.EsSoporte || (!delegacion.SomosLaConsultora && _tenantPropietarioAutorizante is not null));

    /// <summary>
    /// Fuera de la organización propia no: la operación delegada se escribe con el
    /// Tenant propietario como workspace activo, y RLS la rechazaría desde otro.
    /// </summary>
    private bool PuedeAutorizarOperador => _tenantPropietarioAutorizante is not null && !OperandoWorkspaceAjeno;

    /// <summary>
    /// Quién puede poner a un Gestor CAE en cartera, y por tanto abrir /usuarios: los mismos
    /// roles de su <c>[Authorize]</c>. A los demás el vacío de la tarjeta les dice a quién
    /// pedirlo en vez de enlazarles una ruta que su rol cierra.
    /// </summary>
    private const string RolesQueAsignanCartera =
        $"{Roles.Administrador},{Roles.DireccionCae},{Roles.CoordinadorCae}";

    /// <summary>Nombre canónico para la guarda de reentrada del alta — evita repetir el campo legacy en cada punto de lectura.</summary>
    private bool CreacionEnCurso => _creandoDelegacion;

    /// <summary>
    /// Cerrar la nueva delegación (cancelar, crearla o descartarla al salir) vacía el nombre:
    /// reabrirla sin tocarlo no es un cambio, y así el nombre escrito basta para saber si hay
    /// algo que perder (P1-E2b).
    /// </summary>
    private void OcultarFormularioNueva()
    {
        _mostrarNuevaDelegacion = false;
        _nombreClienteNuevo = string.Empty;
    }
    private string TextoEstadoAcceso(EstadoAccesoSoporteTalveg estado) => estado switch
    {
        EstadoAccesoSoporteTalveg.Abierto => TextosAccesos["EstadoAbierto"],
        EstadoAccesoSoporteTalveg.Cerrado => TextosAccesos["EstadoCerrado"],
        _ => TextosAccesos["EstadoCaducado"],
    };

    private string TextoTipoAcceso(CapacidadPrivilegio capacidad) => capacidad switch
    {
        CapacidadPrivilegio.SoporteLectura => TextosAccesos["TipoSoporteLectura"],
        CapacidadPrivilegio.Aprovisionamiento => TextosAccesos["TipoAprovisionamiento"],
        _ => TextosAccesos["TipoOtro"],
    };

    private static TonoBadge TonoEstadoAcceso(EstadoAccesoSoporteTalveg estado) => estado switch
    {
        EstadoAccesoSoporteTalveg.Abierto => TonoBadge.Advertencia,
        EstadoAccesoSoporteTalveg.Cerrado => TonoBadge.Neutro,
        _ => TonoBadge.Neutro,
    };

    /// <summary>
    /// Inicio y final efectivo de la ventana: el cierre si la sesión se cerró
    /// antes de expirar, si no, su expiración.
    /// </summary>
    private static string TextoVentanaAcceso(AccesoSoporteTalvegDto acceso)
    {
        var fin = acceso.CerradaEnUtc < acceso.ExpiraEnUtc ? acceso.CerradaEnUtc.Value : acceso.ExpiraEnUtc;
        return $"{acceso.InicioEnUtc.EnHoraPeninsular():dd/MM/yyyy HH:mm} – {fin.EnHoraPeninsular():HH:mm}";
    }

    private string MensajeRevocacion => _delegacionARevocar is not { } aRevocar
        ? string.Empty
        : $"Se retirará el acceso de «{TituloDe(aRevocar.SomosLaConsultora, aRevocar.ClienteNombre, aRevocar.ConsultoraNombre)}». " +
          "Los datos no se borran. Solo un Administrador de la organización propietaria de los datos podrá reactivarlo.";

    private string MensajeRetirada
    {
        get
        {
            if (_operadorARetirar is not { } aRetirar)
            {
                return string.Empty;
            }

            var vinculo = _delegaciones.FirstOrDefault(d => d.Id == aRetirar.EntidadId);
            var titulo = vinculo is null ? string.Empty : TituloDe(vinculo.SomosLaConsultora, vinculo.ClienteNombre, vinculo.ConsultoraNombre);
            return TextosAutorizar["RetirarMensaje", NombreDeUsuario(aRetirar.Operador.UsuarioId), titulo];
        }
    }

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
            var accesosSoporteTalveg = await Mediator.Send(new ObtenerAccesosSoporteTalvegQuery(), token);
            // Con un Tenant beneficiario seleccionado, solo las delegaciones que lo tienen en un
            // extremo (hallazgo 16, 2026-10-21): la consulta sigue acotada al Tenant de origen.
            var delegaciones = await Mediator.Send(new ObtenerDelegacionesQuery(TenantSeleccionado), token);
            await CargarNombresDeOperadoresAsync(
                delegaciones.SelectMany(d => d.Operadores.Select(o => (o, d.EsSoporte, d.OperacionDelTenantEntero))), token);
            await CargarCriteriosDeRevocacionYReactivacionAsync(
                delegaciones.Where(e => !e.EsSoporte).Select(e => (e.Id, e.Activa)), token);
            if (_desechado || version != _versionCarga)
            {
                return;
            }

            _esAdministradorPlataforma = esAdministrador;
            _esTenantOrigenPlataforma = esTenantOrigenPlataforma;
            _tenantPropietarioAutorizante = tenantPropietarioAutorizante;
            _accesosSoporteTalveg = accesosSoporteTalveg;
            _delegaciones = delegaciones;
            _paginaLista = Math.Clamp(_paginaLista, 1, TotalPaginasLista);
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

    /// <summary>
    /// Un enlace nuevo en el mismo circuito (<c>?autorizar=</c> con otro Id, sin
    /// recargar) vuelve a atenderse: sin esto <c>_sugerenciaAtendida</c> quedaba en
    /// <c>true</c> para toda la vida del componente y el segundo enlace no abría el
    /// modal (hallazgo de Codex, P2, al integrar <c>origin/main</c> en el incremento 1b).
    /// </summary>
    protected override void OnParametersSet()
    {
        SincronizarParametrosLista();
        if (OperadorSugeridoId != _sugerenciaVista)
        {
            _sugerenciaVista = OperadorSugeridoId;
            _sugerenciaAtendida = false;
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
            // El Operador CAE que trae el enlace no es un cambio de quien autoriza.
            FijarInstantaneaOperador();
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
        FijarInstantaneaOperador();
    }

    private readonly InstantaneaFormulario _instantaneaSoporte = new();
    private readonly InstantaneaFormulario _instantaneaOperador = new();

    /// <summary>
    /// P1-E2b: «hay cambios» de cada uno de los tres ModalFormulario de la página (el guardián de su kit): el acceso de
    /// Soporte TALVEG (motivo, horas y permisos) y la autorización de un Operador CAE externo (búsqueda y selección),
    /// cada uno comparado con cómo se abrió, o un nombre ya escrito en la nueva delegación. Cerrados (también tras
    /// guardar) nunca hay nada que perder. Solo el aviso: no toca la apertura del acceso ni su autorización.
    /// </summary>
    private bool HayCambiosSoporte => _delegacionSoporteAAbrir is not null && _instantaneaSoporte.Difiere(ValoresSoporte());

    private bool HayCambiosNueva => !string.IsNullOrWhiteSpace(_nombreClienteNuevo);

    private bool HayCambiosOperador => _mostrarAutorizarOperador && _instantaneaOperador.Difiere(ValoresOperador());

    private object?[] ValoresSoporte() => [_motivoSoporte, _horasSoporte, _rolSoporte];

    private object?[] ValoresOperador() => [_busquedaOperador, _operadorSeleccionado];

    private void FijarInstantaneaOperador() => _instantaneaOperador.Fijar(ValoresOperador());

    /// <summary>
    /// «Salir y descartar» del aviso de navegación del modal de autorizar: el kit ya lo cierra (CerrarAutorizarOperador). Si la salida
    /// descartada vuelve a esta misma página con ?autorizar=, esa sugerencia tiene que volver a atenderse.
    /// </summary>
    private void AlDescartarAutorizarPorNavegacion() => _sugerenciaVista = null;

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

    /// <summary>
    /// Nombre de cada persona listada y el rol que se le enseña
    /// (<see cref="_rolMostradoPorAsignacionId"/>), que necesita su rol de Identity, el del
    /// Tenant de origen: un rol por cuenta; si hubiera dos, uno estable (mismo criterio que
    /// <c>DirectorioUsuariosTenant.ObtenerRolesDeCuentasAsync</c>).
    /// </summary>
    private Task CargarNombresDeOperadoresAsync(
        IEnumerable<(OperadorDelegadoDto Persona, bool EsSoporte, bool OperacionDelTenantEntero)> personas,
        CancellationToken cancellationToken) =>
        PuertaAccesoDatos.EjecutarAsync(async () =>
        {
            var filas = personas.ToArray();
            var cuentas = (await LecturaPersonas.ObtenerAsync(filas.Select(f => f.Persona.UsuarioId).Distinct().ToArray(), cancellationToken))
                .ToDictionary(u => u.Id);
            foreach (var (persona, esSoporte, operacionDelTenantEntero) in filas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var usuarioId = persona.UsuarioId;
                cuentas.TryGetValue(usuarioId, out var usuario);
                _nombresPorUsuarioId[usuarioId] = usuario is null
                    ? "Persona no encontrada"
                    : $"{usuario.NombreCompleto} ({usuario.Email})";

                _rolMostradoPorAsignacionId[persona.AsignacionId] = TenantsBeneficiariosAutorizados.RolEjercidoEnTenantBeneficiario(
                    esSoporte, operacionDelTenantEntero, persona.Rol, usuario?.Rol);
            }
        });

    /// <summary>
    /// Solo para los accesos de Operador CAE externo, que son los que llevan «Revocar
    /// acceso» o «Reactivar» (los de soporte usan otro criterio, ver
    /// <see cref="_esTenantOrigenPlataforma"/>): el activo pregunta si se puede revocar y el
    /// revocado si se puede reactivar. Fallo cerrado: un acceso sin entrada en el diccionario
    /// se trata como "no autorizado" en <see cref="PuedeRevocar"/> y
    /// <see cref="PuedeReactivar"/>, nunca como "sí".
    /// </summary>
    private Task CargarCriteriosDeRevocacionYReactivacionAsync(IEnumerable<(Guid Id, bool Activa)> entidades, CancellationToken token) =>
        PuertaAccesoDatos.EjecutarAsync(async () =>
        {
            var filas = entidades.ToArray();
            var permisos = await Mediator.Send(new CaeManager.Application.Tenants.PermisosAccesosDelegadosQuery(
                filas.Select(f => f.Id).Distinct().ToArray()), token);
            foreach (var (id, activa) in filas)
            {
                _puedeRevocarPorEntidad[id] = activa && permisos.TryGetValue(id, out var permisoRevocar)
                    && permisoRevocar.PuedeRevocar;
                _puedeReactivarPorEntidad[id] = !activa && permisos.TryGetValue(id, out var permisoReactivar)
                    && permisoReactivar.PuedeReactivar;
            }
        });

    /// <summary>Ver el doc-comment de <see cref="_esTenantOrigenPlataforma"/>.</summary>
    private bool PuedeAdministrarAccesoSoporte(bool somosLaConsultora) =>
        _esTenantOrigenPlataforma && somosLaConsultora;

    private bool PuedeReactivar(Guid entidadId) =>
        _puedeReactivarPorEntidad.GetValueOrDefault(entidadId);

    private bool PuedeRevocar(Guid entidadId) =>
        _puedeRevocarPorEntidad.GetValueOrDefault(entidadId);

    private string NombreDeUsuario(Guid usuarioId) =>
        _nombresPorUsuarioId.GetValueOrDefault(usuarioId, "…");

    private bool EsUsuarioNoResuelto(Guid usuarioId) =>
        _nombresPorUsuarioId.TryGetValue(usuarioId, out var nombre) && nombre == "Persona no encontrada";

    private static string NombreRol(string rol) => Roles.NombreVisible(rol);

    private string RolCaeMostrado(OperadorDelegadoDto persona) =>
        NombreRol(_rolMostradoPorAsignacionId.GetValueOrDefault(persona.AsignacionId, persona.Rol));
    private static string TituloDe(bool somosLaConsultora, string clienteNombre, string consultoraNombre) =>
        somosLaConsultora ? $"Gestionamos a {clienteNombre}" : $"{consultoraNombre} nos gestiona";

    private int PrepararEntidad(Guid id)
    {
        if (_entidadActiva != id)
        {
            _entidadActiva = id;
            _generacionEntidad++;
            _cargandoActividad = false;
            _delegacionARevocar = null;
            _operadorARetirar = null;
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

    private void CerrarRetirada(bool visible)
    {
        if (!visible && !EstaProcesando(_operadorARetirar?.Operador.AsignacionId))
        {
            _operadorARetirar = null;
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

    private async Task VerActividadAsync(AsignacionLista delegacion)
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

    private void AbrirFormularioSoporte(AsignacionLista delegacion)
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
        _instantaneaSoporte.Fijar(ValoresSoporte());
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

    private async Task CerrarAccesoSoporteAsync(AsignacionLista delegacion) =>
        await EjecutarParaEntidadAsync(
            delegacion.Id,
            delegacion.Id,
            () => Mediator.Send(new CerrarAccesoSoporteCommand(delegacion.Id)),
            "Acceso de soporte cerrado.");

    private async Task ReactivarAsync(AsignacionLista delegacion) =>
        await EjecutarParaEntidadAsync(
            delegacion.Id,
            delegacion.Id,
            () => Mediator.Send(new ReactivarDelegacionTenantCommand(delegacion.Id)),
            "Delegación reactivada.");

    // Retirar a una persona le quita el acceso sin vuelta atrás: se confirma
    // antes, como revocar la delegación entera.
    private void PedirRetirarOperador(Guid entidadId, OperadorDelegadoDto operador)
    {
        if (EstaProcesando(operador.AsignacionId))
        {
            return;
        }

        PrepararEntidad(entidadId);
        _operadorARetirar = (entidadId, operador);
    }

    private async Task RetirarOperadorAsync()
    {
        if (_operadorARetirar is not { } aRetirar)
        {
            return;
        }

        await EjecutarParaEntidadAsync(
            aRetirar.EntidadId,
            aRetirar.Operador.AsignacionId,
            () => Mediator.Send(new RevocarAsignacionOperadorDelegadoCommand(aRetirar.Operador.AsignacionId)),
            "Persona retirada de la delegación.");
        _operadorARetirar = null;
    }

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

    // Listado nuevo (lista-design): estado de la lista, filtros y agrupación.
    [Inject] private NavigationManager NavegacionLista { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosAsignacionesOperacion> TextosLista { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "q")] public string? BusquedaLista { get; set; }
    [SupplyParameterFromQuery(Name = "estado")] public string? EstadoLista { get; set; }
    [SupplyParameterFromQuery(Name = "tipo")] public string? TipoLista { get; set; }
    [SupplyParameterFromQuery(Name = "agrupar")] public string? AgrupacionLista { get; set; }

    private readonly ConexionFiltrosGuardados _filtrosGuardados = new();
    private readonly ConexionVistaRecordada _vistaRecordada = new();
    private readonly GruposAbiertos _gruposLista = new();
    private readonly HashSet<Guid> _detallesLista = [];
    private readonly HashSet<Guid> _personasCompletas = [];

    private int _paginaLista = 1;
    private int _tamanoLista = 20;
    private bool Agrupada => AgrupacionLista == "tipo";
    private string? _parametrosListaVistos;
    private void SincronizarParametrosLista()
    {
        EstadoLista = SeleccionEstados.Unir(SeleccionEstados.Separar(EstadoLista).Where(v => EstadosLista.Any(o => o.Valores.Contains(v))));
        if (TipoLista is not ("Soporte" or "OperadorExterno")) TipoLista = null;
        if (AgrupacionLista != "tipo") AgrupacionLista = null;
        var firma = System.Text.Json.JsonSerializer.Serialize(new[] { BusquedaLista, EstadoLista, TipoLista, AgrupacionLista });
        if (firma == _parametrosListaVistos) return;
        _parametrosListaVistos = firma;
        _paginaLista = 1;

        _detallesLista.Clear();
        _personasCompletas.Clear();
    }

    private string TipoDeLista(AsignacionLista d) => d.EsSoporte ? "Soporte" : "OperadorExterno";
    private string EstadoDeLista(AsignacionLista d) => d.EsSoporte
        ? d.AccesoVigente ? "Abierto" : d.VentanaCaducada ? "Caducada" : "SinAcceso"
        : d.Activa ? "Activa" : "Revocada";
    private string NombreLista(AsignacionLista d) => d.SomosLaConsultora ? d.ClienteNombre : d.ConsultoraNombre;
    private int TotalPaginasLista => Agrupada ? 1 : Math.Max(1, (int)Math.Ceiling((double)FilasFiltradas.Count / _tamanoLista));
    private string TextoPersonasLista(int cantidad) => cantidad == 1 ? TextosLista["Persona"] : TextosLista["Personas", cantidad];
    private string PermisoSoporteLista(AsignacionLista d)
    {
        var permiso = string.Join(", ", d.Operadores.Select(RolCaeMostrado).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct());
        return string.IsNullOrWhiteSpace(permiso) ? "—" : permiso;
    }
    private TonoBadge TonoEstadoLista(AsignacionLista d) => EstadosLista.Single(e => e.Valores.Contains(EstadoDeLista(d))).Tono;
    private string TextoEstadoLista(AsignacionLista d) => TextosLista[EstadoDeLista(d)];
    private IEnumerable<AsignacionLista> FilasSinEstado => _delegaciones.Where(d =>
        (string.IsNullOrWhiteSpace(BusquedaLista) || NombreLista(d).Contains(BusquedaLista.Trim(), StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrEmpty(TipoLista) || TipoDeLista(d) == TipoLista));
    private IReadOnlyList<AsignacionLista> FilasFiltradas => FilasSinEstado.Where(d =>
        string.IsNullOrEmpty(EstadoLista) || SeleccionEstados.Separar(EstadoLista).Contains(EstadoDeLista(d))).ToList();
    private IReadOnlyDictionary<string, int> RecuentosLista => FilasSinEstado.GroupBy(EstadoDeLista).ToDictionary(g => g.Key, g => g.Count());
    private IReadOnlyList<OpcionFranjaEstado> EstadosLista => [
        new(TextosLista["Abierto"], TonoBadge.Advertencia, "Abierto"),
        new(TextosLista["Activa"], TonoBadge.Exito, "Activa"),
        new(TextosLista["Caducada"], TonoBadge.Neutro, "Caducada"),
        new(TextosLista["SinAcceso"], TonoBadge.Neutro, "SinAcceso"),
        new(TextosLista["Revocada"], TonoBadge.Neutro, "Revocada")];
    private IReadOnlyList<OpcionEstado> TiposLista => [new("OperadorExterno", TextosLista["OperadorExterno"]), new("Soporte", TextosLista["Soporte"])];
    private IReadOnlyList<OpcionEstado> AgrupacionesLista => [new("tipo", TextosLista["Tipo"])];
    private IEnumerable<FilaDeGrupo<AsignacionLista>> BloquesLista => GruposDeLista.Agrupar(
        Agrupada ? FilasFiltradas : FilasFiltradas.Skip((_paginaLista - 1) * _tamanoLista).Take(_tamanoLista),
        Agrupada, TipoDeLista, (clave, _) => TextosLista[clave], _ => false, _gruposLista);
    private IReadOnlyList<AsignacionLista> FilasVisiblesLista => BloquesLista.Where(f => !f.Oculta).Select(f => f.Fila).ToList();
    private void AlternarGrupoLista(string clave) => _gruposLista.Alternar(clave);
    private void AlternarDetalleLista(Guid id) { if (!_detallesLista.Remove(id)) _detallesLista.Add(id); }
    private void CambiarPaginaLista(int pagina) => _paginaLista = Math.Clamp(pagina, 1, TotalPaginasLista);
    private void CambiarTamanoLista(int tamano) { _tamanoLista = tamano; CambiarPaginaLista(1); }
    private Task BuscarListaAsync(string valor) => CambiarFiltroLista("q", valor);
    private Task CambiarEstadoListaAsync(string? valor) => CambiarFiltroLista("estado", valor);
    private Task CambiarTipoListaAsync(string valor) => CambiarFiltroLista("tipo", valor);
    private Task CambiarAgrupacionListaAsync(string valor) => CambiarFiltroLista("agrupar", valor);
    private Task LimpiarFiltrosListaAsync() => AplicarVistaListaAsync(new Dictionary<string, string?> { ["agrupar"] = AgrupacionLista });
    private Task CambiarFiltroLista(string clave, string? valor) => AplicarVistaListaAsync(new Dictionary<string, string?>
    {
        ["q"] = BusquedaLista,
        ["estado"] = EstadoLista,
        ["tipo"] = TipoLista,
        ["agrupar"] = AgrupacionLista,
        [clave] = valor
    });
    private Task AplicarVistaListaAsync(IReadOnlyDictionary<string, string?> vista)
    {
        BusquedaLista = vista.GetValueOrDefault("q");
        EstadoLista = SeleccionEstados.Unir(SeleccionEstados.Separar(vista.GetValueOrDefault("estado")).Where(v => EstadosLista.Any(o => o.Valores.Contains(v))));
        TipoLista = vista.GetValueOrDefault("tipo") is "Soporte" or "OperadorExterno" ? vista["tipo"] : null;
        AgrupacionLista = vista.GetValueOrDefault("agrupar") == "tipo" ? "tipo" : null;

        _detallesLista.Clear();
        _personasCompletas.Clear();
        _paginaLista = 1;
        NavegacionLista.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        { ["q"] = BusquedaLista, ["estado"] = EstadoLista, ["tipo"] = TipoLista, ["agrupar"] = AgrupacionLista });
        StateHasChanged();
        return Task.CompletedTask;
    }
}
