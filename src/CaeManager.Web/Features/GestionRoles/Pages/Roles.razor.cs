using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Features.GestionRoles.Pages;

public record RolInfoDto(string Nombre, string Descripcion, int CantidadUsuarios);

public record UsuarioPendienteDto(Guid Id, string Email, string NombreCompleto, DateTime FechaCreacion);

public partial class Roles : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase
{
    // Jerarquía de alcance de datos (ver Roles.cs y IAlcanceDatosService) —
    // no es un catálogo editable, cada uno corresponde a un nivel fijo de
    // visibilidad ya implementado en Dashboard, las consultas de listado,
    // etc. Esta página es de referencia y gobierno, no un CRUD de roles.
    private static readonly Dictionary<string, string> DescripcionesPorRol = new()
    {
        [CaeManager.Infrastructure.Identity.Roles.Administrador] =
            "Acceso completo: gestión de usuarios, roles, configuración y auditoría, además de todo el contenido operativo y de negocio, sin restricción de cartera.",
        [CaeManager.Infrastructure.Identity.Roles.DireccionCae] =
            "Ve todo el negocio igual que Administrador (Clientes, Empresas, Documentos, Visitas, Reportes…), sin acceso a las pantallas de configuración del sistema. Junto a Administrador, asigna Gestores CAE a cada Coordinador CAE.",
        [CaeManager.Infrastructure.Identity.Roles.CoordinadorCae] =
            "Ve la cartera combinada de los Gestores CAE que tiene asignados: comparativa de cumplimiento y rendimiento, y puede reasignar Clientes entre ellos (pantalla Supervisión).",
        [CaeManager.Infrastructure.Identity.Roles.GestorCae] =
            "Ve únicamente sus propios Clientes (creados o asignados) y todo lo asociado a ellos: Empresas, Trabajadores, Subcontratas, Asignaciones, Vehículos, Documentos, Reportes y Dashboard.",
        [CaeManager.Infrastructure.Identity.Roles.Consulta] =
            "Solo lectura de todos los datos de negocio, sin ninguna acción de creación, edición o eliminación.",
        [CaeManager.Infrastructure.Identity.Roles.Cliente] =
            "Solo lectura de su propia información: sus Trabajadores, Empresas, Centros y Subcontratas asociadas. Sin ningún tipo de edición."
    };

    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private CaeManager.Infrastructure.Autorizacion.DirectorioUsuariosTenant DirectorioUsuarios { get; set; } = default!;
    [Inject] private IEmailService EmailService { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<Roles> Logger { get; set; } = default!;

    private string _pestanaActiva = "roles";
    private IReadOnlyList<RolInfoDto> _roles = [];
    private bool _cargando = true;
    private bool _error;

    private IReadOnlyList<UsuarioPendienteDto> _usuariosPendientes = [];
    private readonly Dictionary<Guid, string> _rolElegidoPorUsuario = [];
    private Guid? _asignandoId;

    // Confirmación de la asignación: a quién y QUÉ rol. El rol se congela al
    // pedir la confirmación para que lo que se envía sea exactamente lo que el
    // diálogo acaba de decir.
    private UsuarioPendienteDto? _pendienteAConfirmar;
    private string _rolAConfirmar = CaeManager.Infrastructure.Identity.Roles.Consulta;

    // Cada CargarAsync toma un número; solo la última vigente escribe estado.
    // Hoy la interfaz casi no deja solaparlas —mientras carga no hay botones y
    // el diálogo es modal—, pero un segundo clic en «Reintentar» que llegue al
    // servidor antes de que el navegador reciba el repintado sí lo hace, y sin
    // esto la carga más antigua pisaría a la más nueva si respondiera después.
    private int _versionCarga;

    protected override Task OnInitializedAsync() => CargarAsync();

    // ── Fuente de datos ──────────────────────────────────────────────────
    // Las tres lecturas del directorio pasan por aquí. Son virtuales para que
    // los tests de bUnit controlen cuándo y con qué responden (el directorio
    // es una clase concreta sobre PostgreSQL, sin interfaz que lo cubra). La
    // página no decide nada en ellas: el acotado al tenant, y la diferencia
    // entre cuenta PROPIA y cuenta solo VISIBLE, siguen siendo del directorio.

    /// <summary>Cuántas cuentas propias del tenant activo tiene cada rol.</summary>
    protected virtual Task<IReadOnlyDictionary<string, int>> ContarCuentasPorRolAsync() =>
        DirectorioUsuarios.ContarCuentasPropiasPorRolAsync();

    /// <summary>Cuentas propias del tenant activo que todavía no tienen rol.</summary>
    protected virtual async Task<IReadOnlyList<UsuarioPendienteDto>> ObtenerPendientesAsync() =>
        (await DirectorioUsuarios.ObtenerCuentasPropiasSinRolAsync())
            .Select(u => new UsuarioPendienteDto(u.Id, u.Email ?? string.Empty, u.NombreCompleto, u.FechaCreacion))
            .ToList();

    /// <summary>
    /// Si la cuenta pertenece al tenant activo — propiedad, no visibilidad:
    /// ver <c>DirectorioUsuariosTenant.EsCuentaPropiaDelTenantActualAsync</c>.
    /// </summary>
    protected virtual Task<bool> EsCuentaPropiaAsync(Guid usuarioId) =>
        DirectorioUsuarios.EsCuentaPropiaDelTenantActualAsync(usuarioId);

    // ── Pestañas ─────────────────────────────────────────────────────────

    /// <summary>Orden visual de la tira: el que siguen las flechas, Inicio y Fin.</summary>
    private static readonly string[] OrdenPestanas = ["roles", "pendientes"];

    private readonly ElementReference[] _referenciasPestanas = new ElementReference[OrdenPestanas.Length];

    // La pestaña a la que una tecla acaba de mover la selección y que aún no
    // tiene el foco. El foco se da DESPUÉS del render, cuando la pestaña ya
    // lleva tabindex="0": darlo antes lo pondría en un botón que el propio
    // render está a punto de cambiar.
    private string? _pestanaPorEnfocar;

    private void CambiarPestana(string pestana) => _pestanaActiva = pestana;

    private void ManejarTeclaPestana(KeyboardEventArgs e, string pestanaActual)
    {
        var indiceActual = Array.IndexOf(OrdenPestanas, pestanaActual);
        var destino = e.Key switch
        {
            "ArrowRight" => (indiceActual + 1) % OrdenPestanas.Length,
            "ArrowLeft" => (indiceActual - 1 + OrdenPestanas.Length) % OrdenPestanas.Length,
            "Home" => 0,
            "End" => OrdenPestanas.Length - 1,
            _ => -1
        };

        if (destino < 0) return;

        CambiarPestana(OrdenPestanas[destino]);
        _pestanaPorEnfocar = OrdenPestanas[destino];
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_pestanaPorEnfocar is not { } pestana) return;

        _pestanaPorEnfocar = null;
        await _referenciasPestanas[Array.IndexOf(OrdenPestanas, pestana)].FocusAsync();
    }

