using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Features.GestionRoles.Pages;

public record RolInfoDto(string Nombre, string Descripcion, int CantidadUsuarios);

public record UsuarioPendienteDto(Guid Id, string Email, string NombreCompleto, DateTime FechaCreacion);

public partial class Roles : ComponentBase
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

    protected override Task OnInitializedAsync() => CargarAsync();

    private void CambiarPestana(string pestana) => _pestanaActiva = pestana;

    private string ClasePestana(string pestana) => _pestanaActiva == pestana ? "pestana-rol pestana-rol-activa" : "pestana-rol";

    private async Task CargarAsync()
    {
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
            var cantidadesPorRol = await DirectorioUsuarios.ContarCuentasPropiasPorRolAsync();

            _roles = [.. CaeManager.Infrastructure.Identity.Roles.Todos.Select(nombreRol =>
                new RolInfoDto(
                    CaeManager.Infrastructure.Identity.Roles.NombreVisible(nombreRol),
                    DescripcionesPorRol[nombreRol],
                    cantidadesPorRol.GetValueOrDefault(nombreRol)))];

            await CargarPendientesAsync();
        }
        catch (Exception)
        {
            _error = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private async Task CargarPendientesAsync()
    {
        var pendientes = (await DirectorioUsuarios.ObtenerCuentasPropiasSinRolAsync())
            .Select(u => new UsuarioPendienteDto(u.Id, u.Email ?? string.Empty, u.NombreCompleto, u.FechaCreacion))
            .ToList();

        _usuariosPendientes = pendientes;
        foreach (var pendiente in pendientes)
            _rolElegidoPorUsuario.TryAdd(pendiente.Id, CaeManager.Infrastructure.Identity.Roles.Consulta);
    }

    private string RolElegido(Guid usuarioId) =>
        _rolElegidoPorUsuario.TryGetValue(usuarioId, out var rol) ? rol : CaeManager.Infrastructure.Identity.Roles.Consulta;

    private void CambiarRolElegido(Guid usuarioId, string rol) => _rolElegidoPorUsuario[usuarioId] = rol;

    private async Task AsignarRolAsync(UsuarioPendienteDto pendiente)
    {
        _asignandoId = pendiente.Id;
        StateHasChanged();

        try
        {
            var rol = RolElegido(pendiente.Id);

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
            if (!await DirectorioUsuarios.EsCuentaPropiaDelTenantActualAsync(pendiente.Id))
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
        finally
        {
            _asignandoId = null;
        }
    }

    private async Task NotificarUsuarioRolAsignadoAsync(Guid usuarioId, string email, string nombreCompleto, string rol)
    {
        // Plantilla mínima a propósito — contenido/diseño final pendiente de
        // definir con el usuario (ver ROADMAP.md). Best-effort: un fallo de
        // envío no debe deshacer la asignación de rol, que ya se guardó.
        var cuerpo = $"""
            <p>Hola {System.Net.WebUtility.HtmlEncode(nombreCompleto)},</p>
            <p>Tu acceso a {Marca.Nombre} ya está activo, con el rol <strong>{System.Net.WebUtility.HtmlEncode(CaeManager.Infrastructure.Identity.Roles.NombreVisible(rol))}</strong>.</p>
            <p>Ya puedes iniciar sesión con tu cuenta de Microsoft.</p>
            """;

        var resultado = await EmailService.EnviarAsync(email, $"Tu acceso a {Marca.Nombre} ya está activo", cuerpo);
        if (resultado.EsFallido)
            Logger.LogWarning("No se pudo enviar el correo de confirmación de rol a {UsuarioId}.", usuarioId);
    }
}
