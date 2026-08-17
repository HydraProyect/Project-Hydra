using System.ComponentModel.DataAnnotations;
using System.Text;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Components.Account.Pages;

public partial class RestablecerContrasena : ComponentBase
{
    private sealed record Requisito(string Etiqueta, bool Cumplido);

    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private SignInManager<ApplicationUser> SignInManager { get; set; } = default!;
    [Inject] private IOptions<IdentityOptions> OpcionesIdentity { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private ILoggerFactory LoggerFactory { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "userId")]
    private string? UserId { get; set; }

    [SupplyParameterFromQuery(Name = "code")]
    private string? Code { get; set; }

    [SupplyParameterFromForm]
    private DatosEntrada? Entrada { get; set; }

    private ApplicationUser? _usuario;
    private string? _token;
    private string? _correoCuenta;
    private bool _enlaceInvalido;
    private bool _completado;
    private bool _guardando;
    private string? _mensajeError;
    private IReadOnlyList<Requisito> _requisitos = [];

    private bool _mostrarDesajuste => Entrada is not null && Entrada.ConfirmarContrasenaNueva.Length > 0
        && Entrada.ContrasenaNueva != Entrada.ConfirmarContrasenaNueva;

    private bool PuedeGuardar => Entrada is not null && _requisitos.Count > 0 && _requisitos.All(r => r.Cumplido)
        && Entrada.ConfirmarContrasenaNueva.Length > 0 && Entrada.ContrasenaNueva == Entrada.ConfirmarContrasenaNueva;

    protected override void OnInitialized() => Entrada ??= new DatosEntrada();

    protected override async Task OnInitializedAsync()
    {
        ActualizarRequisitos();

        if (string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(Code))
        {
            _enlaceInvalido = true;
            return;
        }

        _usuario = await UserManager.FindByIdAsync(UserId);
        if (_usuario is null)
        {
            _enlaceInvalido = true;
            return;
        }

        try
        {
            _token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(Code));
        }
        catch (FormatException)
        {
            _enlaceInvalido = true;
            return;
        }

        _correoCuenta = _usuario.Email;
    }

    private void ActualizarRequisitos()
    {
        var politica = OpcionesIdentity.Value.Password;
        var p = Entrada?.ContrasenaNueva ?? string.Empty;

        var requisitos = new List<Requisito> { new($"Al menos {politica.RequiredLength} caracteres", p.Length >= politica.RequiredLength) };

        if (politica.RequireUppercase)
            requisitos.Add(new("Una mayúscula", p.Any(char.IsUpper)));
        if (politica.RequireLowercase)
            requisitos.Add(new("Una minúscula", p.Any(char.IsLower)));
        if (politica.RequireDigit)
            requisitos.Add(new("Un número", p.Any(char.IsDigit)));
        if (politica.RequireNonAlphanumeric)
            requisitos.Add(new("Un símbolo", p.Any(c => !char.IsLetterOrDigit(c))));

        _requisitos = requisitos;
    }

    private async Task GuardarAsync()
    {
        if (_usuario is null || _token is null || Entrada is null) return;

        _guardando = true;
        _mensajeError = null;
        StateHasChanged();

        try
        {
            var resultado = await UserManager.ResetPasswordAsync(_usuario, _token, Entrada.ContrasenaNueva);
            var logger = LoggerFactory.CreateLogger(AuditoriaAutenticacion.CategoriaLog);

            if (!resultado.Succeeded)
            {
                // Un token caducado o ya usado cae aquí igual que un fallo de
                // política de contraseña — no se distingue el motivo exacto
                // en el mensaje (mismo criterio de no-enumeración que
                // Login.razor), pero si el problema es el token se ofrece
                // directamente pedir uno nuevo.
                if (resultado.Errors.Any(e => e.Code is "InvalidToken"))
                {
                    logger.LogWarning("Restablecimiento de contraseña rechazado (token inválido o caducado): {UsuarioId}", _usuario.Id);
                    _enlaceInvalido = true;
                    return;
                }

                _mensajeError = string.Join(" ", resultado.Errors.Select(e => e.Description));
                return;
            }

            _usuario.DebeCambiarContrasena = false;
            await UserManager.UpdateAsync(_usuario);

            logger.LogInformation("Restablecimiento de contraseña correcto: {UsuarioId}", _usuario.Id);

            await SignInManager.SignOutAsync();
            _completado = true;
        }
        finally
        {
            _guardando = false;
        }
    }

    private sealed class DatosEntrada
    {
        [Required(ErrorMessage = "Introduce la contraseña nueva.")]
        public string ContrasenaNueva { get; set; } = string.Empty;

        [Required(ErrorMessage = "Confirma la contraseña nueva.")]
        public string ConfirmarContrasenaNueva { get; set; } = string.Empty;
    }
}