    private static string IdPestana(string pestana) => $"pestana-roles-{pestana}";

    private static string IdPanel(string pestana) => $"panel-roles-{pestana}";

    private string TabIndex(string pestana) => _pestanaActiva == pestana ? "0" : "-1";

    private string ClasePestana(string pestana) => _pestanaActiva == pestana ? "pestana-rol pestana-rol-activa" : "pestana-rol";

    private string AriaSeleccionada(string pestana) => _pestanaActiva == pestana ? "true" : "false";

    private string EtiquetaAccesiblePendientes => $"{_usuariosPendientes.Count} pendientes de asignar";

    private string TituloPendientes =>
        $"{_usuariosPendientes.Count} pendiente(s) de asignar: " +
        string.Join("; ", _usuariosPendientes.Select(p => $"{p.NombreCompleto} ({p.FechaCreacion:dd/MM/yyyy HH:mm})"));

    // ── Carga ────────────────────────────────────────────────────────────

    private async Task CargarAsync()
    {
        var version = ++_versionCarga;
        _cargando = true;
        _error = false;
        StateHasChanged();

        try
        {
            // Acotado al tenant activo. Antes se recorrían los seis roles con
            // GetUsersInRoleAsync —que no filtra por tenant— y luego se
            // materializaba UserManager.Users entero para preguntar rol por
            // rol: los recuentos incluían usuarios de otras organizaciones y la
            // lista de pendientes mostraba su nombre y su correo. Ver
            // DirectorioUsuariosTenant, que ya existía para esto y que
            // /usuarios sí usaba.
            var cantidadesPorRol = await ContarCuentasPorRolAsync();
            if (version != _versionCarga) return;

            var pendientes = await ObtenerPendientesAsync();
            if (version != _versionCarga) return;

            _roles = [.. CaeManager.Infrastructure.Identity.Roles.Todos.Select(nombreRol =>
                new RolInfoDto(
                    CaeManager.Infrastructure.Identity.Roles.NombreVisible(nombreRol),
                    DescripcionesPorRol[nombreRol],
                    cantidadesPorRol.GetValueOrDefault(nombreRol)))];

            _usuariosPendientes = pendientes;
            foreach (var pendiente in pendientes)
                _rolElegidoPorUsuario.TryAdd(pendiente.Id, CaeManager.Infrastructure.Identity.Roles.Consulta);
        }
        catch (Exception ex)
        {
            if (version != _versionCarga) return;

            Logger.LogError(ex, "No se pudieron cargar los roles ni las cuentas pendientes de rol.");
            _error = true;
        }
        finally
        {
            if (version == _versionCarga)
                _cargando = false;
        }
    }

