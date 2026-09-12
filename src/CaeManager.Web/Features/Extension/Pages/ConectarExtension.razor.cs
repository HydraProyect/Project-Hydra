using System.Security.Claims;
using System.Text.Json.Serialization;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.Extension.Pages;

/// <summary>
/// Incremento 4 del MVP1 de integración con plataformas CAE externas vía
/// extensión de navegador: cierra el hueco que #484 dejó explícito ("sin UI
/// todavía, la página de Conectar extensión no puede cerrarse hasta que
/// exista la extensión que reciba el token"). Genera el mismo token que
/// consume <c>ExtensionTokenEndpoints</c>, vía <see cref="EmisorTokenExtension"/>
/// -- llamado aquí directamente, en el circuito interactivo, en vez de por
/// HTTP con antiforgery de formulario: mismo criterio que ya usa
/// <c>ClavesApi.razor</c> para generar sus secretos.
///
/// <para>
/// Enlace automático con la extensión (DEC A', 2026-09-10, ver
/// ARQUITECTURA-INTEGRACIONES.md § 14 en el repositorio de negocio): en
/// cuanto se genera el token, esta página se lo manda directamente a la
/// extensión vía <c>chrome.runtime.sendMessage</c>
/// (<c>wwwroot/js/conexionExtension.js</c>) — Chrome solo expone esa API
/// aquí porque <c>externally_connectable.matches</c> del manifiesto de la
/// extensión declara este origen. Sin copiar/pegar, y sin el permiso nativo
/// que antes se pedía en caliente (ahora <c>host_permissions</c> estático,
/// posible porque TALVEG sirve todos los tenants desde un dominio fijo por
/// entorno — el tenant se resuelve por claim de sesión, no por Host, ver
/// <see cref="Services.TenantActual"/>).
/// </para>
/// </summary>
public partial class ConectarExtension : ComponentBase, IAsyncDisposable
{
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private IDataProtectionProvider DataProtectionProvider { get; set; } = default!;
    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;
    [Inject] private IConfiguration Configuracion { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private IJSObjectReference? _modulo;
    private bool _generando;
    private string? _token;
    private DateTime? _expiraEnUtc;
    private string? _error;

    // null: no se ha intentado enlazar todavía (antes del primer "Generar
    // token"). true/false: resultado del último intento de enlace automático.
    private bool? _extensionConectada;
    private string? _errorExtension;
    private string? _versionExtension;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
            _modulo = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/conexionExtension.js");
    }

    private async Task GenerarAsync()
    {
        _generando = true;
        _error = null;
        _extensionConectada = null;
        _errorExtension = null;
        StateHasChanged();

        try
        {
            var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var valorClaim = estadoAutenticacion.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(valorClaim, out var usuarioId))
            {
                _error = "No pudimos identificar tu sesión. Vuelve a iniciar sesión.";
                return;
            }

            var resultado = await EmisorTokenExtension.EmitirAsync(usuarioId, UserManager, DataProtectionProvider);
            if (resultado.EsFallido)
            {
                _error = resultado.Error.Mensaje;
                return;
            }

            (_token, _expiraEnUtc) = resultado.Valor;
            await ConectarExtensionAsync();
        }
        finally
        {
            _generando = false;
        }
    }

    private async Task ConectarExtensionAsync()
    {
        var idExtension = Configuracion["Extension:IdChromeStore"];
        if (string.IsNullOrWhiteSpace(idExtension) || _modulo is null || _token is null || _expiraEnUtc is null)
        {
            _extensionConectada = false;
            _errorExtension = "El enlace automático no está configurado en este entorno.";
            return;
        }

        var mensaje = new
        {
            tipo = "hydra.conectarExtension.v1",
            hydraUrl = NavigationManager.BaseUri,
            token = _token,
            expiraEnUtc = _expiraEnUtc.Value.ToString("O"),
        };

        var respuesta = await _modulo.InvokeAsync<RespuestaConexionExtension>("conectar", idExtension, mensaje);

        _versionExtension = respuesta.VersionExtension;
        _extensionConectada = respuesta is { Disponible: true, Ok: true };
        if (_extensionConectada is not true)
        {
            _errorExtension = respuesta.Disponible
                ? respuesta.Error ?? "La extensión no pudo completar el enlace."
                : "No detectamos la extensión instalada, o tiene una versión antigua sin enlace automático.";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_modulo is not null)
        {
            try
            {
                await _modulo.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // El circuito ya se cerró — no hay a quién avisar.
            }
        }
    }

    private sealed record RespuestaConexionExtension(
        [property: JsonPropertyName("disponible")] bool Disponible,
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("versionExtension")] string? VersionExtension);
}
