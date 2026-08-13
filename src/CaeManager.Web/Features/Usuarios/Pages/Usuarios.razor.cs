using System.Security.Claims;
using CaeManager.Application.Clientes.Queries.BuscarClientePorCif;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Web.Services;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Features.Usuarios.Pages;

public record UsuarioListaDto(Guid Id, string Email, string NombreCompleto, string Rol, bool Activo);

public record CoordinadorDto(Guid Id, string NombreCompleto, string Email);

public partial class Usuarios : ComponentBase
{
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private DirectorioUsuariosTenant DirectorioUsuarios { get; set; } = default!;
    [Inject] private ITenantActual TenantActual { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IEmailService EmailService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<Usuarios> Logger { get; set; } = default!;

    private int _tamanoPagina = 20;

    private IReadOnlyList<UsuarioListaDto> _usuarios = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private Guid? _usuarioActualId;
    private int _pagina = 1;

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_usuarios.Count / (double)_tamanoPagina));
    private IReadOnlyList<UsuarioListaDto> UsuariosDePagina => _usuarios.Skip((_pagina - 1) * _tamanoPagina).Take(_tamanoPagina).ToList();

    private Task IrAPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return Task.CompletedTask;
    }

    // H5 (docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _tamanoPagina = tamano;
        _pagina = 1;
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

    private IReadOnlyList<CoordinadorDto> _coordinadoresDisponibles = [];
    private string _coordinadorUsuarioId = string.Empty;

    private string _clienteCif = string.Empty;
    private ClientePorCifDto? _clienteEncontrado;
    private bool _buscandoCliente;

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
            // Acotado al tenant activo: UserManager.Users no filtra nada
            // (AspNetUsers es la única tabla sin filtro global) y esta pantalla
            // listaba los usuarios de todas las organizaciones con nombre y
            // correo. Ver DirectorioUsuariosTenant.
            // Por la puerta: UserManager no pasa por MediatR y esta carga corre
            // en paralelo con los componentes del layout (ver PuertaAccesoDatos).
            await PuertaAccesoDatos.EjecutarAsync(async () =>
            {
                // Para un Operador Delegado se muestra el rol de la asignación
                // (Consulta/GestorCae/CoordinadorCae), no su rol de origen: un
                // operador de soporte es Administrador en el tenant de
                // plataforma, pero CurrentUserService.ObtenerRolActualAsync ya
                // lo acota al operar aquí — mostrar "Administrador" contradice
                // esa restricción y alarma sin motivo a quien lo ve.
                var rolesDelegados = await DirectorioUsuarios.ObtenerRolesDeOperadoresDelegadosAsync();

                foreach (var usuario in await DirectorioUsuarios.ObtenerVisiblesAsync())
                {
                    var activo = usuario.LockoutEnd is null || usuario.LockoutEnd < DateTimeOffset.UtcNow;
                    string rol;
                    if (rolesDelegados.TryGetValue(usuario.Id, out var rolDelegado))
                        rol = rolDelegado;
                    else
                        rol = (await UserManager.GetRolesAsync(usuario)).FirstOrDefault() ?? "—";

                    usuarios.Add(new UsuarioListaDto(
                        usuario.Id, usuario.Email ?? string.Empty, usuario.NombreCompleto, rol, activo));
                }
            });

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

    private async Task CambiarRolAsync(string valor)
    {
        _rol = valor;

        if (_rol == Roles.GestorCae)
            await CargarCoordinadoresAsync();
    }

    private async Task CargarCoordinadoresAsync()
    {
        var coordinadores = await DirectorioUsuarios.ObtenerVisiblesEnRolAsync(Roles.CoordinadorCae);
        _coordinadoresDisponibles = coordinadores
            .Select(u => new CoordinadorDto(u.Id, u.NombreCompleto, u.Email ?? string.Empty))
            .ToList();
    }

    private async Task BuscarClientePorCifAsync(string valor)
    {
        _clienteCif = valor;
        _clienteEncontrado = null;

        if (string.IsNullOrWhiteSpace(valor)) return;

        _buscandoCliente = true;
        StateHasChanged();

        try
        {
            _clienteEncontrado = await Mediator.Send(new BuscarClientePorCifQuery(valor));
        }
        finally
        {
            _buscandoCliente = false;
        }
    }

    private void AbrirCrear()
    {
        _editandoId = null;
        _email = string.Empty;
        _nombreCompleto = string.Empty;
        _password = string.Empty;
        _rol = Roles.Consulta;
        _coordinadorUsuarioId = string.Empty;
        _clienteCif = string.Empty;
        _clienteEncontrado = null;
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        var usuario = await PuertaAccesoDatos.EjecutarAsync(() => UserManager.FindByIdAsync(id.ToString()));
        if (usuario is null)
        {
            ToastService.Mostrar("No encontramos este usuario.", TonoToast.Error);
            await CargarAsync();
            return;
        }

        var roles = await PuertaAccesoDatos.EjecutarAsync(() => UserManager.GetRolesAsync(usuario));

        _editandoId = usuario.Id;
        _email = usuario.Email ?? string.Empty;
        _nombreCompleto = usuario.NombreCompleto;
        _password = string.Empty;
        _rol = roles.FirstOrDefault() ?? Roles.Consulta;
        _coordinadorUsuarioId = usuario.CoordinadorUsuarioId?.ToString() ?? string.Empty;
        _clienteCif = string.Empty;
        _clienteEncontrado = null;

        if (_rol == Roles.GestorCae)
            await CargarCoordinadoresAsync();

        if (_rol == Roles.Cliente && usuario.ClienteId is not null)
        {
            var cliente = await Mediator.Send(new ObtenerClientePorIdQuery(usuario.ClienteId.Value));
            if (cliente is not null)
            {
                _clienteCif = cliente.Cif;
                _clienteEncontrado = new ClientePorCifDto(cliente.Id, cliente.RazonSocial, cliente.Cif);
            }
        }

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
            if (_rol == Roles.Cliente && _clienteEncontrado is null)
            {
                _mensajeErrorFormulario = "Busca y confirma el CIF del cliente a vincular antes de guardar.";
                return;
            }

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

        // ApplicationUser no lo sella el interceptor de tenant (no extiende
        // EntidadConTenant, ver CaeManagerDbContext), así que hay que
        // asignarlo aquí: sin esto el usuario nacía con TenantId vacío pese a
        // que el propio ApplicationUser documenta que "todo usuario nuevo debe
        // crearse con un TenantId explícito", y al iniciar sesión su claim de
        // tenant no correspondía a ninguna organización.
        if (TenantActual.TenantId is not { } tenantId)
        {
            _mensajeErrorFormulario = "No pudimos determinar tu organización. Vuelve a iniciar sesión.";
            return;
        }

        var usuario = new ApplicationUser
        {
            UserName = _email,
            Email = _email,
            NombreCompleto = _nombreCompleto,
            EmailConfirmed = true,
            TenantId = tenantId,
            CoordinadorUsuarioId = _rol == Roles.GestorCae && Guid.TryParse(_coordinadorUsuarioId, out var coordId) ? coordId : null,
            ClienteId = _rol == Roles.Cliente ? _clienteEncontrado?.Id : null,
            // La contraseña que acaba de escribir el Administrador es
            // temporal, pensada para entregarse fuera de banda — se obliga
            // a cambiarla en el primer inicio de sesión real del usuario
            // (ver CambiarContrasena.razor y el guard en MainLayout).
            DebeCambiarContrasena = true
        };

        var resultado = await PuertaAccesoDatos.EjecutarAsync(() => UserManager.CreateAsync(usuario, _password));
        if (!resultado.Succeeded)
        {
            _mensajeErrorFormulario = string.Join(" ", resultado.Errors.Select(e => e.Description));
            return;
        }

        await PuertaAccesoDatos.EjecutarAsync(() => UserManager.AddToRoleAsync(usuario, _rol));

        ToastService.Mostrar("Usuario creado correctamente.", TonoToast.Exito);
        await EnviarCorreoBienvenidaAsync(_email, _nombreCompleto, _password);
        _drawerVisible = false;
        await CargarAsync();
    }

    /// <summary>
    /// Best-effort (Issue #2): un fallo de envío no debe deshacer el alta,
    /// que ya se guardó. La contraseña que se envía es la temporal que
    /// acaba de escribir el Administrador — <see cref="ApplicationUser.DebeCambiarContrasena"/>
    /// ya obliga a cambiarla en el primer inicio de sesión real.
    /// </summary>
    private async Task EnviarCorreoBienvenidaAsync(string email, string nombreCompleto, string contrasenaTemporal)
    {
        var urlAcceso = NavigationManager.BaseUri.TrimEnd('/');

        var cuerpo = $"""
            <p>Hola {System.Net.WebUtility.HtmlEncode(nombreCompleto)},</p>
            <p>Se ha creado tu acceso a {Marca.Nombre}:</p>
            <ul>
                <li>Correo: {System.Net.WebUtility.HtmlEncode(email)}</li>
                <li>Contraseña temporal: {System.Net.WebUtility.HtmlEncode(contrasenaTemporal)}</li>
            </ul>
            <p>Se te pedirá cambiarla en tu primer inicio de sesión.</p>
            <p><a href="{System.Net.WebUtility.HtmlEncode(urlAcceso)}">Entrar a {Marca.Nombre}</a></p>
            """;

        var resultado = await EmailService.EnviarAsync(email, $"Tu acceso a {Marca.Nombre}", cuerpo);
        if (resultado.EsFallido)
            Logger.LogWarning("No se pudo enviar el correo de bienvenida a {Email}.", email);
    }

    private async Task EditarUsuarioAsync(Guid id)
    {
        if (string.IsNullOrWhiteSpace(_nombreCompleto))
        {
            _mensajeErrorFormulario = "El nombre es obligatorio.";
            return;
        }

        var actualizado = await PuertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await UserManager.FindByIdAsync(id.ToString());
            if (usuario is null) return false;

            usuario.NombreCompleto = _nombreCompleto;
            usuario.CoordinadorUsuarioId = _rol == Roles.GestorCae && Guid.TryParse(_coordinadorUsuarioId, out var coordId) ? coordId : null;
            usuario.ClienteId = _rol == Roles.Cliente ? _clienteEncontrado?.Id : null;
            await UserManager.UpdateAsync(usuario);

            var rolesActuales = await UserManager.GetRolesAsync(usuario);
            if (!rolesActuales.Contains(_rol))
            {
                await UserManager.RemoveFromRolesAsync(usuario, rolesActuales);
                await UserManager.AddToRoleAsync(usuario, _rol);
            }

            return true;
        });

        if (!actualizado)
        {
            _mensajeErrorFormulario = "No encontramos este usuario.";
            return;
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

        var encontrado = await PuertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await UserManager.FindByIdAsync(usuarioLista.Id.ToString());
            if (usuario is null) return false;

            usuario.LockoutEnabled = true;
            usuario.LockoutEnd = usuarioLista.Activo ? DateTimeOffset.MaxValue : null;
            await UserManager.UpdateAsync(usuario);
            return true;
        });
        if (!encontrado) return;

        ToastService.Mostrar(
            usuarioLista.Activo ? "Usuario desactivado." : "Usuario reactivado.",
            TonoToast.Exito);

        await CargarAsync();
    }
}
