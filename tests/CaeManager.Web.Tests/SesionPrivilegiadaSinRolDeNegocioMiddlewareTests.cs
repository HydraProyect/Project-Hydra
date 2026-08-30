using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace CaeManager.Web.Tests;

/// <summary>
/// Las 30 puertas <c>[Authorize(Roles = …)]</c> del portal preguntan al
/// <c>ClaimsPrincipal</c>, no a <c>CurrentUserService</c>. Mientras el técnico
/// de plataforma conserve ahí el rol de <b>su</b> tenant, esas puertas le
/// contestan que sí dentro del tenant del cliente que visita — con una
/// autoridad que nadie le concedió sobre ese tenant.
///
/// Estos tests fijan la regla: bajo sesión privilegiada, el principal se queda
/// sin ningún claim de rol, y sin él las 30 puertas fallan cerradas. Lo que la
/// sesión sí concede (lectura del tenant objetivo) se concede por capacidad en
/// <c>AlcanceDatosService</c>, nunca por rol.
/// </summary>
public class SesionPrivilegiadaSinRolDeNegocioMiddlewareTests
{
    private static readonly Guid Usuario = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantVisitado = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Con_sesion_privilegiada_el_principal_se_queda_sin_rol()
    {
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: null, sesionPrivilegiadaId: Guid.NewGuid());

        var contexto = ContextoCon(token, Roles.Administrador);

        await EjecutarAsync(contexto, protector);

        contexto.User.IsInRole(Roles.Administrador).Should().BeFalse(
            "un técnico de soporte no es Administrador del tenant que visita, por mucho que lo sea del suyo");
        contexto.User.FindAll(ClaimTypes.Role).Should().BeEmpty();

        // La identidad del usuario NO se toca: la auditoría necesita saber
        // quién entró, y quitarle el rol no es quitarle el nombre.
        contexto.User.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be(Usuario.ToString());
        contexto.User.Identity!.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task Sin_cookie_de_seleccion_el_rol_se_conserva_intacto()
    {
        // El caso del 100 % de los usuarios de hoy: cero cambio de
        // comportamiento y ni un descifrado.
        var contexto = ContextoCon(valorCookie: null, Roles.Administrador);

        await EjecutarAsync(contexto, ProtectorDePruebas());

        contexto.User.IsInRole(Roles.Administrador).Should().BeTrue();
    }

    [Fact]
    public async Task Con_un_workspace_delegado_normal_el_rol_se_conserva()
    {
        // Plano 2, no plano 3: ESTE middleware no lo toca, porque quitarle el
        // rol entero a un operador delegado —que sí es miembro del workspace—
        // habría roto la delegación.
        //
        // Lo que decía este comentario antes era que el claim "sigue haciendo
        // su trabajo en las puertas de página". Era falso, y era el agujero: en
        // plano 2 el claim es el del tenant de ORIGEN, así que un Administrador
        // en A delegado como Consulta en B superaba las puertas de
        // Administrador de B. Quien pone el rol correcto aquí es
        // RolEfectivoDelWorkspaceMiddleware, registrado justo después de este;
        // lo que este test fija es únicamente que la responsabilidad no es de
        // este middleware, no que el claim de origen sea legítimo.
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: Guid.NewGuid());

        var contexto = ContextoCon(token, Roles.GestorCae);

        await EjecutarAsync(contexto, protector);

        contexto.User.IsInRole(Roles.GestorCae).Should().BeTrue();
    }

    [Fact]
    public async Task Un_token_manipulado_que_dijera_traer_sesion_no_cambia_nada()
    {
        // No descifra, así que no dice nada — ni para conceder ni para quitar.
        // Se comprueba para dejar claro que el middleware no reacciona a la
        // mera presencia de la cookie.
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: null, sesionPrivilegiadaId: Guid.NewGuid());

        var contexto = ContextoCon(token[..^4] + "AAAA", Roles.Administrador);

        await EjecutarAsync(contexto, protector);

        contexto.User.IsInRole(Roles.Administrador).Should().BeTrue(
            "un token roto no abre ninguna sesión privilegiada, así que tampoco hay motivo para retirar el rol");
    }

    [Fact]
    public async Task Un_token_de_otro_usuario_no_retira_el_rol_de_quien_lo_reenvia()
    {
        // Coherencia con el resto del sistema: el token está ligado a su
        // usuario, así que en la sesión de otro no significa nada. Y como no
        // significa nada, tampoco abre contexto alguno para él.
        var protector = ProtectorDePruebas();
        var tokenDeOtro = ClienteActivoSeleccionado.Proteger(
            protector, Guid.NewGuid(), TenantVisitado, asignacionOperacionId: null,
            sesionPrivilegiadaId: Guid.NewGuid());

        var contexto = ContextoCon(tokenDeOtro, Roles.Administrador);

        await EjecutarAsync(contexto, protector);

        contexto.User.IsInRole(Roles.Administrador).Should().BeTrue();

        var seleccion = new ClienteActivoSeleccionado(new HttpContextAccessorFijoLocal(contexto), protector);
        seleccion.TenantIdSeleccionado.Should().BeNull();
        seleccion.SesionPrivilegiadaIdSeleccionada.Should().BeNull();
    }

    [Fact]
    public async Task Quita_el_rol_de_todas_las_identidades_del_principal()
    {
        // Un principal puede llevar varias identidades (cookie + externa). Con
        // dejar una sola con rol, IsInRole seguiría diciendo que sí.
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: null, sesionPrivilegiadaId: Guid.NewGuid());

        var principal = new ClaimsPrincipal(
        [
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Usuario.ToString())], "cookie"),
            new ClaimsIdentity([new Claim(ClaimTypes.Role, Roles.DireccionCae)], "externa"),
        ]);

        var contexto = new DefaultHttpContext { User = principal };
        contexto.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={token}";

        await EjecutarAsync(contexto, protector);

        contexto.User.IsInRole(Roles.DireccionCae).Should().BeFalse();
    }

    private static async Task EjecutarAsync(HttpContext contexto, IDataProtectionProvider protector)
    {
        var siguienteFueLlamado = false;
        var middleware = new SesionPrivilegiadaSinRolDeNegocioMiddleware(_ =>
        {
            siguienteFueLlamado = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(contexto, protector);

        siguienteFueLlamado.Should().BeTrue("el middleware nunca corta la petición: solo recorta el principal");
    }

    private static DefaultHttpContext ContextoCon(string? valorCookie, string rol)
    {
        var identidad = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Usuario.ToString()),
            new Claim(ClaimTypes.Role, rol),
        ], "prueba");

        var contexto = new DefaultHttpContext { User = new ClaimsPrincipal(identidad) };

        if (valorCookie is not null)
            contexto.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={valorCookie}";

        return contexto;
    }

    private static IDataProtectionProvider ProtectorDePruebas() =>
        DataProtectionProvider.Create(nameof(SesionPrivilegiadaSinRolDeNegocioMiddlewareTests));

    private sealed class HttpContextAccessorFijoLocal(HttpContext httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => httpContext;
            set => throw new NotSupportedException();
        }
    }
}
