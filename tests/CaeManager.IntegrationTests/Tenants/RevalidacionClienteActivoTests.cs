using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Hallazgo N-6 de INFORME-AUDITORIA-2.md: la delegación se comprobaba una
/// sola vez, al emitir el token de selección, y su lectura no hace I/O a
/// propósito. Revocada la delegación, un token vivo seguía concediendo acceso
/// al ex-cliente hasta caducar.
///
/// Se prueba el middleware contra un <c>DefaultHttpContext</c> real y las
/// clases de producción, no contra dobles: lo que importa es que el token que
/// el propio sistema emite deje de valer cuando la delegación muere.
/// </summary>
public class RevalidacionClienteActivoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly IDataProtectionProvider _protector = new EphemeralDataProtectionProvider();
    private readonly Guid _consultora = Guid.NewGuid();
    private readonly Guid _clienteDelegante = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();
    private Guid _delegacionId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var delegacion = new DelegacionTenant(_consultora, _clienteDelegante);
        contexto.DelegacionesTenant.Add(delegacion);
        contexto.AsignacionesOperadorDelegado.Add(
            new AsignacionOperadorDelegado(delegacion.Id, _usuario, "GestorCae"));

        await contexto.SaveChangesAsync();
        _delegacionId = delegacion.Id;
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Con_la_delegacion_activa_la_seleccion_sobrevive()
    {
        await using var contexto = CrearContexto();
        var (httpContext, seleccion) = PrepararPeticionConTokenValido();

        await EjecutarMiddlewareAsync(httpContext, seleccion, contexto);

        seleccion.TenantIdSeleccionado.Should().Be(_clienteDelegante);
        CabeceraDeBorradoDeCookie(httpContext).Should().BeNull();
    }

    [Fact]
    public async Task Revocada_la_delegacion_la_seleccion_deja_de_valer_en_la_misma_peticion()
    {
        await RevocarDelegacionAsync();

        await using var contexto = CrearContexto();
        var (httpContext, seleccion) = PrepararPeticionConTokenValido();

        await EjecutarMiddlewareAsync(httpContext, seleccion, contexto);

        // En la misma petición, no en la siguiente: borrar la cookie de la
        // respuesta no basta porque Request.Cookies sigue trayéndola.
        seleccion.TenantIdSeleccionado.Should().BeNull();
        CabeceraDeBorradoDeCookie(httpContext).Should().NotBeNull();
    }

    [Fact]
    public async Task Retirado_el_operador_su_seleccion_deja_de_valer_aunque_la_delegacion_siga_activa()
    {
        await using (var contextoRetirada = CrearContexto())
        {
            var asignacion = await contextoRetirada.AsignacionesOperadorDelegado.FirstAsync(a => a.UsuarioId == _usuario);
            contextoRetirada.AsignacionesOperadorDelegado.Remove(asignacion);
            await contextoRetirada.SaveChangesAsync();
        }

        await using var contexto = CrearContexto();
        var (httpContext, seleccion) = PrepararPeticionConTokenValido();

        await EjecutarMiddlewareAsync(httpContext, seleccion, contexto);

        seleccion.TenantIdSeleccionado.Should().BeNull();
    }

    [Fact]
    public async Task Una_ventana_de_soporte_caducada_corta_el_acceso_sin_que_nadie_la_revoque()
    {
        // La delegación sigue con Activa = true: lo que venció es la ventana.
        // Si la caducidad no se comprobara en cada petición, el operador de
        // soporte conservaría el acceso hasta que alguien se acordara de
        // cerrarlo a mano — que es justo lo que la ventana evita.
        await using (var contextoVencer = CrearContexto())
        {
            // Se sitúa la caducidad en el pasado, que es lo que habría hecho
            // el paso del tiempo sobre una ventana abierta.
            var delegacion = await contextoVencer.DelegacionesTenant.FirstAsync(d => d.Id == _delegacionId);
            contextoVencer.Entry(delegacion).Property(nameof(DelegacionTenant.ExpiraEnUtc))
                .CurrentValue = DateTime.UtcNow.AddMinutes(-1);
            await contextoVencer.SaveChangesAsync();
        }

        await using var contexto = CrearContexto();
        var (httpContext, seleccion) = PrepararPeticionConTokenValido();

        await EjecutarMiddlewareAsync(httpContext, seleccion, contexto);

        seleccion.TenantIdSeleccionado.Should().BeNull();
        CabeceraDeBorradoDeCookie(httpContext).Should().NotBeNull();
    }

    /// <summary>
    /// REC-136: la rama `else` de <see cref="RevalidacionClienteActivoMiddleware"/>
    /// (cookie presente, token que no resuelve a ningún tenant) borraba la cookie
    /// sin dejar ninguna traza — indistinguible en un artefacto de CI de la rama
    /// `!sigueAutorizado`, que sí avisaba desde REC-110. Este test comprueba que
    /// las dos rutas, aunque comparten el mismo efecto observable (cookie
    /// borrada), quedan cada una con su propio aviso, con textos distintos.
    /// </summary>
    [Fact]
    public async Task La_rama_else_deja_traza_distinguible_de_la_rama_de_revalidacion_fallida()
    {
        // Rama else: el valor de la cookie no es un token protegido válido, así
        // que TenantIdSeleccionado nunca resuelve a un tenant y no llega a
        // intentarse ninguna revalidación.
        var loggerElse = new LoggerCapturador<RevalidacionClienteActivoMiddleware>();
        await using (var contextoElse = CrearContexto())
        {
            var httpContextElse = new DefaultHttpContext { User = UsuarioAutenticado(_usuario) };
            httpContextElse.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}=valor-no-valido";
            var seleccionElse = new ClienteActivoSeleccionado(new HttpContextAccessorFalso(httpContextElse), _protector);

            var middlewareElse = new RevalidacionClienteActivoMiddleware(_ => Task.CompletedTask);
            await middlewareElse.InvokeAsync(
                httpContextElse, seleccionElse, new CurrentUserServiceParaMiddlewareFalso(_usuario),
                contextoElse, (IOperacionesQueryContext)contextoElse, SinSesionPrivilegiada, loggerElse);

            seleccionElse.TenantIdSeleccionado.Should().BeNull();
            CabeceraDeBorradoDeCookie(httpContextElse).Should().NotBeNull();
        }

        // Rama !sigueAutorizado: token válido, pero la delegación ya no está
        // viva. Mismo efecto observable (cookie borrada), motivo distinto.
        await RevocarDelegacionAsync();
        var loggerRevalidacion = new LoggerCapturador<RevalidacionClienteActivoMiddleware>();
        await using (var contextoRevalidacion = CrearContexto())
        {
            var (httpContextRevalidacion, seleccionRevalidacion) = PrepararPeticionConTokenValido();
            var middlewareRevalidacion = new RevalidacionClienteActivoMiddleware(_ => Task.CompletedTask);
            await middlewareRevalidacion.InvokeAsync(
                httpContextRevalidacion, seleccionRevalidacion, new CurrentUserServiceParaMiddlewareFalso(_usuario),
                contextoRevalidacion, (IOperacionesQueryContext)contextoRevalidacion, SinSesionPrivilegiada,
                loggerRevalidacion);

            seleccionRevalidacion.TenantIdSeleccionado.Should().BeNull();
            CabeceraDeBorradoDeCookie(httpContextRevalidacion).Should().NotBeNull();
        }

        loggerElse.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Warning);
        loggerRevalidacion.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Warning);

        var mensajeElse = loggerElse.Entradas[0].Mensaje;
        var mensajeRevalidacion = loggerRevalidacion.Entradas[0].Mensaje;

        mensajeElse.Should().NotBe(mensajeRevalidacion);
        mensajeElse.Should().Contain("no resolvió a ningún tenant");
        mensajeRevalidacion.Should().Contain("Tenant seleccionado");
    }

    [Fact]
    public async Task Sin_cookie_de_seleccion_el_middleware_no_consulta_nada()
    {
        // El caso de todo usuario que no es Operador Delegado: coste cero.
        // dbContext a null lo demuestra — si el middleware lo tocara, saltaría.
        var httpContext = new DefaultHttpContext { User = UsuarioAutenticado(_usuario) };
        var seleccion = new ClienteActivoSeleccionado(new HttpContextAccessorFalso(httpContext), _protector);

        var siguienteFueLlamado = false;
        var middleware = new RevalidacionClienteActivoMiddleware(_ =>
        {
            siguienteFueLlamado = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(
            httpContext, seleccion, new CurrentUserServiceParaMiddlewareFalso(_usuario),
            dbContext: null!, operacionesContext: null!, SinSesionPrivilegiada,
            NullLogger<RevalidacionClienteActivoMiddleware>.Instance);

        siguienteFueLlamado.Should().BeTrue();
    }

    // Estos tests son de plano 2 y de la vía heredada: ninguno abre una sesión
    // privilegiada, así que el resolutor de plano 3 devuelve null sin consultar.
    private static readonly ISesionPrivilegiadaActual SinSesionPrivilegiada = new SesionPrivilegiadaActualFalsa();

    private sealed class SesionPrivilegiadaActualFalsa : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private async Task RevocarDelegacionAsync()
    {
        await using var contexto = CrearContexto();
        var delegacion = await contexto.DelegacionesTenant.FirstAsync(d => d.Id == _delegacionId);
        delegacion.Desactivar();
        await contexto.SaveChangesAsync();
    }

    /// <summary>
    /// Token emitido por la propia clase de producción, no uno inventado: si
    /// el formato cambia, el test cambia con él.
    /// </summary>
    private (DefaultHttpContext, ClienteActivoSeleccionado) PrepararPeticionConTokenValido()
    {
        var token = ClienteActivoSeleccionado.Proteger(_protector, _usuario, _clienteDelegante, null);

        var httpContext = new DefaultHttpContext { User = UsuarioAutenticado(_usuario) };
        httpContext.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={token}";

        return (httpContext, new ClienteActivoSeleccionado(new HttpContextAccessorFalso(httpContext), _protector));
    }

    private async Task EjecutarMiddlewareAsync(
        DefaultHttpContext httpContext, IClienteActivoSeleccionado seleccion, ITenantsQueryContext contexto)
    {
        var middleware = new RevalidacionClienteActivoMiddleware(_ => Task.CompletedTask);
        // Estos tests ejercitan la vía heredada (el token no lleva operación),
        // así que el contexto de operaciones no llega a consultarse. Se pasa el
        // mismo DbContext, que implementa las dos interfaces.
        await middleware.InvokeAsync(
            httpContext, seleccion, new CurrentUserServiceParaMiddlewareFalso(_usuario),
            contexto, (IOperacionesQueryContext)contexto, SinSesionPrivilegiada,
            NullLogger<RevalidacionClienteActivoMiddleware>.Instance);
    }

    private static string? CabeceraDeBorradoDeCookie(DefaultHttpContext httpContext) =>
        httpContext.Response.Headers.SetCookie
            .FirstOrDefault(c => c is not null && c.Contains(ClienteActivoSeleccionado.NombreCookie));

    private static ClaimsPrincipal UsuarioAutenticado(Guid usuarioId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString())], "prueba"));

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _clienteDelegante };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>
    /// Captura los avisos emitidos por el middleware, para poder comprobar por
    /// texto que dos ramas que borran la misma cookie dejan trazas distintas
    /// (REC-136) — <c>NullLogger</c>, usado en el resto de este fichero, no
    /// permite esa comprobación porque descarta el mensaje.
    /// </summary>
    private sealed class LoggerCapturador<T> : ILogger<T>
    {
        public List<(LogLevel Nivel, string Mensaje)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, formatter(state, exception)));
    }

    private sealed class HttpContextAccessorFalso(HttpContext contexto) : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => contexto;
            set => throw new NotSupportedException();
        }
    }

    private sealed class CurrentUserServiceParaMiddlewareFalso(Guid usuarioId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);

        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("GestorCae");

        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);

        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
