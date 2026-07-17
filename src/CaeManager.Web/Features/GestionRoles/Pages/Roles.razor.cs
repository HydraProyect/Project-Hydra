using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.GestionRoles.Pages;

public record RolInfoDto(string Nombre, string Descripcion, int CantidadUsuarios);

public partial class Roles : ComponentBase
{
    // Los 4 roles están alineados 1:1 con los 4 dashboards del brief (ver
    // ARCHITECTURE.md) — no son un catálogo editable, cada uno corresponde
    // a un nivel fijo de visibilidad ya implementado en Dashboard, Centros,
    // Tipos de Documento, etc. Esta página es de referencia y gobierno, no
    // un CRUD de roles.
    private static readonly Dictionary<string, string> DescripcionesPorRol = new()
    {
        [CaeManager.Infrastructure.Identity.Roles.Administrador] =
            "Acceso completo: gestión de usuarios, roles, configuración y auditoría, además de todo el contenido operativo.",
        [CaeManager.Infrastructure.Identity.Roles.Supervisor] =
            "Visión operativa ampliada: documentos que requieren atención y centros con más riesgo, además de los módulos de negocio.",
        [CaeManager.Infrastructure.Identity.Roles.EjecutivoCae] =
            "Gestión diaria de CAE: documentos que requieren atención, además de los módulos de negocio.",
        [CaeManager.Infrastructure.Identity.Roles.Consulta] =
            "Solo lectura: los KPI base del Dashboard y los listados, sin acciones de administración."
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
                roles.Add(new RolInfoDto(nombreRol, DescripcionesPorRol[nombreRol], usuariosEnRol.Count));
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
