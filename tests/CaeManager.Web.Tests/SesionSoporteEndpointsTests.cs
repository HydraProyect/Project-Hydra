using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using CaeManager.Application.Plataforma.Commands.CerrarSesionPrivilegiada;
using CaeManager.Domain.Common;
using CaeManager.Web.Features.Plataforma;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CaeManager.Web.Tests;

/// <summary>
/// <b>Lo que decide el endpoint de apertura, y solo eso.</b>
///
/// <para>
/// Las precondiciones de la ceremonia —2FA, tenant ajeno, concesión propia, capacidad que
/// abre, cobertura del ámbito— viven en <see cref="AbrirSesionPrivilegiadaCommand"/> y ya
/// las cubre <c>AbrirSesionPrivilegiadaTests</c>. Repetirlas aquí crearía un segundo sitio
/// donde decidir lo mismo, que es un segundo sitio donde pueden dejar de coincidir.
/// </para>
///
/// <para>
/// Lo que <b>no</b> cubre nadie más, y es exactamente lo que este endpoint añade:
/// (1) que no se abra un acceso desde dentro de otro workspace; (2) que un comando fallido
/// <b>no</b> deje cookie —que el comando falle no basta si el endpoint abre el contexto
/// igualmente—; y (3) que el token nombre la <b>sesión</b> y deje la operación en nulo.
/// </para>
///
/// <para>
/// El tercero no es cosmético: el lector del token descarta entero uno que nombre a la vez
/// operación y sesión, así que emitirlo mal no daría un error visible — dejaría al técnico
/// en su propio tenant creyendo estar en el visitado. Por eso se comprueba descifrando el
/// token emitido, no leyendo el argumento que se le pasó.
/// </para>
/// </summary>
public class SesionSoporteEndpointsTests
{
    private static readonly Guid Usuario = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantObjetivo = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Concesion = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Sesion = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task No_abre_un_acceso_desde_dentro_de_otro_workspace()
    {
        var contexto = new DefaultHttpContext();
        var mediator = new MediatorFalso();

        var resultado = await Invocar(
            contexto, mediator,
            // Ya hay un workspace activo: la guarda tiene que cortar ANTES de despachar.
            seleccion: new ClienteActivoSeleccionadoFalso(tenantIdSeleccionado: TenantObjetivo));

        resultado.Should().BeOfType<ForbidHttpResult>(
            "abrir una sesión desde dentro de otro workspace acumularía dos vías de acceso, y " +
            "además el INSERT moriría con 42501 porque la conexión ya lleva el rol de solo lectura");

        mediator.Enviados.Should().BeEmpty(
            "la guarda es del endpoint: si despachara y luego rechazara, habría creado una sesión " +
            "en la base que nadie va a usar");

        CookieEmitida(contexto).Should().BeNull();
    }

    [Fact]
    public async Task Un_comando_fallido_no_deja_cookie()
    {
        var contexto = new DefaultHttpContext();
        var mediator = new MediatorFalso
        {
            Respuesta = Result.Fallo<Guid>(Error.Crear("SesionPrivilegiada.SinDobleFactor", "sin 2FA")),
        };

        var resultado = await Invocar(contexto, mediator);

        CookieEmitida(contexto).Should().BeNull(
            "es la mitad del contrato que ningún test del comando puede cubrir: que el comando " +
            "rechace no sirve de nada si el endpoint abre el contexto igualmente");

        resultado.Should().BeOfType<RedirectHttpResult>()
            .Which.Url.Should().Contain("errorSoporte=SesionPrivilegiada.SinDobleFactor",
                "la pantalla traduce el código; no se filtra el texto de la excepción");
    }

    [Fact]
    public async Task El_token_emitido_nombra_la_sesion_y_no_una_operacion()
    {
        var contexto = new DefaultHttpContext();
        var protector = ProtectorDePruebas();
        var mediator = new MediatorFalso { Respuesta = Result.Exito(Sesion) };

        var resultado = await Invocar(contexto, mediator, protector: protector);

        resultado.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/",
            "la pantalla de origen es de plataforma y no existe dentro del tenant visitado");

        var cookie = CookieEmitida(contexto);
        cookie.Should().NotBeNull();

        // Se descifra lo emitido en vez de confiar en el argumento: es la única forma de
        // comprobar que la carga útil es la que el lector aceptará.
        var (tenantId, asignacionOperacionId, sesionPrivilegiadaId) =
            ClienteActivoSeleccionado.LeerCargaUtil(protector, cookie, Usuario);

