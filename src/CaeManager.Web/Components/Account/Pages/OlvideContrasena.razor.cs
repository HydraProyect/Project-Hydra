using System.ComponentModel.DataAnnotations;
using CaeManager.Web.Services;
using CaeManager.Application.Common;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Components.Account.Pages;

public partial class OlvideContrasena : ComponentBase
{
    /// <summary>Mismo valor que el token por defecto de Identity (DataProtectorTokenProvider) — ver AddDefaultTokenProviders en InfrastructureServiceCollectionExtensions.</summary>
    private const int MinutosCaducidad = 60;

    [Inject] private ILookupNormalizer Normalizador { get; set; } = default!;
    [Inject] private RecuperacionContrasena Recuperacion { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private EnlacesPublicos Enlaces { get; set; } = default!;

    [SupplyParameterFromForm]
    private DatosEntrada? Entrada { get; set; }

    private bool _enviado;

    private static string ExplicacionFormulario =>
        $"Escribe la dirección con la que entras a {Marca.Nombre} y te enviaremos un enlace para restablecerla.";

    protected override void OnInitialized() => Entrada ??= new DatosEntrada();

    // La respuesta no espera ni la búsqueda de cuenta ni el envío. Ausencia,
    // límite por cuenta y fallos de correo muestran la misma confirmación.
    private Task EnviarAsync()
    {
        if (Entrada is null) return Task.CompletedTask;
        var correo = Entrada.Email.Trim();
        // Un correo más largo que el máximo de una dirección no puede existir: se
        // descarta antes de normalizarlo, con la misma confirmación visible.
        if (correo.Length <= RecuperacionContrasena.LongitudMaximaCorreo)
        {
            var basePublica = Enlaces.ObtenerBase(Navigation.BaseUri);
            if (basePublica is not null)
                Recuperacion.Solicitar(Normalizador.NormalizeEmail(correo)!, basePublica);
        }

        _enviado = true;
        return Task.CompletedTask;
    }

    private sealed class DatosEntrada
    {
        [Required(ErrorMessage = "El correo es obligatorio.")]
        [StringLength(RecuperacionContrasena.LongitudMaximaCorreo, ErrorMessage = "Introduce un correo válido.")]
        [EmailAddress(ErrorMessage = "Introduce un correo válido.")]
        public string Email { get; set; } = string.Empty;
    }
}