    private string RolElegido(Guid usuarioId) =>
        _rolElegidoPorUsuario.TryGetValue(usuarioId, out var rol) ? rol : CaeManager.Infrastructure.Identity.Roles.Consulta;

    private void CambiarRolElegido(Guid usuarioId, string rol) => _rolElegidoPorUsuario[usuarioId] = rol;

    // ── Confirmación ─────────────────────────────────────────────────────

    private void PedirConfirmacion(UsuarioPendienteDto pendiente)
    {
        _pendienteAConfirmar = pendiente;
        _rolAConfirmar = RolElegido(pendiente.Id);
    }

    private void AlCambiarVisibilidadConfirmacion(bool visible)
    {
        // Mientras se asigna el diálogo no se cierra: el resultado tiene que
        // llegar a alguien que lo esté mirando.
        if (!visible && _asignandoId is null)
            _pendienteAConfirmar = null;
    }

    private string TituloConfirmacion =>
        $"¿Asignar el rol {CaeManager.Infrastructure.Identity.Roles.NombreVisible(_rolAConfirmar)}?";

    private string MensajeConfirmacion => _pendienteAConfirmar is { } pendiente
        ? $"{pendiente.NombreCompleto} ({pendiente.Email}) tendrá el rol " +
          $"{CaeManager.Infrastructure.Identity.Roles.NombreVisible(_rolAConfirmar)} en esta organización y dejará " +
          "de ver la pantalla de espera. Lo que puede ver y hacer depende de ese rol."
        : string.Empty;

    private async Task ConfirmarAsignacionAsync()
    {
        if (_pendienteAConfirmar is not { } pendiente) return;

        try
        {
            await AsignarRolAsync(pendiente, _rolAConfirmar);
        }
        finally
        {
            _pendienteAConfirmar = null;
        }
    }

    // ── Asignación ───────────────────────────────────────────────────────

