using System.Security.Claims;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace CaeManager.Web.Tests;

/// <summary>
/// P0 del piloto Outbound (2026-09-28): el Tenant beneficiario elegido volvía al Tenant de origen.
/// Causa: la revalidación del security stamp de la cookie de Identity abre una conexión ANTES de que
/// <c>UseAuthentication</c> fije <c>HttpContext.User</c>; el interceptor de RLS lee entonces la
/// selección, y una lectura sin usuario autenticado se memoizaba como «sin selección» para toda la
/// petición, con lo que la revalidación borraba la cookie en silencio.
/// </summary>
public class ClienteActivoSeleccionadoLecturaPrematuraTests
{
    [Fact]
    public void Una_lectura_antes_de_autenticar_no_se_memoiza_como_sin_seleccion()
    {
        var protector = new EphemeralDataProtectionProvider();
        var usuario = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        var token = ClienteActivoSeleccionado.Proteger(protector, usuario, tenant, null);

        var contexto = new DefaultHttpContext();
        contexto.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={token}";
        var seleccion = new ClienteActivoSeleccionado(new Accesor(contexto), protector);

        // Petición aún anónima (estamos dentro de la validación del stamp).
        seleccion.SesionPrivilegiadaIdSeleccionada.Should().BeNull();
        seleccion.TenantIdSeleccionado.Should().BeNull("sin usuario autenticado no hay a quién ligar el token: fallo cerrado");

        // UseAuthentication termina: la selección debe resolverse, no quedar en nulo por la lectura prematura.
        contexto.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, usuario.ToString())], authenticationType: "Identity.Application"));

        seleccion.TenantIdSeleccionado.Should().Be(tenant);
    }

    [Fact]
    public void Un_token_de_otro_usuario_ya_autenticado_sigue_descartandose()
    {
        var protector = new EphemeralDataProtectionProvider();
        var token = ClienteActivoSeleccionado.Proteger(protector, Guid.NewGuid(), Guid.NewGuid(), null);

        var contexto = new DefaultHttpContext();
        contexto.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={token}";
        contexto.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], authenticationType: "Identity.Application"));

        new ClienteActivoSeleccionado(new Accesor(contexto), protector).TenantIdSeleccionado.Should().BeNull();
    }

    private sealed class Accesor(HttpContext contexto) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = contexto;
    }
}
