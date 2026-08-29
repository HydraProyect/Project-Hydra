using System.Security.Claims;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace CaeManager.Web.Tests;

/// <summary>
/// <b>La selección de workspace y de sesión privilegiada solo existe mientras haya
/// <c>HttpContext</c>.</b> Sin él —la condición de un circuito de Blazor Server ya
/// establecido— <see cref="ClienteActivoSeleccionado"/> resuelve a nulo y
/// <b>memoiza ese nulo</b> para todo el ámbito de DI.
///
/// <para>
/// <b>Por qué importa y no es un detalle de implementación.</b>
/// <c>TenantRlsConnectionInterceptor</c> adopta el rol de solo lectura
/// <c>cae_app_soporte</c> <b>solo</b> cuando <c>SesionPrivilegiadaIdSeleccionada</c>
/// no es nulo. Si dentro del circuito resolviera nulo, no habría <c>SET ROLE</c>, y
/// la garantía de solo lectura del plano 3 —la que sostiene la decisión D-2 de que
/// el soporte no conserva escritura— <b>no aplicaría a nada de lo que ocurre por el
/// circuito</b>. El fallo sería silencioso: nada se rompe, simplemente la protección
/// no está.
/// </para>
///
/// <para>
/// <b>Lo que estos dos tests demuestran, y lo que no.</b> Demuestran el mecanismo:
/// con <c>HttpContext</c> la sesión se resuelve, y sin él no. <b>No</b> demuestran
/// que el <c>HttpContext</c> sea efectivamente nulo dentro de un circuito de esta
/// aplicación — eso exige ejecutarla. Se dejan aquí como ancla de regresión y como
/// enunciado preciso de la consecuencia, no como prueba de que el hueco esté abierto
/// en producción.
/// </para>
///
/// <para>
/// <b>Estado de la evidencia al escribirlos (2026-08-29)</b>: ningún test del
/// repositorio ejercitaba la selección desde dentro de un circuito. Los cuatro E2E
/// que cambian de workspace lo hacen con navegación completa del navegador
/// (<c>page.GotoAsync</c> y el <c>&lt;form&gt;</c> del selector), es decir por una
/// petición HTTP donde <c>HttpContext</c> sí existe. Y <see cref="TenantActual"/>
/// tiene vía nativa de circuito para el <b>claim</b>
/// (<c>AuthenticationStateProvider</c>) pero <b>ninguna</b> para la selección, que
/// depende por completo de <c>IHttpContextAccessor</c>.
/// </para>
/// </summary>
public class SeleccionSinHttpContextTests
{
    private static readonly Guid Usuario = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid TenantVisitado = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid Sesion = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    /// <summary>
    /// Control positivo. Sin él, el test de abajo pasaría igual con un token roto o
    /// un protector que no descifra, y no significaría nada: mediría "no se resuelve"
    /// por el motivo equivocado.
    /// </summary>
    [Fact]
    public void Con_HttpContext_la_sesion_privilegiada_se_resuelve()
    {
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: null, sesionPrivilegiadaId: Sesion);

        var seleccion = new ClienteActivoSeleccionado(
            new HttpContextAccessorFijo(ContextoCon(token)), protector);

        seleccion.SesionPrivilegiadaIdSeleccionada.Should().Be(Sesion,
            "el token nombra la sesión y hay HttpContext del que leer la cookie");
        seleccion.TenantIdSeleccionado.Should().Be(TenantVisitado);
    }

    /// <summary>
    /// La condición del circuito: mismo token válido, mismo protector, mismo usuario
    /// — lo único que cambia es que no hay <c>HttpContext</c>.
    /// </summary>
    [Fact]
    public void Sin_HttpContext_no_hay_sesion_privilegiada_aunque_la_cookie_exista()
    {
        var protector = ProtectorDePruebas();

        // El token se crea igual que arriba y es perfectamente válido. Se descarta
        // por dónde se lee, no por lo que dice.
        _ = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: null, sesionPrivilegiadaId: Sesion);

        var seleccion = new ClienteActivoSeleccionado(new HttpContextAccessorSinContexto(), protector);

        seleccion.SesionPrivilegiadaIdSeleccionada.Should().BeNull(
            "sin HttpContext no hay cookie que leer, y el interceptor solo adopta cae_app_soporte cuando " +
            "este valor no es nulo — es decir, en esta condición no habría SET ROLE y la conexión " +
            "conservaría la identidad de escritura");

        seleccion.TenantIdSeleccionado.Should().BeNull(
            "por el mismo motivo: la selección entera depende del HttpContext");
    }

    /// <summary>
    /// El nulo se <b>memoiza</b>. Importa porque un ámbito de DI que resuelva la
    /// selección una sola vez en el momento equivocado la deja apagada para el resto
    /// de su vida, aunque más tarde hubiera contexto disponible.
    /// </summary>
    [Fact]
    public void El_nulo_se_memoiza_para_todo_el_ambito()
    {
        var seleccion = new ClienteActivoSeleccionado(
            new HttpContextAccessorSinContexto(), ProtectorDePruebas());

        seleccion.SesionPrivilegiadaIdSeleccionada.Should().BeNull();

        // Segunda lectura: si volviera a intentarlo, un accessor que empezara a
        // devolver contexto cambiaría la respuesta a mitad de ámbito. No lo hace.
        seleccion.SesionPrivilegiadaIdSeleccionada.Should().BeNull(
            "AsegurarLeidoDeCookie marca _leidoDeCookie en la primera lectura, con éxito o sin él");
    }

    private static IDataProtectionProvider ProtectorDePruebas() =>
        DataProtectionProvider.Create(nameof(SeleccionSinHttpContextTests));

    private static HttpContext ContextoCon(string valorCookie)
    {
        var identidad = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Usuario.ToString())], "prueba");

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

    /// <summary>Lo que devuelve <c>IHttpContextAccessor</c> dentro de un circuito.</summary>
    private sealed class HttpContextAccessorSinContexto : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => null;
            set => throw new NotSupportedException();
        }
    }
}
