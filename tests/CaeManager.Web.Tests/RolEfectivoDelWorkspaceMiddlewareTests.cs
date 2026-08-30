using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace CaeManager.Web.Tests;

/// <summary>
/// El escenario adversario que dio origen a este middleware: <b>Administrador
/// en el tenant A, Consulta en el tenant B</b>, operando el workspace de B.
///
/// <para>
/// Las 30 puertas <c>[Authorize(Roles = …)]</c> del portal preguntan al
/// <c>ClaimsPrincipal</c>, no a <c>CurrentUserService</c>. Mientras el claim
/// siguiera siendo el del tenant de ORIGEN, esas puertas contestaban que sí
/// dentro del tenant visitado: Configuración, Roles, Claves de API, Auditoría,
/// Integraciones e Importaciones de un cliente sobre el que solo se tenía
/// permiso de lectura. Estos tests fijan que en el workspace delegado manda el
/// rol de la cartera, y solo ese.
/// </para>
/// </summary>
public class RolEfectivoDelWorkspaceMiddlewareTests
{
    private static readonly Guid Usuario = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantVisitado = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Un_administrador_en_su_tenant_delegado_como_consulta_no_es_administrador_en_el_visitado()
    {
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: Guid.NewGuid());

        var contexto = ContextoCon(token, rolDeSesion: Roles.Administrador);

        await EjecutarAsync(contexto, protector, rolEfectivo: Roles.Consulta);

