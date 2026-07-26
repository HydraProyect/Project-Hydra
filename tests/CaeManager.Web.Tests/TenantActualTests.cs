using System.Security.Claims;
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
        var tenantActual = new TenantActual(authStateProvider, httpContextAccessor);

        tenantActual.TenantId.Should().Be(TenantIdDeEjemplo);
    }

    [Fact]
    public void Cae_a_HttpContext_cuando_no_hay_circuito_de_blazor_pero_si_usuario_autenticado_por_cookie()
    {
        var authStateProvider = new AuthenticationStateProviderFalso(lanzarInvalidOperationException: true);
        var httpContextAccessor = new HttpContextAccessorFalso(UsuarioAutenticadoCon(TenantIdDeEjemplo));
        var tenantActual = new TenantActual(authStateProvider, httpContextAccessor);

        tenantActual.TenantId.Should().Be(TenantIdDeEjemplo);
    }

    [Fact]
    public void Devuelve_null_si_no_hay_circuito_ni_HttpContext_autenticado()
    {
        var authStateProvider = new AuthenticationStateProviderFalso(lanzarInvalidOperationException: true);
        var httpContextAccessor = new HttpContextAccessorFalso(null);
        var tenantActual = new TenantActual(authStateProvider, httpContextAccessor);

        tenantActual.TenantId.Should().BeNull();
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

    private sealed class HttpContextAccessorFalso(ClaimsPrincipal? usuario) : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => usuario is null ? null : new DefaultHttpContext { User = usuario };
            set => throw new NotSupportedException();
        }
    }
}
