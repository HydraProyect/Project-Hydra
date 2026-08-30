using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
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

    // ── Andamiaje ──────────────────────────────────────────────────────────

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
            Concesion, TenantObjetivo, "Incidencia 4021", 7, ticket: null, returnUrl: null,
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

    private sealed class ClienteActivoSeleccionadoFalso(Guid? tenantIdSeleccionado = null)
        : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantIdSeleccionado;

        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => null;
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