        tenantId.Should().Be(TenantObjetivo);
        sesionPrivilegiadaId.Should().Be(Sesion);
        asignacionOperacionId.Should().BeNull(
            "un token que nombre a la vez operación y sesión lo descarta ENTERO el lector, y el " +
            "técnico caería en silencio a su propio tenant creyendo estar en el visitado");
    }

    [Fact]
    public async Task Sin_usuario_autenticado_no_despacha_ni_emite_nada()
    {
        var contexto = new DefaultHttpContext();
        var mediator = new MediatorFalso();

        var resultado = await Invocar(contexto, mediator, sinUsuario: true);

        resultado.Should().BeOfType<UnauthorizedHttpResult>();
        mediator.Enviados.Should().BeEmpty();
        CookieEmitida(contexto).Should().BeNull();
    }

    // ── Salida (B1.3) ──────────────────────────────────────────────────────

    /// <summary>
    /// El efecto que importa de la salida: la cookie deja de viajar. Se observa en
    /// la cabecera real —una cookie se borra emitiendo un <c>Set-Cookie</c> vacío y
    /// caducado, no omitiéndola— porque un doble diría que sí sin que el navegador
    /// llegue a hacerlo.
    /// </summary>
    [Fact]
    public async Task Al_salir_se_borra_la_cookie_de_contexto()
    {
        var contexto = new DefaultHttpContext();

        var resultado = await InvocarSalir(
            contexto, new ClienteActivoSeleccionadoFalso(sesionPrivilegiadaIdSeleccionada: Sesion));

        CookieBorrada(contexto).Should().BeTrue(
            "mientras la cookie siga viajando, el interceptor sigue haciendo SET ROLE al rol de solo " +
            "lectura y el cierre no podría escribir");

        resultado.Should().BeOfType<RedirectHttpResult>()
            .Which.Url.Should().Be($"/configuracion/plataforma?sesionParaCerrar={Sesion}",
                "al volver ya no hay cookie de la que sacar el id, y el contrato de lectura del plano 3 " +
                "prohíbe un 'listar sesiones': si la redirección no lo lleva, no queda de dónde sacarlo");
    }

    /// <summary>
    /// Este endpoint no es un "olvida el workspace" genérico. Sin sesión de plano 3
    /// corta, en vez de borrar la cookie igualmente: si la borrara, sería una
    /// segunda forma de salir del plano 2 sin pasar por las reglas de
    /// <c>/cuenta/cliente-activo</c>.
    /// </summary>
    [Fact]
    public async Task Salir_no_borra_la_cookie_de_un_workspace_de_plano_2()
    {
        var contexto = new DefaultHttpContext();

        var resultado = await InvocarSalir(
            contexto, new ClienteActivoSeleccionadoFalso(tenantIdSeleccionado: TenantObjetivo));

        resultado.Should().BeOfType<ForbidHttpResult>();
        CookieBorrada(contexto).Should().BeFalse();
    }

    [Fact]
    public async Task Salir_sin_usuario_autenticado_no_toca_la_cookie()
    {
        var contexto = new DefaultHttpContext();

        var resultado = await InvocarSalir(
            contexto, new ClienteActivoSeleccionadoFalso(sesionPrivilegiadaIdSeleccionada: Sesion),
            sinUsuario: true);

        resultado.Should().BeOfType<UnauthorizedHttpResult>();
        CookieBorrada(contexto).Should().BeFalse();
    }

    // ── Cierre (B1.3) ──────────────────────────────────────────────────────

    /// <summary>
    /// <b>La guarda que hace posible la opción B.</b> Desde dentro del tenant
    /// visitado el cierre no se intenta siquiera: la conexión lleva
    /// <c>SET ROLE cae_app_soporte</c> —solo <c>SELECT</c>— y
    /// <c>AutorizacionEscrituraBehavior</c> ya memoizó la sesión, así que el
    /// <c>UPDATE</c> moriría por partida doble.
    ///
    /// <para>
    /// Se comprueba además que <b>no despacha</b>. Cortar después de despachar
    /// daría el mismo código de respuesta y sería un test verde sobre un endpoint
    /// que ya habría pedido a la base una escritura imposible.
    /// </para>
    /// </summary>
    [Fact]
    public async Task No_cierra_mientras_el_tecnico_sigue_dentro_del_tenant_visitado()
    {
        // Respuesta preparada a propósito aunque no deba usarse: si la guarda
        // desapareciera, el test tiene que fallar por su aserción y no por un cast
        // nulo. Un rojo por excepción no demuestra que se observara la propiedad.
        var mediator = new MediatorFalso { Respuesta = Result.Exito() };

        var resultado = await InvocarCerrar(
            mediator, new ClienteActivoSeleccionadoFalso(sesionPrivilegiadaIdSeleccionada: Sesion));

        resultado.Should().BeOfType<ForbidHttpResult>(
            "primero se sale y después se cierra: son dos peticiones porque borrar la cookie de la " +
            "respuesta no cambia nada dentro de la petición que se está sirviendo");

        mediator.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Ya_fuera_el_cierre_despacha_el_comando_con_la_sesion_indicada()
    {
        var mediator = new MediatorFalso { Respuesta = Result.Exito() };

        var resultado = await InvocarCerrar(mediator, new ClienteActivoSeleccionadoFalso());

        mediator.Enviados.Should().ContainSingle()
            .Which.Should().BeOfType<CerrarSesionPrivilegiadaCommand>()
            .Which.SesionPrivilegiadaId.Should().Be(Sesion);

        resultado.Should().BeOfType<RedirectHttpResult>()
            .Which.Url.Should().Be("/configuracion/plataforma?cerrada=1");
    }

    /// <summary>
    /// Un cierre fallido lleva el <b>código</b> a la pantalla, no el texto de una
    /// excepción — mismo criterio que la apertura. El caso vivo es el reenvío del
    /// formulario: cerrar dos veces da <c>YaCerrada</c> y tiene que verse como un
    /// mensaje, no como un 500.
    /// </summary>
    [Fact]
    public async Task Un_cierre_fallido_lleva_su_codigo_a_la_pantalla()
    {
        var mediator = new MediatorFalso
        {
            Respuesta = Result.Fallo(Error.Crear(
                "SesionPrivilegiada.YaCerrada", "Esa sesión de soporte ya estaba cerrada.")),
        };

        var resultado = await InvocarCerrar(mediator, new ClienteActivoSeleccionadoFalso());

        resultado.Should().BeOfType<RedirectHttpResult>()
            .Which.Url.Should().Be("/configuracion/plataforma?errorCierre=SesionPrivilegiada.YaCerrada");
    }

    [Fact]
    public async Task Cerrar_sin_usuario_autenticado_no_despacha_nada()
    {
        var mediator = new MediatorFalso();

        var resultado = await InvocarCerrar(mediator, new ClienteActivoSeleccionadoFalso(), sinUsuario: true);

        resultado.Should().BeOfType<UnauthorizedHttpResult>();
        mediator.Enviados.Should().BeEmpty();
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private static Task<IResult> InvocarSalir(
        HttpContext contexto, IClienteActivoSeleccionado seleccion, bool sinUsuario = false) =>
        SesionSoporteEndpoints.SalirAsync(
            contexto, new CurrentUserServiceFalso(sinUsuario ? null : Usuario), seleccion);

    private static Task<IResult> InvocarCerrar(
        IMediator mediator, IClienteActivoSeleccionado seleccion, bool sinUsuario = false) =>
        SesionSoporteEndpoints.CerrarAsync(
            Sesion, mediator, new CurrentUserServiceFalso(sinUsuario ? null : Usuario), seleccion);

    /// <summary>
    /// Si la respuesta borra la cookie de contexto. Borrar es emitir un
    /// <c>Set-Cookie</c> con valor vacío y caducidad en el pasado, así que no basta
    /// con mirar si hay cabecera: la apertura también emite una y pasaría por
    /// borrado.
    /// </summary>
    private static bool CookieBorrada(HttpContext contexto) =>
        contexto.Response.Headers.SetCookie.Any(c =>
            c is not null
            && c.StartsWith(ClienteActivoSeleccionado.NombreCookie + "=;", StringComparison.Ordinal)
            && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));

    private static Task<IResult> Invocar(
        HttpContext contexto,
        IMediator mediator,
        IClienteActivoSeleccionado? seleccion = null,
        IDataProtectionProvider? protector = null,
        // Bandera y no un Guid? nullable: con "usuario ?? Usuario" un null explícito
        // volvía a ser el usuario por defecto, así que el test del no autenticado
        // ejercitaba el camino contrario al que decía. Lo detectó una
        // NullReferenceException, no una aserción — el test habría podido pasar por el
        // motivo equivocado con otro doble.
        bool sinUsuario = false) =>
        SesionSoporteEndpoints.AbrirAsync(
            Concesion, TenantObjetivo, "Incidencia 4021", 4, ticket: null, returnUrl: null,
            contexto, mediator,
            new CurrentUserServiceFalso(sinUsuario ? null : Usuario),
            seleccion ?? new ClienteActivoSeleccionadoFalso(),
            protector ?? ProtectorDePruebas());

    /// <summary>
    /// El valor de la cookie de contexto en la respuesta, o <c>null</c> si no se emitió
    /// ninguna. Se lee de la cabecera real, no de un doble: emitirla es el efecto que
    /// importa y hay que observarlo donde ocurre.
    /// </summary>
    private static string? CookieEmitida(HttpContext contexto)
    {
        var cabecera = contexto.Response.Headers.SetCookie
            .FirstOrDefault(c => c is not null && c.StartsWith(
                ClienteActivoSeleccionado.NombreCookie + "=", StringComparison.Ordinal));

        if (cabecera is null) return null;

        var valor = cabecera[(ClienteActivoSeleccionado.NombreCookie.Length + 1)..];
        var fin = valor.IndexOf(';');
        return fin < 0 ? valor : valor[..fin];
    }

    private static IDataProtectionProvider ProtectorDePruebas() =>
        DataProtectionProvider.Create(nameof(SesionSoporteEndpointsTests));

    private sealed class CurrentUserServiceFalso(Guid? usuarioId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(usuarioId);

        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>(null);

        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);

        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class ClienteActivoSeleccionadoFalso(
        Guid? tenantIdSeleccionado = null, Guid? sesionPrivilegiadaIdSeleccionada = null)
        : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantIdSeleccionado;

        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaIdSeleccionada;
    }

    private sealed class MediatorFalso : IMediator
    {
        public List<object> Enviados { get; } = [];

        public object? Respuesta { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return Task.FromResult((TResponse)Respuesta!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
        {
            Enviados.Add(request!);
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return Task.FromResult(Respuesta);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
