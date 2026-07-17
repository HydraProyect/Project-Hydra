using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Usuarios.Pages;

public record UsuarioListaDto(Guid Id, string Email, string NombreCompleto, string Rol, bool Activo);

public partial class Usuarios : ComponentBase
{
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private const int TamanoPagina = 20;

    private IReadOnlyList<UsuarioListaDto> _usuarios = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private Guid? _usuarioActualId;
    private int _pagina = 1;

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_usuarios.Count / (double)TamanoPagina));
    private IReadOnlyList<UsuarioListaDto> UsuariosDePagina => _usuarios.Skip((_pagina - 1) * TamanoPagina).Take(TamanoPagina).ToList();

    private Task IrAPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return Task.CompletedTask;
    }

    private bool _drawerVisible;
    private Guid? _editandoId;
    private string _email = string.Empty;
    private string _nombreCompleto = string.Empty;
    private string _password = string.Empty;
    private string _rol = Roles.Consulta;
    private bool _guardando;
    private string? _mensajeErrorFormulario;

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var idClaim = estadoAutenticacion.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            _usuarioActualId = Guid.TryParse(idClaim, out var id) ? id : null;

            var usuarios = new List<UsuarioListaDto>();
            foreach (var usuario in UserManager.Users.OrderBy(u => u.Email).ToList())
            {
                var roles = await UserManager.GetRolesAsync(usuario);
                var activo = usuario.LockoutEnd is null || usuario.LockoutEnd < DateTimeOffset.UtcNow;
                usuarios.Add(new UsuarioListaDto(
                    usuario.Id, usuario.Email ?? string.Empty, usuario.NombreCompleto, roles.FirstOrDefault() ?? "—", activo));
            }

            _usuarios = usuarios;
            _pagina = 1;
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private void AbrirCrear()
    {
        _editandoId = null;
        _email = string.Empty;
        _nombreCompleto = string.Empty;
        _password = string.Empty;
        _rol = Roles.Consulta;
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        var usuario = await UserManager.FindByIdAsync(id.ToString());
        if (usuario is null)
        {
            ToastService.Mostrar("No encontramos este usuario.", TonoToast.Error);
            await CargarAsync();
            return;
        }

        var roles = await UserManager.GetRolesAsync(usuario);

        _editandoId = usuario.Id;
        _email = usuario.Email ?? string.Empty;
        _nombreCompleto = usuario.NombreCompleto;
        _password = string.Empty;
        _rol = roles.FirstOrDefault() ?? Roles.Consulta;
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private async Task GuardarAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        StateHasChanged();

        try
        {
            if (_editandoId is null)
                await CrearUsuarioAsync();
            else
                await EditarUsuarioAsync(_editandoId.Value);
        }
        finally
        {
            _guardando = false;
        }
    }

    private async Task CrearUsuarioAsync()
    {
        if (string.IsNullOrWhiteSpace(_email) || string.IsNullOrWhiteSpace(_password) || string.IsNullOrWhiteSpace(_nombreCompleto))
        {
            _mensajeErrorFormulario = "Correo, nombre y contraseña son obligatorios.";
            return;
        }

        var usuario = new ApplicationUser
        {
            UserName = _email,
            Email = _email,
            NombreCompleto = _nombreCompleto,
            EmailConfirmed = true
        };

        var resultado = await UserManager.CreateAsync(usuario, _password);
        if (!resultado.Succeeded)
        {
            _mensajeErrorFormulario = string.Join(" ", resultado.Errors.Select(e => e.Description));
            return;
        }

        await UserManager.AddToRoleAsync(usuario, _rol);

        ToastService.Mostrar("Usuario creado correctamente.", TonoToast.Exito);
        _drawerVisible = false;
        await CargarAsync();
    }

    private async Task EditarUsuarioAsync(Guid id)
    {
        if (string.IsNullOrWhiteSpace(_nombreCompleto))
        {
            _mensajeErrorFormulario = "El nombre es obligatorio.";
            return;
        }

        var usuario = await UserManager.FindByIdAsync(id.ToString());
        if (usuario is null)
        {
            _mensajeErrorFormulario = "No encontramos este usuario.";
            return;
        }

        usuario.NombreCompleto = _nombreCompleto;
        await UserManager.UpdateAsync(usuario);

        var rolesActuales = await UserManager.GetRolesAsync(usuario);
        if (!rolesActuales.Contains(_rol))
        {
            await UserManager.RemoveFromRolesAsync(usuario, rolesActuales);
            await UserManager.AddToRoleAsync(usuario, _rol);
        }

        ToastService.Mostrar("Usuario actualizado correctamente.", TonoToast.Exito);
        _drawerVisible = false;
        await CargarAsync();
    }

    private async Task CambiarActivacionAsync(UsuarioListaDto usuarioLista)
    {
        if (usuarioLista.Id == _usuarioActualId)
        {
            ToastService.Mostrar("No puedes desactivar tu propia cuenta.", TonoToast.Error);
            return;
        }

        var usuario = await UserManager.FindByIdAsync(usuarioLista.Id.ToString());
        if (usuario is null) return;

        usuario.LockoutEnabled = true;
        usuario.LockoutEnd = usuarioLista.Activo ? DateTimeOffset.MaxValue : null;
        await UserManager.UpdateAsync(usuario);

        ToastService.Mostrar(
            usuarioLista.Activo ? "Usuario desactivado." : "Usuario reactivado.",
            TonoToast.Exito);

        await CargarAsync();
    }
}
