using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.GestionRoles.Pages;

public record RolInfoDto(string Nombre, string Descripcion, int CantidadUsuarios);

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

    private IReadOnlyList<RolInfoDto> _roles = [];
    private bool _cargando = true;
    private bool _error;

    protected override Task OnInitializedAsync() => CargarAsync();

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
}
