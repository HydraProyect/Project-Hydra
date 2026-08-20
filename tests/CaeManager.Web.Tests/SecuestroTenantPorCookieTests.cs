using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace CaeManager.Web.Tests;

/// <summary>
/// Regresión del hallazgo C-1 de INFORME-AUDITORIA-TECNICA.md: la cookie
/// <c>cae_cliente_activo</c> ganaba al claim firmado <c>tenant_id</c> dentro de
/// <see cref="TenantActual"/>, que es el único valor que alimenta el filtro
/// global de EF Core, el <c>TenantSelladoInterceptor</c> y el particionado de
/// almacenamiento. Si esa cookie se puede fabricar, la frontera de aislamiento
/// que docs/MULTITENANCY.md define como absoluta se rompe entera.
///
/// El ataque no necesitaba filtrar información previa: el tenant #1 tiene un Id
/// determinista y público en el código (<see cref="TenantSeedData.IdPorDefecto"/>,
/// <c>…-000000000001</c>). Bastaba con que un usuario autenticado de otro tenant
/// reenviara su propia sesión legítima añadiendo la cookie a mano (devtools o
/// <c>curl</c>; <c>HttpOnly</c> no lo impide, solo impide que la lea JavaScript
/// de terceros).
///
/// Estos tests recorren la ruta real de producción — el
/// <see cref="ClienteActivoSeleccionado"/> de verdad leyendo de un
/// <c>HttpContext</c> de verdad —, no el doble que devuelve siempre null que usa
/// <see cref="TenantActualTests"/> y que dejaba esta ruta sin cubrir.
/// </summary>
public class SecuestroTenantPorCookieTests
{
    /// <summary>El tenant objetivo del ataque: Id fijo y adivinable.</summary>
    private static readonly Guid TenantVictima = TenantSeedData.IdPorDefecto;

    /// <summary>El tenant al que sí pertenece el atacante, según su claim firmado.</summary>
    private static readonly Guid TenantAtacante = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Guid UsuarioAtacante = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UsuarioLegitimo = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void Una_cookie_fabricada_a_mano_no_secuestra_el_tenant_victima()
    {
        // Sesión legítima del atacante (claim firmado = su propio tenant) +
        // cookie escrita a mano apuntando al tenant #1, tal cual la escribiría
        // curl. No pasa por el endpoint, así que nunca se validó delegación
        // alguna.
        var tenantActual = CrearTenantActual(ContextoCon(
            usuarioId: UsuarioAtacante,
            tenantDelClaim: TenantAtacante,
            valorCookie: TenantVictima.ToString()));

        tenantActual.TenantId.Should().Be(
            TenantAtacante,
            "un GUID en claro no es una credencial: debe descartarse y mandar el claim firmado");
        tenantActual.TenantId.Should().NotBe(
            TenantVictima,
            "resolver aquí el tenant víctima es la toma de control completa descrita en C-1");
    }

    [Fact]
    public void Una_cookie_manipulada_tras_haber_sido_emitida_se_descarta()
    {
        // Variante más realista: el atacante sí es operador delegado sobre
        // algún tenant, recibe un token legítimo y le cambia el final para
        // apuntar a otro. La manipulación debe invalidar el valor entero, no
        // degradar a "confío en lo que ponga".
        var protector = ProtectorDePruebas();
        var tokenLegitimo = ClienteActivoSeleccionado.Proteger(protector, UsuarioAtacante, TenantAtacante, null);
        var tokenManipulado = tokenLegitimo[..^4] + "AAAA";

        var tenantActual = CrearTenantActual(
            ContextoCon(UsuarioAtacante, TenantAtacante, tokenManipulado), protector);

        tenantActual.TenantId.Should().Be(TenantAtacante);
    }

    [Fact]
    public void Un_token_legitimo_de_otro_usuario_no_es_reutilizable()
    {
        // El token va ligado al usuario para el que se emitió: copiarlo a la
        // sesión de otro no debe transferirle el acceso al workspace.
        var protector = ProtectorDePruebas();
        var tokenDeOtro = ClienteActivoSeleccionado.Proteger(protector, UsuarioLegitimo, TenantVictima, null);

        var tenantActual = CrearTenantActual(
            ContextoCon(UsuarioAtacante, TenantAtacante, tokenDeOtro), protector);

        tenantActual.TenantId.Should().Be(TenantAtacante);
        tenantActual.TenantId.Should().NotBe(TenantVictima);
    }

    [Fact]
    public void Un_token_emitido_con_otro_llavero_no_vale_en_este()
    {
        // Un atacante que fabricara su propio token con Data Protection no
        // consigue nada: sin las claves del sistema, no descifra aquí.
        var tokenAjeno = ClienteActivoSeleccionado.Proteger(
            DataProtectionProvider.Create("llavero-del-atacante"), UsuarioAtacante, TenantVictima, null);

        var tenantActual = CrearTenantActual(
            ContextoCon(UsuarioAtacante, TenantAtacante, tokenAjeno), ProtectorDePruebas());

        tenantActual.TenantId.Should().Be(TenantAtacante);
    }

    [Fact]
    public void Sin_sesion_autenticada_la_cookie_no_abre_ningun_tenant()
    {
        // Fallo cerrado: la selección puede cambiar el tenant de un usuario ya
        // autenticado, nunca crear un contexto donde no había ninguno.
        var protector = ProtectorDePruebas();
        var tokenValido = ClienteActivoSeleccionado.Proteger(protector, UsuarioAtacante, TenantVictima, null);

        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        httpContext.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={tokenValido}";

        CrearTenantActual(httpContext, protector).TenantId.Should().BeNull();
    }

    [Fact]
    public void Un_token_emitido_por_el_propio_sistema_si_selecciona_el_workspace()
    {
        // Contrapeso imprescindible: el fix no puede romper ADR-004. Un token
        // emitido por el endpoint (que sí validó la delegación contra base de
        // datos) debe seguir seleccionando el Delegated Workspace.
        var protector = ProtectorDePruebas();
        var tokenValido = ClienteActivoSeleccionado.Proteger(protector, UsuarioAtacante, TenantVictima, null);

        var tenantActual = CrearTenantActual(
            ContextoCon(UsuarioAtacante, TenantAtacante, tokenValido), protector);

        tenantActual.TenantId.Should().Be(TenantVictima);
    }

    private static TenantActual CrearTenantActual(HttpContext httpContext, IDataProtectionProvider? protector = null)
    {
        var accessor = new HttpContextAccessorFijo(httpContext);
        var clienteActivo = new ClienteActivoSeleccionado(accessor, protector ?? ProtectorDePruebas());

        // Sin circuito de Blazor (el caso de la petición HTTP directa del
        // ataque), así que TenantActual cae al usuario del HttpContext.
        return new TenantActual(new SinCircuitoDeBlazor(), accessor, clienteActivo);
    }

    private static IDataProtectionProvider ProtectorDePruebas() =>
        DataProtectionProvider.Create(nameof(SecuestroTenantPorCookieTests));

    private static HttpContext ContextoCon(Guid usuarioId, Guid tenantDelClaim, string valorCookie)
    {
        var identidad = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString()),
            new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, tenantDelClaim.ToString()),
        ], "prueba");

        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identidad) };
        httpContext.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={valorCookie}";
        return httpContext;
    }

    private sealed class HttpContextAccessorFijo(HttpContext httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => httpContext;
            set => throw new NotSupportedException();
        }
    }

    private sealed class SinCircuitoDeBlazor : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            throw new InvalidOperationException("Sin circuito de Blazor (simulado en el test).");
    }
}
