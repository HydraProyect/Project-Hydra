using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    public async Task Quien_administra_por_encargo_lleva_el_claim_del_encargo_junto_al_rol_elevado()
    {
        var protector = ProtectorDePruebas();
        var encargo = Guid.NewGuid();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: Guid.NewGuid());
        var contexto = ContextoCon(token, rolDeSesion: Roles.Administrador);

        await EjecutarAsync(contexto, protector, new CurrentUserServiceFalso(Roles.Administrador, encargo));

        contexto.User.IsInRole(Roles.Administrador).Should().BeTrue();
        contexto.User.FindAll(RolEfectivoDelWorkspaceMiddleware.TipoClaimEncargoAdministracion)
            .Should().ContainSingle("las páginas excluidas se niegan mirando este claim")
            .Which.Value.Should().Be(encargo.ToString());
    }

    [Fact]
    public async Task Sin_encargo_que_eleve_el_claim_del_encargo_no_sobrevive_aunque_viniera_en_el_principal()
    {
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: Guid.NewGuid());
        var contexto = ContextoCon(token, rolDeSesion: Roles.Administrador);
        ((ClaimsIdentity)contexto.User.Identity!).AddClaim(new Claim(
            RolEfectivoDelWorkspaceMiddleware.TipoClaimEncargoAdministracion, Guid.NewGuid().ToString()));

        await EjecutarAsync(contexto, protector, rolEfectivo: Roles.GestorCae);

        contexto.User.HasClaim(c => c.Type == RolEfectivoDelWorkspaceMiddleware.TipoClaimEncargoAdministracion)
            .Should().BeFalse("el claim solo lo pone el middleware, en cada petición, a partir de la resolución del rol");
    }

    [Fact]
    public async Task Un_encargo_sin_rol_efectivo_no_deja_el_claim()
    {
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: Guid.NewGuid());
        var contexto = ContextoCon(token, rolDeSesion: Roles.Administrador);

        await EjecutarAsync(contexto, protector, new CurrentUserServiceFalso(rolEfectivo: null, Guid.NewGuid()));

        contexto.User.HasClaim(c => c.Type == RolEfectivoDelWorkspaceMiddleware.TipoClaimEncargoAdministracion)
            .Should().BeFalse();
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

    [Fact]
    public async Task Sustituir_el_rol_deja_rastro_en_el_log_como_informativo()
    {
        // REC-189: hasta esta prueba, ninguna sustitución de rol por
        // delegación dejaba rastro. Se comprueba el nivel y el contenido, no
        // solo que "algo" se registrara: un aviso vacío pasaría igual.
        //
        // Nivel Information, no Warning: operar un workspace delegado con el
        // rol de su cartera es el camino feliz de cualquier Operador
        // Delegado activo, no una anomalía — avisar con Warning en cada
        // petición fabricaría el ruido que REC-162 documentó (decisión
        // corregida tras la revisión de REC-189, ver el comentario en
        // RolEfectivoDelWorkspaceMiddleware).
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: Guid.NewGuid());

        var contexto = ContextoCon(token, rolDeSesion: Roles.Administrador);
        var capturador = new LoggerCapturador<RolEfectivoDelWorkspaceMiddleware>();

        await EjecutarAsync(contexto, protector, new CurrentUserServiceFalso(Roles.Consulta), capturador);

        var evento = capturador.Eventos.Should().ContainSingle().Subject;
        evento.Nivel.Should().Be(LogLevel.Information);
        evento.Mensaje.Should().Contain(Roles.Administrador).And.Contain(Roles.Consulta)
            .And.Contain(TenantVisitado.ToString());
    }

    [Fact]
    public async Task Retirar_el_rol_por_delegacion_revocada_deja_rastro_en_el_log_como_aviso()
    {
        // A diferencia de la sustitución rutinaria, retirar el rol entero
        // significa que la delegación ya no vale: es la anomalía real, y
        // comparte nivel con RevalidacionClienteActivoMiddleware.
        var protector = ProtectorDePruebas();
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: Guid.NewGuid());

        var contexto = ContextoCon(token, rolDeSesion: Roles.Administrador);
        var capturador = new LoggerCapturador<RolEfectivoDelWorkspaceMiddleware>();

        await EjecutarAsync(contexto, protector, new CurrentUserServiceFalso(rolEfectivo: null), capturador);

        var evento = capturador.Eventos.Should().ContainSingle().Subject;
        evento.Nivel.Should().Be(LogLevel.Warning);
        evento.Mensaje.Should().Contain(Roles.Administrador).And.Contain("sin sustituto")
            .And.Contain(TenantVisitado.ToString());
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

    /// <summary>
    /// Decisión P7 (2026-09-23), defecto real: el fan-out multi-Tenant
    /// (<c>ObtenerMiTrabajoAgregadoQuery</c>, <c>ObtenerKpisGlobalesQuery</c>,
    /// <c>ObtenerDashboardEjecutivoQuery</c>) visita también el Tenant de
    /// ORIGEN, y para él <c>CurrentUserService</c> devolvía «el claim de
    /// sesión». Pero con un Workspace operativo derivado seleccionado ese claim
    /// ya lo ha sustituido este middleware por el rol de la cartera en el
    /// Tenant propietario visitado: el alcance del Tenant de origen se
    /// calculaba con el rol de otro Tenant. Aquí se compone el middleware real
    /// con el <c>CurrentUserService</c> real sobre el mismo principal, que es
    /// exactamente lo que ve el circuito de Blazor.
    /// </summary>
    [Fact]
    public async Task Tras_el_ajuste_el_fan_out_al_tenant_de_origen_usa_el_rol_de_sesion_de_origen_no_el_de_la_cartera()
    {
        var protector = ProtectorDePruebas();
        var tenantOrigen = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: Guid.NewGuid());

        var contexto = ContextoCon(token, rolDeSesion: Roles.Administrador);
        ((ClaimsIdentity)contexto.User.Identity!).AddClaim(
            new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, tenantOrigen.ToString()));

        await EjecutarAsync(contexto, protector, rolEfectivo: Roles.Consulta);
        contexto.User.IsInRole(Roles.Consulta).Should().BeTrue(
            "control positivo: el middleware ya sustituyó el claim por el rol de la cartera, que es la condición del defecto");

        var accesor = new HttpContextAccessorFijoLocal(contexto);
        var servicioReal = new CurrentUserService(
            new SinCircuitoDeBlazor(),
            accesor,
            new ClienteActivoSeleccionado(accesor, protector),
            new ServiceCollection().BuildServiceProvider());

        using (AmbitoTenantExplicito.Establecer(tenantOrigen))
        {
            (await servicioReal.ObtenerRolEfectivoAsync()).Should().Be(Roles.Administrador,
                "en su propio Tenant de origen el usuario tiene el rol de su sesión, no el de la cartera del Tenant "
                + "propietario que tenga seleccionado");
        }
    }

    /// <summary>
    /// Hallazgo del 2026-10-09 (recorrido en vivo del piloto Outbound, LV-8):
    /// el principal con el que nace el circuito ya trae el claim de rol
    /// sustituido por el de la Asignación de Cartera del Tenant propietario
    /// seleccionado, y ese principal vive lo que viva el circuito. Si dentro
    /// del circuito la selección se retira —<c>RevalidacionCircuitoActivoHandler</c>
    /// llama a <c>Invalidar()</c> cuando la Asignación de Cartera caduca o se
    /// revoca—, <c>TenantActual</c> vuelve al Tenant de origen, y el rol
    /// efectivo tiene que volver con él al de la sesión en origen. Devolver el
    /// claim sustituido autorizaba escrituras en el Tenant de origen con el rol
    /// que la Operación concedió en OTRO Tenant: una coordenada de contexto
    /// convertida en autoridad.
    ///
    /// <para>
    /// Se compone el middleware real con la selección real y el
    /// <c>CurrentUserService</c> real sobre el mismo principal, que es el
    /// estado que construye el producto. La tercera fila es el mismo defecto en
    /// el sentido contrario: un Administrador de su Tenant de origen se quedaba
    /// en él con el rol menor de la cartera.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(Roles.Consulta, Roles.GestorCae)]
    [InlineData(Roles.GestorCae, Roles.CoordinadorCae)]
    [InlineData(Roles.Administrador, Roles.Consulta)]
    public async Task Retirada_la_seleccion_dentro_del_circuito_el_rol_efectivo_es_el_de_sesion_en_origen_no_el_de_la_cartera(
        string rolDeSesionEnOrigen, string rolDeLaCartera)
    {
        var protector = ProtectorDePruebas();
        var negociacion = await NegociarCircuitoAsync(protector, rolDeSesionEnOrigen, rolDeLaCartera);

        // El circuito siembra la selección mientras el HttpContext de la
        // negociación sigue disponible (OnCircuitOpenedAsync) y después la
        // revalidación periódica la retira.
        var accesor = new HttpContextAccessorFijoLocal(negociacion);
        var seleccionDelCircuito = new ClienteActivoSeleccionado(accesor, protector);
        seleccionDelCircuito.TenantIdSeleccionado.Should().Be(TenantVisitado,
            "control positivo: el circuito nace operando el Tenant propietario seleccionado");
        seleccionDelCircuito.Invalidar();

        var servicio = new CurrentUserService(
            new CircuitoDeBlazorCon(negociacion.User), accesor, seleccionDelCircuito,
            new ServiceCollection().BuildServiceProvider());

        (await servicio.ObtenerRolEfectivoAsync()).Should().Be(rolDeSesionEnOrigen,
            "sin Tenant seleccionado se opera el Tenant de origen, y ahí el rol es el de la sesión en origen: el de "
            + "la Asignación de Cartera solo vale en el Tenant propietario que la concede");
    }

    /// <summary>
    /// El mismo estado por la otra vía: un circuito cuyo ámbito lee la
    /// selección cuando ya no hay <c>HttpContext</c> la memoiza nula, con el
    /// principal igualmente sustituido.
    /// </summary>
    [Fact]
    public async Task Un_circuito_que_lee_la_seleccion_sin_HttpContext_no_conserva_el_rol_de_la_cartera_en_el_Tenant_de_origen()
    {
        var protector = ProtectorDePruebas();
        var negociacion = await NegociarCircuitoAsync(protector, Roles.Consulta, Roles.GestorCae);

        var sinHttpContext = new HttpContextAccessorAusente();
        var seleccionDelCircuito = new ClienteActivoSeleccionado(sinHttpContext, protector);
        seleccionDelCircuito.TenantIdSeleccionado.Should().BeNull(
            "control positivo: sin HttpContext la selección se memoiza nula y el Tenant resuelto es el de origen");

        var servicio = new CurrentUserService(
            new CircuitoDeBlazorCon(negociacion.User), sinHttpContext, seleccionDelCircuito,
            new ServiceCollection().BuildServiceProvider());

        (await servicio.ObtenerRolEfectivoAsync()).Should().Be(Roles.Consulta);
    }

    /// <summary>
    /// Una cuenta SIN rol en su Tenant de origen (estado que el producto
    /// admite: se crea sin rol y se le asigna después) con una Asignación de
    /// Cartera en otro Tenant. Tras la sustitución, el único claim de rol del
    /// principal es el de la cartera; si el middleware no dejara constancia de
    /// que en origen no había ninguno, «el rol de sesión en origen» se leería
    /// del claim de rol y devolvería el de la cartera — el mismo defecto por
    /// la rama que no guardaba nada. Vale para las dos lecturas del rol de
    /// origen: sin selección y en el fan-out que visita el Tenant de origen.
    /// </summary>
    [Fact]
    public async Task Sin_rol_de_sesion_en_origen_el_rol_de_la_cartera_no_se_lee_como_rol_de_origen()
    {
        var protector = ProtectorDePruebas();
        var tenantOrigen = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var negociacion = await NegociarCircuitoAsync(protector, rolDeSesionEnOrigen: null, Roles.GestorCae);
        ((ClaimsIdentity)negociacion.User.Identity!).AddClaim(
            new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, tenantOrigen.ToString()));

        var accesor = new HttpContextAccessorFijoLocal(negociacion);
        var seleccionDelCircuito = new ClienteActivoSeleccionado(accesor, protector);
        var servicio = new CurrentUserService(
            new CircuitoDeBlazorCon(negociacion.User), accesor, seleccionDelCircuito,
            new ServiceCollection().BuildServiceProvider());

        string? rolEnElFanOutAlOrigen;
        using (AmbitoTenantExplicito.Establecer(tenantOrigen))
            rolEnElFanOutAlOrigen = await servicio.ObtenerRolEfectivoAsync();

        seleccionDelCircuito.Invalidar();
        var rolSinSeleccion = await servicio.ObtenerRolEfectivoAsync();

        // Las dos lecturas se afirman juntas: son dos ramas distintas del
        // servicio y la primera en fallar no debe tapar a la otra.
        using (new AssertionScope())
        {
            rolEnElFanOutAlOrigen.Should().BeNull(
                "el fan-out que visita el Tenant de origen no puede dar ahí el rol de la cartera de otro Tenant");
            rolSinSeleccion.Should().BeNull(
                "quien no tiene rol en su Tenant de origen no lo gana por haber operado la cartera de otro Tenant");
        }
    }

    /// <summary>
    /// La petición que negocia el circuito (<c>/_blazor</c>) tal como la deja
    /// el middleware real: claim de rol sustituido por el de la cartera.
    /// </summary>
    private static async Task<DefaultHttpContext> NegociarCircuitoAsync(
        IDataProtectionProvider protector, string? rolDeSesionEnOrigen, string rolDeLaCartera)
    {
        var token = ClienteActivoSeleccionado.Proteger(
            protector, Usuario, TenantVisitado, asignacionOperacionId: Guid.NewGuid());

        var contexto = ContextoCon(token, rolDeSesionEnOrigen);
        contexto.Request.Path = "/_blazor/negotiate";

        await EjecutarAsync(contexto, protector, rolEfectivo: rolDeLaCartera);
        contexto.User.FindAll(ClaimTypes.Role).Should().ContainSingle(c => c.Value == rolDeLaCartera,
            "control positivo: el principal con el que nace el circuito ya lleva el rol de la cartera, que es la "
            + "condición del defecto");

        return contexto;
    }

    private sealed class SinCircuitoDeBlazor : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            throw new InvalidOperationException("sin circuito");
    }

    /// <summary>El circuito conserva el principal del instante en que se conectó.</summary>
    private sealed class CircuitoDeBlazorCon(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class HttpContextAccessorAusente : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => null;
            set => throw new NotSupportedException();
        }
    }

    private static Task EjecutarAsync(HttpContext contexto, IDataProtectionProvider protector, string? rolEfectivo) =>
        EjecutarAsync(contexto, protector, new CurrentUserServiceFalso(rolEfectivo));

    private static Task EjecutarAsync(
        HttpContext contexto, IDataProtectionProvider protector, CurrentUserServiceFalso servicio) =>
        EjecutarAsync(contexto, protector, servicio, NullLogger<RolEfectivoDelWorkspaceMiddleware>.Instance);

    private static async Task EjecutarAsync(
        HttpContext contexto, IDataProtectionProvider protector, CurrentUserServiceFalso servicio,
        ILogger<RolEfectivoDelWorkspaceMiddleware> logger)
    {
        var siguienteFueLlamado = false;
        var middleware = new RolEfectivoDelWorkspaceMiddleware(_ =>
        {
            siguienteFueLlamado = true;
            return Task.CompletedTask;
        });

        var seleccion = new ClienteActivoSeleccionado(new HttpContextAccessorFijoLocal(contexto), protector);

        await middleware.InvokeAsync(contexto, seleccion, servicio, servicio, logger);

        siguienteFueLlamado.Should().BeTrue("el middleware nunca corta la petición: solo ajusta el principal");
    }

    private static DefaultHttpContext ContextoCon(string? valorCookie, string? rolDeSesion)
    {
        var identidad = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Usuario.ToString())], "prueba");

        // Null: una cuenta sin rol en su Tenant de origen.
        if (rolDeSesion is not null)
            identidad.AddClaim(new Claim(ClaimTypes.Role, rolDeSesion));

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
    private sealed class CurrentUserServiceFalso(string? rolEfectivo, Guid? encargoQueEleva = null)
        : ICurrentUserService, IEncargoDeAdministracionActual
    {
        public int VecesConsultado { get; private set; }

        public Task<Guid?> EncargoQueElevaAsync() => Task.FromResult(encargoQueEleva);
        public Guid? EncargoDeLaUltimaResolucion(Guid asignacionOperacionId) => encargoQueEleva;

        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync()
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

    /// <summary>
    /// Captura nivel y mensaje formateado en vez de solo contar llamadas: un
    /// aviso que se dispara pero no dice nada útil pasaría igual con un mero
    /// contador (REC-189).
    /// </summary>
    private sealed class LoggerCapturador<T> : ILogger<T>
    {
        public List<(LogLevel Nivel, string Mensaje)> Eventos { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => AmbitoVacio.Instancia;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Eventos.Add((logLevel, formatter(state, exception)));

        private sealed class AmbitoVacio : IDisposable
        {
            public static readonly AmbitoVacio Instancia = new();
            public void Dispose() { }
        }
    }
}
