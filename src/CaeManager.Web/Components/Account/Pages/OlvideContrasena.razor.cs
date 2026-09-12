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

    private bool _enviado;
    private bool _error;

    private static string ExplicacionFormulario =>
        $"Escribe la dirección con la que entras a {Marca.Nombre} y te enviaremos un enlace para restablecerla.";

    protected override void OnInitialized() => Entrada ??= new DatosEntrada();

    /// <summary>
    /// Si la cuenta no existe, o existe y el envío tuvo éxito, muestra siempre
    /// "revisa tu correo" — decir explícitamente "no encontramos esa cuenta"
    /// permitiría enumerar qué correos están dados de alta (mismo criterio que
    /// Login.razor con el mensaje único de credenciales inválidas/cuenta
    /// bloqueada). Un fallo de envío es distinto: es un fallo de
    /// infraestructura (Graph mal configurado, sin red, sin token) que no
    /// depende de si la cuenta existe, así que anunciarlo con
    /// <see cref="_error"/> en vez de <see cref="_enviado"/> no delata nada —
    /// solo se llega aquí para una cuenta real que sí intentó recibir el
    /// correo.
    /// </summary>
    private async Task EnviarAsync()
    {
        if (Entrada is null) return;

        _error = false;

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
            {
                LoggerFactory.CreateLogger(AuditoriaAutenticacion.CategoriaLog)
                    .LogWarning("No se pudo enviar el correo de restablecimiento de contraseña a {UsuarioId}.", usuario.Id);
                _error = true;
                return;
            }
        }

        _enviado = true;
    }

    private sealed class DatosEntrada
    {
        [Required(ErrorMessage = "El correo es obligatorio.")]
        [EmailAddress(ErrorMessage = "Introduce un correo válido.")]
        public string Email { get; set; } = string.Empty;
    }
}
