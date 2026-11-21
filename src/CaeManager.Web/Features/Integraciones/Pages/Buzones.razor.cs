using System.Globalization;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.FijarAcuseAutomaticoBuzon;
using CaeManager.Application.Integraciones.Commands.FijarResponsableBuzon;
using CaeManager.Application.Integraciones.Commands.ReactivarConexion;
using CaeManager.Application.Integraciones.Queries.ComprobarAccesoBuzon;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Integraciones.Queries.ObtenerResponsablesElegiblesBuzones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Integraciones;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.Integraciones.Pages;

/// <summary>
/// Administración de los buzones de Microsoft 365 (maqueta <c>Buzones.dc.html</c>): el listado con su Responsable y el
/// interruptor del acuse automático, «Añadir buzón» (el mismo flujo de conectar que Conexiones), «Reconectar» un buzón con error
/// y «Comprobar acceso» en las demás filas; desconectar sigue en Conexiones. La página no es una frontera de
/// autorización: <c>FijarAcuseAutomaticoBuzonCommand</c> y <c>FijarResponsableBuzonCommand</c> exigen Administrador y buzón
/// visible por su cuenta; aquí solo se decide qué se enseña (quien no es Administrador ve el estado, sin controles).
/// </summary>
public partial class Buzones : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase, IDisposable
{
    private const string RolAdministrador = Roles.Administrador;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IActividadBuzones ActividadBuzones { get; set; } = default!;
    [Inject] private IBuzonesDeTenantsBeneficiarios BuzonesDeTenants { get; set; } = default!;
    [Inject] private ITenantActual TenantActual { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<Buzones> Logger { get; set; } = default!;
    [Inject] private DirectorioUsuariosTenant DirectorioUsuarios { get; set; } = default!;
    [Inject] private NavigationManager Navegacion { get; set; } = default!;
    [Inject] private IOptions<Microsoft365GraphOptions> OpcionesMicrosoft365 { get; set; } = default!;

    /// <summary>
    /// Sin la URL pública de la aplicación el endpoint de conectar no puede arrancar el flujo OAuth (no hay redirect_uri): no
    /// se ofrece «Añadir buzón» y la página lo explica. Es la misma condición que comprueba el endpoint.
    /// </summary>
    // shortcut: el aviso solo mira UrlPublicaBase; sin ClientId o sin credencial el botón sigue y el endpoint vuelve con
    // «configuracion». Mejorarlo si un entorno llega a tener la URL pública sin el resto (mirar EstaConfigurado aquí y en Conexiones).
    private bool ConexionDisponible => !string.IsNullOrWhiteSpace(OpcionesMicrosoft365.Value.UrlPublicaBase);

    private IReadOnlyList<ConexionIntegracionListaDto> _conexiones = [];
    private IReadOnlyList<CuentaVisibleEnRol> _candidatos = [];
    private IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> _elegibles = new Dictionary<Guid, IReadOnlySet<Guid>>();
    private IReadOnlyDictionary<Guid, string> _nombres = new Dictionary<Guid, string>();
    private IReadOnlyDictionary<Guid, ActividadBuzonDto> _actividad = new Dictionary<Guid, ActividadBuzonDto>();
    private bool _cargando = true;
    private bool _cargaFallida;
    private Guid? _procesandoId;
    private readonly CancellationTokenSource _cancelacion = new();
    private long _generacionCarga;
    private bool _dispuesto;
    private int _versionSelectores;

    // El acuse envía correo externo: activarlo pide confirmar, con las reglas a la vista. Desactivarlo no.
    private ConexionIntegracionListaDto? _acuseAActivar;
    private bool _guardandoAcuse;

    /// <summary>El texto del acuse es fijo y mientras sea borrador (pendiente de aprobación) se dice al activarlo.</summary>
    private static bool TextoDelAcuseEsBorrador => PlantillaAcuseBuzon.EsBorrador;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            await CargarAsync(_cancelacion.Token);
            await CargarEmpresasAsync(_cancelacion.Token);
        }
        catch (OperationCanceledException) when (_cancelacion.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _cargaFallida = true;
            Logger.LogError(ex, "Error al cargar los buzones.");
        }
        finally
        {
            if (!_dispuesto) _cargando = false;
        }
    }

    // ---- Empresa gestionada (lienzo, tablero de Buzones, L-55) ----
    // Las pastillas son una coordenada que solo estrecha: los Tenants los da el servidor por la vista unificada de
    // Comunicaciones, y cada consulta vuelve a pasar por su entrada y por el ámbito autorizado del Tenant, bajo su RLS
    // (IBuzonesDeTenantsBeneficiarios). Los buzones de otra Empresa gestionada se ven en solo consulta: ninguna acción
    // de la página se ofrece sobre ellos, porque sus comandos actúan en el Tenant activo.

    private IReadOnlyList<TenantBeneficiarioDeBuzonesDto> _empresas = [];
    private TenantBeneficiarioDeBuzonesDto? _empresaConsultada;
    private bool _cargandoConsulta;
    private bool _consultaFallida;

    /// <summary>Se ven los buzones de otra Empresa gestionada, no los del Tenant activo.</summary>
    private bool SoloConsulta => _empresaConsultada is not null;

    /// <summary>
    /// Solo hay pastillas si el conjunto tiene la Empresa gestionada activa y alguna otra. Si la activa no está en el
    /// conjunto (el Tenant de origen sin gestionar, por ejemplo), no habría pastilla que marcar ni nombre que poner en el
    /// título, así que la página queda como en main (hueco declarado de PR-S5: la pastilla del Tenant activo fuera del
    /// conjunto).
    /// </summary>
    private bool MuestraEmpresas =>
        _empresas.Any(e => e.TenantId == TenantActual.TenantId) && _empresas.Any(e => e.TenantId != TenantActual.TenantId);

    private TenantBeneficiarioDeBuzonesDto? EmpresaVisible =>
        _empresaConsultada ?? _empresas.FirstOrDefault(e => e.TenantId == TenantActual.TenantId);

    /// <summary>La Empresa gestionada que nombran el título y el subtítulo: solo cuando hay pastillas entre las que elegir.</summary>
    private TenantBeneficiarioDeBuzonesDto? EmpresaRotulada => MuestraEmpresas ? EmpresaVisible : null;

    private string TituloVisible =>
        EmpresaRotulada is { } empresa ? Textos["TituloDeEmpresa", empresa.TenantNombre] : Textos["Titulo"];

    /// <summary>
    /// Las Empresas gestionadas del selector. Fuera de la vista unificada (papel Inbound, Sesión Privilegiada, Usuario de
    /// Cliente, Soporte TALVEG) el servidor la rechaza y la página sigue como siempre, sin pastillas; un fallo, igual.
    /// </summary>
    private async Task CargarEmpresasAsync(CancellationToken token)
    {
        try
        {
            _empresas = await BuzonesDeTenants.ObtenerTenantsAsync(token);
        }
        catch (UnauthorizedAccessException)
        {
            _empresas = [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Error al cargar las Empresas gestionadas de los buzones.");
            _empresas = [];
        }
    }

    /// <summary>
    /// Pulsar otra Empresa gestionada carga sus buzones en solo consulta; pulsar la activa o la que ya se consulta vuelve a
    /// los del Tenant activo. No se cambia mientras se procesa una acción sobre un buzón.
    /// </summary>
    private async Task ElegirEmpresaAsync(TenantBeneficiarioDeBuzonesDto empresa)
    {
        // Ni con una acción en curso ni con el modal del acuse o el cajón de añadir abiertos: los dos actúan en el Tenant
        // activo y su recarga no debe cruzarse con la consulta de otra Empresa gestionada.
        if (_procesandoId is not null || _guardandoAcuse || _acuseAActivar is not null || _cajonVisible || _dispuesto) return;
        if (empresa.TenantId == TenantActual.TenantId || empresa.TenantId == _empresaConsultada?.TenantId)
        {
            await VolverAlTenantActivoAsync();
            return;
        }

        var generacion = Interlocked.Increment(ref _generacionCarga);
        _empresaConsultada = empresa;
        _cargandoConsulta = true;
        _consultaFallida = false;
        _conexiones = [];
        try
        {
            var buzones = await BuzonesDeTenants.ObtenerBuzonesAsync(empresa.TenantId, _cancelacion.Token);
            if (generacion != _generacionCarga || _dispuesto) return;
            // Null: el servidor no lee ese Tenant para quien pregunta (la asignación caducó, por ejemplo). No se distingue
            // de un fallo: la página no afirma nada de sus buzones.
            _consultaFallida = buzones is null;
            _conexiones = buzones?.Conexiones ?? [];
            _nombres = buzones?.Nombres ?? new Dictionary<Guid, string>();
            _actividad = buzones?.Actividad ?? new Dictionary<Guid, ActividadBuzonDto>();
            _elegibles = new Dictionary<Guid, IReadOnlySet<Guid>>();
            _candidatos = [];
        }
        catch (OperationCanceledException) when (_cancelacion.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (generacion == _generacionCarga) _consultaFallida = true;
            Logger.LogError(ex, "Error al cargar los buzones de otra Empresa gestionada.");
        }
        finally
        {
            if (generacion == _generacionCarga && !_dispuesto) _cargandoConsulta = false;
        }
    }

    private async Task VolverAlTenantActivoAsync()
    {
        _empresaConsultada = null;
        _cargandoConsulta = false;
        _consultaFallida = false;
        _cargando = true;
        await RecargarSinPropagarAsync();
        if (!_dispuesto) _cargando = false;
    }

    /// <summary>
    /// Carga el listado y los nombres de quien responde. Una carga que falla no deja a la vista el listado anterior: sus
    /// estados pueden estar obsoletos y sus controles operarían sobre ellos, así que se vacía y la página avisa.
    /// </summary>
    private async Task CargarAsync(CancellationToken token)
    {
        var generacion = Interlocked.Increment(ref _generacionCarga);
        try
        {
            var conexiones = await Mediator.Send(new ObtenerConexionesIntegracionQuery(), token);
            var candidatos = await ObtenerCandidatosAsync(token);
            var ids = conexiones.SelectMany(c => new[] { c.ResponsableUsuarioId, c.GestorPropietarioId })
                .OfType<Guid>().Distinct().ToList();
            var nombres = await DirectorioUsuarios.ObtenerNombresVisiblesAsync(ids, token);

            // Solo los buzones compartidos tienen selector: uno personal responde siempre a su propietario. Para que lo
            // evalúe el servidor hace falta ser Administrador, y es quien ve el selector.
            var elegibles = await ObtenerElegiblesAsync(conexiones, candidatos, token);
            var actividad = await ObtenerActividadAsync(conexiones, token);
            // Mientras se consultan los buzones de otra Empresa gestionada, una recarga del Tenant activo (tras una acción
            // que empezó antes de elegirla) no los sustituye: quedarían rotulados con la Empresa consultada.
            if (generacion != _generacionCarga || token.IsCancellationRequested || SoloConsulta) return;
            _conexiones = conexiones;
            _candidatos = candidatos;
            _elegibles = elegibles;
            _nombres = nombres;
            _actividad = actividad;
            _cargaFallida = false;
        }
        catch (Exception) when (generacion == _generacionCarga && !token.IsCancellationRequested && !SoloConsulta)
        {
            _conexiones = [];
            _cargaFallida = true;
            throw;
        }
    }

    /// <summary>
    /// Todas las cuentas visibles en el Tenant activo, sin filtrar por rol: el universo sobre el que se pregunta. Quién entra
    /// lo decide solo <see cref="ResponsableBuzonElegible"/> en el servidor; un filtro por rol aquí sería una réplica de la regla
    /// y dejaría fuera a quien ella acepta por otra vía (una cuenta de un Operador CAE externo con rol de origen Consulta y rol
    /// guardado Gestor CAE en la vía heredada).
    /// </summary>
    private async Task<IReadOnlyList<CuentaVisibleEnRol>> ObtenerCandidatosAsync(CancellationToken token) =>
        (await DirectorioUsuarios.ObtenerCuentasVisiblesAsync(token))
            .DistinctBy(c => c.Id)
            .OrderBy(c => c.NombreCompleto, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> ObtenerElegiblesAsync(
        IReadOnlyList<ConexionIntegracionListaDto> conexiones, IReadOnlyList<CuentaVisibleEnRol> candidatos, CancellationToken token)
    {
        var compartidos = conexiones.Where(c => c.GestorPropietarioId is null).Select(c => c.Id).ToList();
        if (compartidos.Count == 0 || candidatos.Count == 0) return new Dictionary<Guid, IReadOnlySet<Guid>>();
        try
        {
            return await Mediator.Send(
                new ObtenerResponsablesElegiblesBuzonesQuery(compartidos, candidatos.Select(c => c.Id).ToList()), token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Es un auxiliar del selector: si falla, el listado y el interruptor del acuse siguen y cada selector ofrece solo a su
            // Responsable actual (OpcionesDeResponsable), en vez de dejar la página sin cargar.
            Logger.LogError(ex, "Error al calcular los responsables elegibles de los buzones.");
            return new Dictionary<Guid, IReadOnlySet<Guid>>();
        }
    }

    /// <summary>
    /// «Último correo» y «Abiertas» de cada buzón, en una sola petición para todos. Es un dato de lectura del listado: si falla,
    /// la tabla sigue y esas dos celdas enseñan «—» (no «0» ni «Sin correos visibles», que afirmarían algo que no se ha medido).
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, ActividadBuzonDto>> ObtenerActividadAsync(
        IReadOnlyList<ConexionIntegracionListaDto> conexiones, CancellationToken token)
    {
        if (conexiones.Count == 0) return new Dictionary<Guid, ActividadBuzonDto>();
        try
        {
            return await ActividadBuzones.ObtenerAsync(conexiones.Select(c => c.Id).ToList(), token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Error al calcular la actividad (último correo y conversaciones abiertas) de los buzones.");
            return new Dictionary<Guid, ActividadBuzonDto>();
        }
    }

    /// <summary>
    /// «Hoy, 08:31» / «Ayer, 17:40» (lienzo, con la hora en dos dígitos de D-19) y, para lo más antiguo, el formato único
    /// «15/11/2026 08:31» (D-19). Hora y día peninsulares
    /// (<see cref="DiaDeNegocio"/>), nunca la zona del servidor. Sin ningún correo visible para quien mira: «Sin correos
    /// visibles» (DN-M, 2026-11-20), nunca «aún» ni «ninguno», para no revelar si hay correo fuera de su alcance; sin dato
    /// (la petición falló o el buzón no está en la respuesta): «—».
    /// </summary>
    private string DescribirUltimoCorreo(Guid conexionId)
    {
        if (!_actividad.TryGetValue(conexionId, out var actividad)) return "—";
        if (actividad.UltimoCorreoUtc is not { } instanteUtc) return Textos["SinCorreosVisibles"];

        var local = instanteUtc.EnHoraPeninsular();
        var hora = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        var hoy = DiaDeNegocio.Hoy();
        var dia = DiaDeNegocio.De(instanteUtc);
        // Lo anterior a ayer no está en el lienzo: lleva el formato único de fecha y hora en pantalla (D-19), no uno propio.
        return dia == hoy ? Textos["UltimoCorreoHoy", hora]
            : dia == hoy.AddDays(-1) ? Textos["UltimoCorreoAyer", hora]
            : local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    private string DescribirAbiertas(Guid conexionId) =>
        _actividad.TryGetValue(conexionId, out var actividad)
            ? actividad.ConversacionesAbiertas.ToString(CultureInfo.CurrentCulture)
            : "—";

    private async Task RecargarSinPropagarAsync()
    {
        if (_dispuesto) return;
        try
        {
            await CargarAsync(_cancelacion.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException && _dispuesto)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al recargar los buzones.");
        }
    }

    private string NombreDe(Guid usuarioId) => _nombres.TryGetValue(usuarioId, out var nombre) ? nombre : "—";

    private string NombreResponsable(ConexionIntegracionListaDto conexion) =>
        conexion.ResponsableUsuarioId is { } responsableId ? NombreDe(responsableId) : Textos["SinResponsable"].Value;

    /// <summary>
    /// Las cuentas que <see cref="ResponsableBuzonElegible"/> acepta para este buzón (la regla de <c>FijarResponsableBuzonCommand</c>
    /// y de la ingesta, no una lista de roles propia) y, si el responsable actual ya no lo es (cambió de rol o de cartera), él
    /// también para no mostrar otro.
    /// </summary>
    private IEnumerable<(Guid Id, string NombreCompleto)> OpcionesDeResponsable(ConexionIntegracionListaDto conexion)
    {
        var opciones = _elegibles.TryGetValue(conexion.Id, out var ids)
            ? _candidatos.Where(c => ids.Contains(c.Id)).Select(c => (c.Id, c.NombreCompleto)).ToList()
            : [];
        if (conexion.ResponsableUsuarioId is { } actual && opciones.All(o => o.Id != actual))
            opciones.Add((actual, NombreDe(actual)));
        return opciones;
    }

    private string DescribirTipo(ConexionIntegracionListaDto conexion) =>
        conexion.GestorPropietarioId is not null ? Textos["TipoPersonal"]
        : conexion.EsBuzonCompartido ? Textos["TipoCompartido"]
        : Textos["TipoConectado"];

    /// <summary>Solo lectura: no hay comando que cambie a qué Empresa enruta un buzón.</summary>
    private string DescribirEnruta(ConexionIntegracionListaDto conexion) =>
        conexion.GestorPropietarioId is { } propietarioId ? Textos["EnrutaPersonal", NombreDe(propietarioId)]
        : conexion.ClienteNombre ?? Textos["EnrutaOrganizacionPropia"];

    private string EtiquetaAcuse(ConexionIntegracionListaDto conexion) =>
        conexion.AcuseAutomaticoActivo ? Textos["AcuseActivado"] : Textos["AcuseDesactivado"];

    /// <summary>Clase del punto de estado (E35, parcial): el color sale de los tokens de éxito, peligro y neutro de la hoja de estilos.</summary>
    private static string ClaseEstado(EstadoConexionIntegracion estado) => estado switch
    {
        EstadoConexionIntegracion.Habilitada => "exito",
        EstadoConexionIntegracion.ConError => "peligro",
        _ => "neutro"
    };

    private string ObtenerEtiquetaEstado(EstadoConexionIntegracion estado) => estado switch
    {
        EstadoConexionIntegracion.Habilitada => Textos["EstadoHabilitada"],
        EstadoConexionIntegracion.ConError => Textos["EstadoConError"],
        _ => Textos["EstadoDeshabilitada"]
    };

    // ---- Añadir buzón (cajón) ----
    // La elección de propietario y la URL de conectar son las de Conexiones (SeleccionPropietarioBuzon + CamposPropietarioBuzon):
    // el cajón no arranca nada por sí mismo, navega a la misma URL y es el endpoint quien autoriza, valida y consume el nonce.

    private readonly SeleccionPropietarioBuzon _propietario = new();
    private OpcionesPropietarioBuzon _opcionesCajon = OpcionesPropietarioBuzon.Vacias;
    private bool _cajonVisible;
    private bool _cargandoCajon;
    private string? _errorCajon;
    private bool _saliendoAMicrosoft;
    private bool _navegacionAMicrosoftLanzada;

    private async Task AbrirCajonAsync()
    {
        if (!ConexionDisponible) return;
        _propietario.Limpiar();
        _saliendoAMicrosoft = false;
        _navegacionAMicrosoftLanzada = false;
        _errorCajon = null;
        _cajonVisible = true;
        _cargandoCajon = true;
        StateHasChanged();
        try
        {
            // Se cargan al abrir y no con la página: quien no añade buzones no paga estas consultas.
            // shortcut: se recargan en cada apertura, sin caché, y la carga no se cancela al cerrar el cajón (solo al salir de la
            // página). Mejorarlo si el cajón se abre a menudo o las listas crecen (caché por visita o selector con búsqueda).
            _opcionesCajon = await OpcionesPropietarioBuzon.CargarAsync(Mediator, DirectorioUsuarios, _cancelacion.Token);
        }
        catch (OperationCanceledException) when (_cancelacion.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Sin las listas solo queda el buzón de la organización propia o un compartido: se dice y se puede seguir.
            Logger.LogError(ex, "Error al cargar las Empresas y los Gestores CAE del cajón de añadir buzón.");
            _opcionesCajon = OpcionesPropietarioBuzon.Vacias;
            _errorCajon = Textos["CajonErrorCarga"];
        }
        finally
        {
            if (!_dispuesto) _cargandoCajon = false;
        }
    }

    private Task CerrarCajon(bool visible)
    {
        if (!visible) _cajonVisible = false;
        return Task.CompletedTask;
    }

    private void RepintarCajon()
    {
    }

    /// <summary>Guardián del cajón: una elección hecha y todavía no enviada a Microsoft se pregunta antes de perderla.</summary>
    private bool HayEleccionSinEnviar() => !_saliendoAMicrosoft && _propietario.HayEleccion;

    /// <summary>
    /// «Continuar en Microsoft» va a la misma URL que Conexiones con página completa (la navegación mejorada haría un fetch al
    /// endpoint y la CSP bloquearía el salto a Microsoft) y sale de la página a propósito, así que primero deja de haber cambios pendientes y se repinta:
    /// el NavigationLock del kit consulta HayCambios al navegar, pero el aviso del navegador (beforeunload) lo registra el
    /// render, y navegar antes de que ese render llegue al cliente haría saltar «¿salir del sitio?». La navegación sale de
    /// <see cref="OnAfterRenderAsync"/>, después de ese render.
    /// </summary>
    private Task ContinuarAMicrosoftAsync()
    {
        if (!ConexionDisponible || _cargandoCajon || _saliendoAMicrosoft) return Task.CompletedTask;
        _saliendoAMicrosoft = true;
        StateHasChanged();
        return Task.CompletedTask;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_saliendoAMicrosoft || _navegacionAMicrosoftLanzada) return;
        _navegacionAMicrosoftLanzada = true;
        // Cede el turno para que el NavigationLock de este mismo render quite antes el aviso del navegador.
        await Task.Yield();
        Navegacion.NavigateTo(_propietario.UrlConectar, forceLoad: true);
    }

    // ---- Reconectar ----

    /// <summary>
    /// «Reconectar» es la misma acción que «Reactivar» de Conexiones: <c>ReactivarConexionCommand</c> vuelve a comprobar en
    /// Microsoft que el buzón sigue siendo el mismo y rehabilita la conexión; exige Administrador efectivo en el servidor.
    /// </summary>
    private async Task ReconectarAsync(ConexionIntegracionListaDto conexion)
    {
        if (_procesandoId is not null) return;
        _procesandoId = conexion.Id;
        StateHasChanged();
        try
        {
            var resultado = await Mediator.Send(new ReactivarConexionCommand(conexion.Id));
            if (resultado.EsFallido)
                ToastService.MostrarError(resultado.Error);
            else
                ToastService.Mostrar(Textos["ToastReconectado", conexion.Nombre], TonoToast.Exito);

            await RecargarSinPropagarAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al reconectar la conexión {ConexionId}.", conexion.Id);
            ToastService.Mostrar(Textos["ErrorReconectar"], TonoToast.Error);
            await RecargarSinPropagarAsync();
        }
        finally
        {
            _procesandoId = null;
        }
    }

    /// <summary>La franja de error con «Reconectar» (E32) sale solo con ConError y un último error que enseñar.</summary>
    private static bool MuestraFranjaError(ConexionIntegracionListaDto conexion) =>
        conexion.Estado == EstadoConexionIntegracion.ConError && !string.IsNullOrWhiteSpace(conexion.UltimoError);

    // ---- Comprobar acceso ----

    private Guid? _comprobandoId;

    /// <summary>
    /// «Comprobar acceso» de una fila (L-57): <c>ComprobarAccesoBuzonQuery</c> lee en Microsoft la Bandeja de entrada del buzón
    /// con el mismo token que lo abre, como «Reconectar», pero sin cambiar nada; exige Administrador efectivo en el servidor. El
    /// resultado es un aviso con los textos del lienzo y la tabla no se recarga, porque la comprobación no cambia la conexión.
    /// </summary>
    private async Task ComprobarAccesoAsync(ConexionIntegracionListaDto conexion)
    {
        if (_procesandoId is not null) return;
        _procesandoId = conexion.Id;
        _comprobandoId = conexion.Id;
        StateHasChanged();
        try
        {
            var resultado = await Mediator.Send(new ComprobarAccesoBuzonQuery(conexion.Id), _cancelacion.Token);
            if (resultado.EsFallido)
                ToastService.MostrarError(resultado.Error);
            else if (resultado.Valor == ResultadoComprobacionAccesoBuzon.Ok)
                ToastService.Mostrar(Textos["ComprobacionOk"], TonoToast.Exito);
            else
                ToastService.Mostrar(
                    Textos[resultado.Valor == ResultadoComprobacionAccesoBuzon.Ocupado ? "ComprobacionOcupado" : "ComprobacionSinAcceso"],
                    TonoToast.Error);
        }
        catch (OperationCanceledException) when (_dispuesto)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al comprobar el acceso a la conexión {ConexionId}.", conexion.Id);
            ToastService.Mostrar(Textos["ErrorComprobarAcceso"], TonoToast.Error);
        }
        finally
        {
            _procesandoId = null;
            _comprobandoId = null;
        }
    }

    // ---- Acuse automático ----

    private async Task AlternarAcuseAsync(ConexionIntegracionListaDto conexion)
    {
        if (_procesandoId is not null || _guardandoAcuse) return;

        if (!conexion.AcuseAutomaticoActivo)
        {
            _acuseAActivar = conexion;
            return;
        }

        await FijarAcuseAsync(conexion, activo: false);
    }

    private Task CerrarModalAcuse(bool visible)
    {
        if (visible || _guardandoAcuse) return Task.CompletedTask;
        _acuseAActivar = null;
        return Task.CompletedTask;
    }

    private async Task ConfirmarActivacionAcuseAsync()
    {
        if (_acuseAActivar is not { } conexion || _guardandoAcuse) return;
        _guardandoAcuse = true;
        try
        {
            await FijarAcuseAsync(conexion, activo: true);
        }
        finally
        {
            _guardandoAcuse = false;
            _acuseAActivar = null;
        }
    }

    private async Task FijarAcuseAsync(ConexionIntegracionListaDto conexion, bool activo)
    {
        _procesandoId = conexion.Id;
        StateHasChanged();
        try
        {
            var resultado = await Mediator.Send(new FijarAcuseAutomaticoBuzonCommand(conexion.Id, activo));
            if (resultado.EsFallido)
                ToastService.MostrarError(resultado.Error);
            else
                ToastService.Mostrar(
                    Textos[activo ? "ToastAcuseActivado" : "ToastAcuseDesactivado", conexion.Nombre], TonoToast.Exito);

            // Con éxito o sin él se recarga: el estado que se enseña es siempre el guardado.
            await RecargarSinPropagarAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cambiar el acuse automático de la conexión {ConexionId}.", conexion.Id);
            ToastService.Mostrar(Textos["ErrorAcuse"], TonoToast.Error);

            // Una excepción no dice si el comando llegó a guardar: el estado que se enseña tiene que volver del servidor, o la
            // fila seguiría mostrando el anterior (p. ej. «Desactivado» con el acuse ya enviando correo). Si la recarga también
            // falla, CargarAsync vacía el listado y la página avisa en vez de dejar controles sobre un estado dudoso.
            await RecargarSinPropagarAsync();
        }
        finally
        {
            _procesandoId = null;
        }
    }

    // ---- Responsable ----

    private async Task CambiarResponsableAsync(ConexionIntegracionListaDto conexion, string? valor)
    {
        if (_procesandoId is not null) return;
        Guid? responsable = Guid.TryParse(valor, out var id) ? id : null;
        if (responsable == conexion.ResponsableUsuarioId) return;

        _procesandoId = conexion.Id;
        StateHasChanged();
        try
        {
            var resultado = await Mediator.Send(new FijarResponsableBuzonCommand(conexion.Id, responsable));
            if (resultado.EsFallido)
                ToastService.MostrarError(resultado.Error);
            else
                ToastService.Mostrar(Textos["ToastResponsableActualizado", conexion.Nombre], TonoToast.Exito);

            await RecargarSinPropagarAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cambiar el responsable de la conexión {ConexionId}.", conexion.Id);
            ToastService.Mostrar(Textos["ErrorResponsable"], TonoToast.Error);

            // Misma razón que en el acuse: tras una excepción no se sabe si el Responsable llegó a cambiar.
            await RecargarSinPropagarAsync();
        }
        finally
        {
            _procesandoId = null;
            // El <select> conserva lo que el navegador eligió si el árbol de render no cambia (rechazo del servidor que deja el
            // mismo responsable): se recrea para que enseñe siempre lo guardado.
            _versionSelectores++;
        }
    }

    public void Dispose()
    {
        _dispuesto = true;
        _cancelacion.Cancel();
        _cancelacion.Dispose();
    }
}
