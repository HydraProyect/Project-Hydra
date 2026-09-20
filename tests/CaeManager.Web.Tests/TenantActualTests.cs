using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cubre el fallo real encontrado al previsualizar un documento en un
/// iframe (Fase 44): fuera de un circuito de Blazor (p. ej. el minimal API
/// GET /documentos/{id}/archivo que sirve el iframe) no hay
/// AuthenticationState, así que TenantId debía caer a IHttpContextAccessor
/// para no resolver "sin tenant" con un usuario realmente autenticado por
/// cookie.
/// </summary>
public class TenantActualTests
{
    private static readonly Guid TenantIdDeEjemplo = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Resuelve_el_tenant_desde_el_circuito_de_blazor_cuando_hay_uno()
    {
        var authStateProvider = new AuthenticationStateProviderFalso(UsuarioAutenticadoCon(TenantIdDeEjemplo));
        var httpContextAccessor = new HttpContextAccessorFalso(null);
        var tenantActual = new TenantActual(authStateProvider, httpContextAccessor, new ClienteActivoSeleccionadoFalso());

        tenantActual.TenantId.Should().Be(TenantIdDeEjemplo);
    }

    [Fact]
    public void Cae_a_HttpContext_cuando_no_hay_circuito_de_blazor_pero_si_usuario_autenticado_por_cookie()
    {
        var authStateProvider = new AuthenticationStateProviderFalso(lanzarInvalidOperationException: true);
        var httpContextAccessor = new HttpContextAccessorFalso(UsuarioAutenticadoCon(TenantIdDeEjemplo));
        var tenantActual = new TenantActual(authStateProvider, httpContextAccessor, new ClienteActivoSeleccionadoFalso());

        tenantActual.TenantId.Should().Be(TenantIdDeEjemplo);
    }

    [Fact]
    public void Devuelve_null_si_no_hay_circuito_ni_HttpContext_autenticado()
    {
        var authStateProvider = new AuthenticationStateProviderFalso(lanzarInvalidOperationException: true);
        var httpContextAccessor = new HttpContextAccessorFalso(null);
        var tenantActual = new TenantActual(authStateProvider, httpContextAccessor, new ClienteActivoSeleccionadoFalso());

        tenantActual.TenantId.Should().BeNull();
    }

    [Fact]
    public void Un_circuito_con_la_sesion_invalidada_no_recupera_identidad_por_el_HttpContext_que_lo_abrio()
    {
        // Medido en E2E: dentro de un circuito el HttpContext es el de la
        // petición que lo abrió (presente, autenticado, usuario de entonces).
        var httpContextAccessor = new HttpContextAccessorFalso(UsuarioAutenticadoCon(TenantIdDeEjemplo));

        var vigente = new TenantActual(
            new AuthenticationStateProviderFalso(lanzarInvalidOperationException: true), httpContextAccessor, new ClienteActivoSeleccionadoFalso());
        var vigenteConInterfaz = new TenantActual(
            new ProveedorDeCircuitoInvalidadoFalso(sesionInvalidada: false), httpContextAccessor, new ClienteActivoSeleccionadoFalso());
        var invalidado = new TenantActual(
            new ProveedorDeCircuitoInvalidadoFalso(), httpContextAccessor, new ClienteActivoSeleccionadoFalso());

        vigente.TenantId.Should().Be(TenantIdDeEjemplo, "control positivo: sin invalidar, el fallback a HttpContext sigue funcionando");
        vigenteConInterfaz.TenantId.Should().Be(TenantIdDeEjemplo, "el proveedor implementa la interfaz pero su sesión sigue vigente");
        invalidado.TenantId.Should().BeNull("un circuito invalidado ya no tiene sesión, aunque el HttpContext heredado la conserve");
    }

    /// <summary>
    /// Invariante de la que depende <c>RevalidacionCircuitoActivoHandler</c>
    /// (Módulo 9, auditoría 2026-08-30): su temporizador de fondo solo cierra
    /// el hueco de lectura de un circuito ya abierto porque esta propiedad
    /// relee <see cref="IClienteActivoSeleccionado.TenantIdSeleccionado"/> en
    /// vivo en cada acceso — el claim de sesión sí se cachea en <c>_resuelto</c>,
    /// la selección nunca. Si algún día se "optimizara" cacheando también la
    /// selección, este test es el que lo detectaría: sin él, el handler
    /// seguiría invalidando en memoria sin que ningún lector lo notara nunca
    /// — un fallo silencioso indistinguible de un éxito.
    /// </summary>
    [Fact]
    public void La_seleccion_de_workspace_se_relee_en_vivo_aunque_el_claim_de_tenant_ya_este_cacheado()
    {
        var authStateProvider = new AuthenticationStateProviderFalso(UsuarioAutenticadoCon(TenantIdDeEjemplo));
        var seleccion = new ClienteActivoSeleccionadoFalso { TenantIdSeleccionado = Guid.NewGuid() };
        var tenantActual = new TenantActual(authStateProvider, new HttpContextAccessorFalso(null), seleccion);

        // Primera lectura: fuerza a TenantActual a resolver y cachear el claim
        // base (_resuelto = true), y a la vez a devolver la selección viva.
        tenantActual.TenantId.Should().Be(seleccion.TenantIdSeleccionado);

        // Invalidación en memoria, sin volver a tocar el claim de sesión — es
        // exactamente lo único que hace RevalidacionCircuitoActivoHandler.
        seleccion.TenantIdSeleccionado = null;

        tenantActual.TenantId.Should().Be(TenantIdDeEjemplo, "la selección invalidada debe dejar paso al tenant propio, no quedarse con el valor viejo cacheado");
    }

    private static ClaimsPrincipal UsuarioAutenticadoCon(Guid tenantId)
    {
        var identidad = new ClaimsIdentity(
            [new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, tenantId.ToString())], "prueba");
        return new ClaimsPrincipal(identidad);
    }

    private sealed class AuthenticationStateProviderFalso : AuthenticationStateProvider
    {
        private readonly ClaimsPrincipal? _usuario;
        private readonly bool _lanzarInvalidOperationException;

        public AuthenticationStateProviderFalso(ClaimsPrincipal? usuario = null, bool lanzarInvalidOperationException = false)
        {
            _usuario = usuario;
            _lanzarInvalidOperationException = lanzarInvalidOperationException;
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            if (_lanzarInvalidOperationException)
                throw new InvalidOperationException("Sin circuito de Blazor (simulado en el test).");

            return Task.FromResult(new AuthenticationState(_usuario ?? new ClaimsPrincipal(new ClaimsIdentity())));
        }
    }

    private sealed class ProveedorDeCircuitoInvalidadoFalso(bool sesionInvalidada = true)
        : AuthenticationStateProvider, ISesionDeCircuitoInvalidable
    {
        public bool SesionInvalidada => sesionInvalidada;

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private sealed class HttpContextAccessorFalso(ClaimsPrincipal? usuario) : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => usuario is null ? null : new DefaultHttpContext { User = usuario };
            set => throw new NotSupportedException();
        }
    }

    private sealed class ClienteActivoSeleccionadoFalso : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado { get; set; }
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
