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
            var roles = new List<RolInfoDto>();
            foreach (var nombreRol in CaeManager.Infrastructure.Identity.Roles.Todos)
            {
                var usuariosEnRol = await UserManager.GetUsersInRoleAsync(nombreRol);
                roles.Add(new RolInfoDto(
                    CaeManager.Infrastructure.Identity.Roles.NombreVisible(nombreRol), DescripcionesPorRol[nombreRol], usuariosEnRol.Count));
            }

            _roles = roles;
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
        var pendientes = new List<UsuarioPendienteDto>();
        foreach (var usuario in UserManager.Users.OrderBy(u => u.FechaCreacion).ToList())
        {
            var roles = await UserManager.GetRolesAsync(usuario);
            if (roles.Count == 0)
                pendientes.Add(new UsuarioPendienteDto(usuario.Id, usuario.Email ?? string.Empty, usuario.NombreCompleto, usuario.FechaCreacion));
        }

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
            var usuario = await UserManager.FindByIdAsync(pendiente.Id.ToString());
            if (usuario is null)
            {
                ToastService.Mostrar("No encontramos este usuario.", TonoToast.Error);
                await CargarAsync();
                return;
            }

            var rol = RolElegido(pendiente.Id);
            var resultado = await UserManager.AddToRoleAsync(usuario, rol);
            if (!resultado.Succeeded)
            {
                ToastService.Mostrar(string.Join(" ", resultado.Errors.Select(e => e.Description)), TonoToast.Error);
                return;
            }

            ToastService.Mostrar(
                $"Rol \"{CaeManager.Infrastructure.Identity.Roles.NombreVisible(rol)}\" asignado a {usuario.NombreCompleto}.", TonoToast.Exito);

            if (!string.IsNullOrWhiteSpace(usuario.Email))
                await NotificarUsuarioRolAsignadoAsync(usuario.Email, usuario.NombreCompleto, rol);

            _rolElegidoPorUsuario.Remove(pendiente.Id);
            await CargarAsync();
        }
        finally
        {
            _asignandoId = null;
        }
    }

    private async Task NotificarUsuarioRolAsignadoAsync(string email, string nombreCompleto, string rol)
    {
        // Plantilla mínima a propósito — contenido/diseño final pendiente de
        // definir con el usuario (ver ROADMAP.md). Best-effort: un fallo de
        // envío no debe deshacer la asignación de rol, que ya se guardó.
        var cuerpo = $"""
            <p>Hola {System.Net.WebUtility.HtmlEncode(nombreCompleto)},</p>
            <p>Tu acceso a CAE Manager ya está activo, con el rol <strong>{System.Net.WebUtility.HtmlEncode(CaeManager.Infrastructure.Identity.Roles.NombreVisible(rol))}</strong>.</p>
            <p>Ya puedes iniciar sesión con tu cuenta de Microsoft.</p>
            """;

        var resultado = await EmailService.EnviarAsync(email, "Tu acceso a CAE Manager ya está activo", cuerpo);
        if (resultado.EsFallido)
            Logger.LogWarning("No se pudo enviar el correo de confirmación de rol a {Email}.", email);
    }
}
