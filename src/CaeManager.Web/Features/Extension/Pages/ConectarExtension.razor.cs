using System.Security.Claims;
using System.Text.Json.Serialization;
using CaeManager.Application.Common;
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
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;
    [Inject] private IConfiguration Configuracion { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private IJSObjectReference? _modulo;
    private bool _generando;
    private string? _token;
    private string? _codigoConexion;
    private DateTime? _expiraEnUtc;
    private string? _error;

    private ResultadoEnlace _enlace = ResultadoEnlace.NoIntentado;
    private string? _errorExtension;
    private string? _versionExtension;

    /// <summary>
    /// En qué quedó el último intento de enlace automático.
    /// <para>
    /// Antes era un <c>bool?</c>, y por eso los tres fallos salían con el mismo
    /// mensaje: «instala o actualiza la extensión». Son tres situaciones con
    /// tres soluciones distintas —una de ellas ni siquiera depende de quien lo
    /// lee—, y juntarlas dejaba al gestor sin saber qué hacer. Distinguirlas es
    /// el motivo de este tipo.
    /// </para>
    /// </summary>
    private enum ResultadoEnlace
    {
        /// <summary>Todavía no se ha generado ningún token en esta visita.</summary>
        NoIntentado,

        Conectado,

        /// <summary>
        /// Este entorno no tiene <c>Extension:IdChromeStore</c>, así que la
        /// página no llegó a intentarlo. No es un problema del navegador de
        /// quien lo lee, y no se arregla reinstalando nada.
        /// </summary>
        PuenteNoConfigurado,

        /// <summary>
        /// La extensión no respondió. Chrome no permite distinguir «no está
        /// instalada» de «está pero es anterior al enlace automático»: en
        /// ambos casos no hay receptor.
        /// </summary>
        NoDetectada,

        /// <summary>Respondió, y dijo que no. Entonces sí hay un motivo concreto que contar.</summary>
        Rechazado,
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
            _modulo = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/conexionExtension.js");
    }

    private async Task GenerarAsync()
    {
        _generando = true;
        _error = null;
        _enlace = ResultadoEnlace.NoIntentado;
        _errorExtension = null;
        _versionExtension = null;
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

            // Por la puerta: UserManager no pasa por MediatR, así que nada
            // serializa su acceso al CaeManagerDbContext scoped del circuito
            // (ver PuertaAccesoDatos). Esta página es interactiva y el
            // DbContext lo comparte con el layout: pulsar "Generar token"
            // mientras MainLayout todavía resuelve su propia carga —o
            // mientras ActividadUsuarioService escribe— es exactamente la
            // carrera de "A second operation was started on this context
            // instance" que la puerta existe para evitar. Antes era deuda
            // registrada en IdentityEnComponentesPorLaPuertaDeAccesoADatosTests.
            var resultado = await PuertaAccesoDatos.EjecutarAsync(() =>
                EmisorTokenExtension.EmitirAsync(usuarioId, UserManager, DataProtectionProvider));
            if (resultado.EsFallido)
            {
                _error = resultado.Error.Mensaje;
                return;
            }

            (_token, _expiraEnUtc) = resultado.Valor;
            _codigoConexion = CodigoConexionExtension.Crear(NavigationManager.BaseUri, _token, _expiraEnUtc.Value);
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
            // Sin el identificador de la extensión no hay a quién mandarle el
            // mensaje, así que ni se intenta. Decir aquí «instala la extensión»
            // sería mentir: el que no está configurado es el servidor.
            _enlace = ResultadoEnlace.PuenteNoConfigurado;
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
        _enlace = respuesta switch
        {
            { Disponible: true, Ok: true } => ResultadoEnlace.Conectado,
            { Disponible: true } => ResultadoEnlace.Rechazado,
            _ => ResultadoEnlace.NoDetectada,
        };

        if (_enlace == ResultadoEnlace.Rechazado)
            _errorExtension = respuesta.Error ?? "La extensión no explicó por qué.";
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