        contexto.User.IsInRole(Roles.Administrador).Should().BeFalse(
            "ser Administrador del tenant propio no concede administración sobre el tenant que se opera");
        contexto.User.IsInRole(Roles.Consulta).Should().BeTrue(
            "el rol que manda en un workspace delegado es el de su cartera");
        contexto.User.FindAll(ClaimTypes.Role).Should().ContainSingle(
            "el sistema asigna exactamente un rol: dejar dos haría que IsInRole contestara que sí a los dos");
    }

    [Fact]
    public async Task La_identidad_del_usuario_no_se_toca()
    {
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: Guid.NewGuid());

        var contexto = ContextoCon(token, rolDeSesion: Roles.Administrador);

        await EjecutarAsync(contexto, protector, rolEfectivo: Roles.GestorCae);

        // La auditoría necesita saber quién actuó: cambiarle el rol no es
        // cambiarle el nombre.
        contexto.User.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be(Usuario.ToString());
        contexto.User.Identity!.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task Una_delegacion_revocada_deja_el_principal_sin_ningun_rol()
    {
        // CurrentUserService devuelve null cuando la cartera ya no está viva
        // (fallo cerrado). Ese null tiene que llegar hasta las puertas: sin
        // rol, todas fallan cerradas, sin esperar a que la revalidación
        // posterior invalide la selección.
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: Guid.NewGuid());

        var contexto = ContextoCon(token, rolDeSesion: Roles.Administrador);

        await EjecutarAsync(contexto, protector, rolEfectivo: null);

        contexto.User.FindAll(ClaimTypes.Role).Should().BeEmpty();
        contexto.User.IsInRole(Roles.Administrador).Should().BeFalse();
    }

    [Fact]
    public async Task Sin_cookie_de_seleccion_el_rol_se_conserva_y_no_se_consulta_la_base()
    {
        // El caso de la inmensa mayoría: ni descifrado ni consulta.
        var contexto = ContextoCon(valorCookie: null, rolDeSesion: Roles.Administrador);
        var servicio = new CurrentUserServiceFalso(rolEfectivo: Roles.Consulta);

        await EjecutarAsync(contexto, ProtectorDePruebas(), servicio);

        contexto.User.IsInRole(Roles.Administrador).Should().BeTrue();
        servicio.VecesConsultado.Should().Be(0, "sin selección no hay nada que resolver");
    }

    [Fact]
    public async Task Bajo_sesion_privilegiada_no_devuelve_ningun_rol()
    {
        // El plano 3 lo resolvió SesionPrivilegiadaSinRolDeNegocioMiddleware
        // quitando el rol entero. Si este middleware volviera a escribir uno,
        // le devolvería al técnico de soporte la autoridad que aquel le quitó
        // — un middleware deshaciendo al anterior, y en el orden en que están
        // registrados nadie lo notaría.
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: null,
            sesionPrivilegiadaId: Guid.NewGuid());

        var contexto = ContextoCon(token, rolDeSesion: Roles.Administrador);
        contexto.User.FindAll(ClaimTypes.Role).ToList()
            .ForEach(c => ((ClaimsIdentity)contexto.User.Identity!).RemoveClaim(c));

        var servicio = new CurrentUserServiceFalso(rolEfectivo: Roles.Administrador);
        await EjecutarAsync(contexto, protector, servicio);

        contexto.User.FindAll(ClaimTypes.Role).Should().BeEmpty();
        servicio.VecesConsultado.Should().Be(0, "el plano 3 ya está resuelto y no se vuelve a tocar");
    }

    [Fact]
    public async Task Un_token_de_otro_usuario_no_cambia_el_rol_de_quien_lo_reenvia()
    {
        // El token está ligado a su usuario: en la sesión de otro no abre
        // ningún workspace, así que manda el claim de sesión.
        var protector = ProtectorDePruebas();
        var tokenDeOtro = ClienteActivoSeleccionado.Proteger(
            protector, Guid.NewGuid(), TenantVisitado, asignacionOperacionId: Guid.NewGuid());

        var contexto = ContextoCon(tokenDeOtro, rolDeSesion: Roles.Administrador);
        var servicio = new CurrentUserServiceFalso(rolEfectivo: Roles.Consulta);

        await EjecutarAsync(contexto, protector, servicio);

        contexto.User.IsInRole(Roles.Administrador).Should().BeTrue();
        servicio.VecesConsultado.Should().Be(0);
    }

    [Fact]
    public async Task Un_usuario_sin_autenticar_no_provoca_consulta_alguna()
    {
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: Guid.NewGuid());

        var contexto = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        contexto.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={token}";

        var servicio = new CurrentUserServiceFalso(rolEfectivo: Roles.Administrador);
        await EjecutarAsync(contexto, protector, servicio);

        servicio.VecesConsultado.Should().Be(0, "quien no ha entrado todavía no tiene rol que ajustar");
    }

    [Fact]
    public async Task Retira_el_rol_de_todas_las_identidades_del_principal()
    {
        // Un principal puede llevar varias identidades (cookie + externa).
        // Dejar una sola con el rol viejo bastaría para que IsInRole siguiera
        // contestando que sí.
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: Guid.NewGuid());

        var principal = new ClaimsPrincipal(
        [
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Usuario.ToString())], "cookie"),
            new ClaimsIdentity([new Claim(ClaimTypes.Role, Roles.Administrador)], "externa"),
        ]);

        var contexto = new DefaultHttpContext { User = principal };
        contexto.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={token}";

        await EjecutarAsync(contexto, protector, rolEfectivo: Roles.Consulta);

        contexto.User.IsInRole(Roles.Administrador).Should().BeFalse();
        contexto.User.IsInRole(Roles.Consulta).Should().BeTrue();
    }

    [Theory]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_content/paquete/estilo.css")]
    public async Task Los_ficheros_de_infraestructura_no_cuestan_una_consulta(string ruta)
    {
        // Este middleware corre ANTES del enrutado, porque después las puertas
        // de rol ya habrían contestado. La consecuencia es que también ve los
        // ficheros estáticos: sin este corte, cada JS y cada CSS de un Operador
        // Delegado pagaría una consulta a base de datos. Nada de lo que cuelga
        // de estos prefijos puede llevar [Authorize(Roles = ...)].
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: Guid.NewGuid());

        var contexto = ContextoCon(token, rolDeSesion: Roles.Administrador);
        contexto.Request.Path = ruta;

        var servicio = new CurrentUserServiceFalso(rolEfectivo: Roles.Consulta);
        await EjecutarAsync(contexto, protector, servicio);

        servicio.VecesConsultado.Should().Be(0);
    }

    [Fact]
    public async Task La_negociacion_del_circuito_si_ajusta_el_rol()
    {
        // /_blazor queda FUERA del recorte a propósito: por ahí se negocia el
        // circuito, que es justo la petición en la que el principal corregido
        // tiene que llegar. Recortarlo devolvería la escalada dentro del
        // circuito, que es donde vive la aplicación.
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: Guid.NewGuid());

        var contexto = ContextoCon(token, rolDeSesion: Roles.Administrador);
        contexto.Request.Path = "/_blazor/negotiate";

        await EjecutarAsync(contexto, protector, rolEfectivo: Roles.Consulta);

        contexto.User.IsInRole(Roles.Administrador).Should().BeFalse();
        contexto.User.IsInRole(Roles.Consulta).Should().BeTrue();
    }

    private static Task EjecutarAsync(HttpContext contexto, IDataProtectionProvider protector, string? rolEfectivo) =>
        EjecutarAsync(contexto, protector, new CurrentUserServiceFalso(rolEfectivo));

    private static async Task EjecutarAsync(
        HttpContext contexto, IDataProtectionProvider protector, CurrentUserServiceFalso servicio)
    {
        var siguienteFueLlamado = false;
        var middleware = new RolEfectivoDelWorkspaceMiddleware(_ =>
        {
            siguienteFueLlamado = true;
            return Task.CompletedTask;
        });

        var seleccion = new ClienteActivoSeleccionado(new HttpContextAccessorFijoLocal(contexto), protector);

        await middleware.InvokeAsync(contexto, seleccion, servicio);

        siguienteFueLlamado.Should().BeTrue("el middleware nunca corta la petición: solo ajusta el principal");
    }

    private static DefaultHttpContext ContextoCon(string? valorCookie, string rolDeSesion)
    {
        var identidad = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Usuario.ToString()),
            new Claim(ClaimTypes.Role, rolDeSesion),
        ], "prueba");

        var contexto = new DefaultHttpContext { User = new ClaimsPrincipal(identidad) };

        if (valorCookie is not null)
            contexto.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={valorCookie}";

        return contexto;
    }

    private static IDataProtectionProvider ProtectorDePruebas() =>
        DataProtectionProvider.Create(nameof(RolEfectivoDelWorkspaceMiddlewareTests));

    /// <summary>
    /// Cuenta las consultas además de responderlas: varios de estos tests
    /// afirman que el middleware <b>no</b> consulta, y sin el contador esa
    /// afirmación no sería observable.
    /// </summary>
    private sealed class CurrentUserServiceFalso(string? rolEfectivo) : ICurrentUserService
    {
        public int VecesConsultado { get; private set; }

        public Task<string?> ObtenerRolActualAsync()
        {
            VecesConsultado++;
            return Task.FromResult(rolEfectivo);
        }

        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Usuario);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
    }

    private sealed class HttpContextAccessorFijoLocal(HttpContext httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => httpContext;
            set => throw new NotSupportedException();
        }
    }
}
