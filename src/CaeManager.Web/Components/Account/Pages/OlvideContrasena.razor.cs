using System.ComponentModel.DataAnnotations;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace CaeManager.Web.Components.Account.Pages;

public partial class OlvideContrasena : ComponentBase
{
    /// <summary>Mismo valor que el token por defecto de Identity (DataProtectorTokenProvider) — ver AddDefaultTokenProviders en InfrastructureServiceCollectionExtensions.</summary>
    private const int MinutosCaducidad = 60;

    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private IEmailService EmailService { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private ILoggerFactory LoggerFactory { get; set; } = default!;

    [SupplyParameterFromForm]
    private DatosEntrada? Entrada { get; set; }

    private bool _enviando;
    private bool _enviado;

    protected override void OnInitialized() => Entrada ??= new DatosEntrada();

    /// <summary>
    /// Siempre muestra "revisa tu correo" al terminar, exista o no la cuenta
    /// — decir explícitamente "no encontramos esa cuenta" permitiría
    /// enumerar qué correos están dados de alta (mismo criterio que
    /// Login.razor con el mensaje único de credenciales inválidas/cuenta
    /// bloqueada).
    /// </summary>
    private async Task EnviarAsync()
    {
        if (Entrada is null) return;

        _enviando = true;
        StateHasChanged();

        try
        {
            var usuario = await UserManager.FindByEmailAsync(Entrada.Email);
            if (usuario is not null)
            {
                var token = await UserManager.GeneratePasswordResetTokenAsync(usuario);
                var tokenCodificado = WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(token));
                var enlace = Navigation.ToAbsoluteUri(
                    $"/cuenta/restablecer-contrasena?userId={usuario.Id}&code={tokenCodificado}").ToString();

                var cuerpo = $"""
                    <p>Recibimos una solicitud para restablecer la contraseña de tu cuenta en {Marca.Nombre}.</p>
                    <p><a href="{System.Net.WebUtility.HtmlEncode(enlace)}">Restablecer mi contraseña</a></p>
                    <p>Este enlace caduca en {MinutosCaducidad} minutos. Si no fuiste tú, puedes ignorar este correo — tu contraseña actual sigue siendo válida.</p>
                    """;

                var resultado = await EmailService.EnviarAsync(
                    usuario.Email!, $"Restablece tu contraseña — {Marca.Nombre}", cuerpo);

                if (resultado.EsFallido)
                    LoggerFactory.CreateLogger(AuditoriaAutenticacion.CategoriaLog)
                        .LogWarning("No se pudo enviar el correo de restablecimiento de contraseña a {Email}.", usuario.Email);
            }

            _enviado = true;
        }
        finally
        {
            _enviando = false;
        }
    }

    private sealed class DatosEntrada
    {
        [Required(ErrorMessage = "El correo es obligatorio.")]
        [EmailAddress(ErrorMessage = "Introduce un correo válido.")]
        public string Email { get; set; } = string.Empty;
    }
}