    private async Task AsignarRolAsync(UsuarioPendienteDto pendiente, string rol)
    {
        _asignandoId = pendiente.Id;
        StateHasChanged();

        try
        {
            // El rol llega de un <select> de la propia página, pero que la
            // interfaz solo ofrezca opciones válidas no impide enviar otra
            // cosa: sin esta comprobación, AddToRoleAsync aceptaría cualquier
            // nombre que exista en AspNetRoles.
            if (!CaeManager.Infrastructure.Identity.Roles.Todos.Contains(rol, StringComparer.Ordinal))
            {
                ToastService.Mostrar("Ese rol no existe.", TonoToast.Error);
                await CargarAsync();
                return;
            }

            // La autoridad se comprueba sobre la PROPIEDAD de la cuenta, no
            // sobre su visibilidad: un Operador Delegado se ve desde este
            // tenant, pero su cuenta pertenece a otra organización y su rol se
            // gobierna allí (ver DirectorioUsuariosTenant). Antes se recuperaba
            // por Guid con FindByIdAsync sin mirar el TenantId, así que el Id
            // de un usuario de otro tenant —que la propia lista de pendientes
            // llegaba a mostrar— bastaba para cambiarle el rol.
            if (!await EsCuentaPropiaAsync(pendiente.Id))
            {
                ToastService.Mostrar("No encontramos este usuario.", TonoToast.Error);
                await CargarAsync();
                return;
            }

            var usuario = await PuertaAccesoDatos.EjecutarAsync(
                () => UserManager.FindByIdAsync(pendiente.Id.ToString()));
            if (usuario is null)
            {
                ToastService.Mostrar("No encontramos este usuario.", TonoToast.Error);
                await CargarAsync();
                return;
            }

            var resultado = await PuertaAccesoDatos.EjecutarAsync(
                () => UserManager.AddToRoleAsync(usuario, rol));
            if (!resultado.Succeeded)
            {
                ToastService.Mostrar(string.Join(" ", resultado.Errors.Select(e => e.Description)), TonoToast.Error);
                return;
            }

            ToastService.Mostrar(
                $"Rol \"{CaeManager.Infrastructure.Identity.Roles.NombreVisible(rol)}\" asignado a {usuario.NombreCompleto}.", TonoToast.Exito);

            if (!string.IsNullOrWhiteSpace(usuario.Email))
                await NotificarUsuarioRolAsignadoAsync(usuario.Id, usuario.Email, usuario.NombreCompleto, rol);

            _rolElegidoPorUsuario.Remove(pendiente.Id);
            await CargarAsync();
        }
        catch (Exception ex)
        {
            // Una excepción sin capturar en un manejador de Blazor Server tumba
            // el circuito entero. Aquí no se sabe si el rol llegó a guardarse,
            // así que se avisa y se recarga: la lista dirá la verdad.
            Logger.LogError(ex, "No se pudo asignar el rol {Rol} a {UsuarioId}.", rol, pendiente.Id);
            ToastService.Mostrar("No pudimos asignar el rol. Revisa la lista e inténtalo de nuevo.", TonoToast.Error);
            await CargarAsync();
        }
        finally
        {
            _asignandoId = null;
        }
    }

    private async Task NotificarUsuarioRolAsignadoAsync(Guid usuarioId, string email, string nombreCompleto, string rol)
    {
        // Plantilla mínima a propósito — contenido/diseño final pendiente de
        // definir con el usuario (ver ROADMAP.md). Best-effort: un fallo de
        // envío no debe deshacer la asignación de rol, que ya se guardó — ni
        // tampoco hacerla pasar por fallida, que es lo que ocurría si el
        // servicio de correo lanzaba en vez de devolver un resultado fallido.
        var cuerpo = $"""
            <p>Hola {System.Net.WebUtility.HtmlEncode(nombreCompleto)},</p>
            <p>Tu acceso a {Marca.Nombre} ya está activo, con el rol <strong>{System.Net.WebUtility.HtmlEncode(CaeManager.Infrastructure.Identity.Roles.NombreVisible(rol))}</strong>.</p>
            <p>Ya puedes iniciar sesión con tu cuenta de Microsoft.</p>
            """;

        try
        {
            var resultado = await EmailService.EnviarAsync(email, $"Tu acceso a {Marca.Nombre} ya está activo", cuerpo);
            if (resultado.EsFallido)
                Logger.LogWarning("No se pudo enviar el correo de confirmación de rol a {UsuarioId}.", usuarioId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "No se pudo enviar el correo de confirmación de rol a {UsuarioId}.", usuarioId);
        }
    }
}
