using System.Diagnostics;
using System.Runtime.CompilerServices;
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
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Hallazgo del Módulo 9 (auditoría 2026-08-30): <c>RevalidacionClienteActivoMiddleware</c>
/// (ver <see cref="RevalidacionClienteActivoTests"/>) solo corre en peticiones
/// HTTP; un circuito de Blazor ya abierto que interactúa puramente por
/// SignalR no genera ninguna, así que una delegación revocada mientras el
/// workspace seguía abierto no se notaba hasta la siguiente navegación. Este
/// handler repite la misma comprobación desde un temporizador de fondo del
/// propio circuito.
///
/// Se prueba contra Postgres real y las clases de producción, no contra
/// dobles del contexto de datos — el <c>Circuit</c> que exige la firma de
/// <c>CircuitHandler</c> sí es un doble (su único constructor real exige un
/// <c>CircuitHost</c> interno de Blazor, y el handler nunca lo lee).
/// </summary>
public class RevalidacionCircuitoActivoHandlerTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly IDataProtectionProvider _protector = new EphemeralDataProtectionProvider();
    private readonly Guid _clienteDelegante = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();
    private Guid _delegacionId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var delegacion = new DelegacionTenant(Guid.NewGuid(), _clienteDelegante);
        contexto.DelegacionesTenant.Add(delegacion);
        contexto.AsignacionesOperadorDelegado.Add(
            new AsignacionOperadorDelegado(delegacion.Id, _usuario, "GestorCae"));

        await contexto.SaveChangesAsync();
        _delegacionId = delegacion.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Revocada_la_delegacion_a_mitad_de_circuito_el_temporizador_invalida_la_seleccion()
    {
        await using var contexto = CrearContexto();
        var seleccion = PrepararSeleccionConTokenValido();
        seleccion.TenantIdSeleccionado.Should().Be(_clienteDelegante); // precondición: arranca autorizado.

        var handler = CrearHandler(seleccion, contexto, intervaloSegundos: 1);
        var circuito = CircuitFalso();
        await handler.OnCircuitOpenedAsync(circuito, CancellationToken.None);

        try
        {
            await RevocarDelegacionAsync();

            (await EsperarHastaAsync(() => seleccion.TenantIdSeleccionado is null, TimeSpan.FromSeconds(8)))
                .Should().BeTrue("el temporizador (1 s) debería haber invalidado la selección tras revocar la delegación");
        }
        finally
        {
            await handler.OnCircuitClosedAsync(circuito, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Sin_revocar_nada_la_seleccion_sobrevive_varios_ciclos_del_temporizador()
    {
        // Control negativo: si el handler invalidara sin condición (un bug
        // trivial que "siempre corta") este test lo detectaría en vez de que
        // solo pareciera funcionar por casualidad en el test positivo.
        await using var contexto = CrearContexto();
        var seleccion = PrepararSeleccionConTokenValido();

        var handler = CrearHandler(seleccion, contexto, intervaloSegundos: 1);
        var circuito = CircuitFalso();
        await handler.OnCircuitOpenedAsync(circuito, CancellationToken.None);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3)); // dos o tres ciclos del temporizador de 1 s.
            seleccion.TenantIdSeleccionado.Should().Be(_clienteDelegante);
        }
        finally
        {
            await handler.OnCircuitClosedAsync(circuito, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Cerrado_el_circuito_el_temporizador_deja_de_revalidar()
    {
        // Prueba de limpieza: OnCircuitClosedAsync no solo debe devolver, debe
        // parar de verdad el bucle — si no, revocar después de "cerrar" el
        // circuito igual invalidaría la selección, y este test lo vería.
        await using var contexto = CrearContexto();
        var seleccion = PrepararSeleccionConTokenValido();

        var handler = CrearHandler(seleccion, contexto, intervaloSegundos: 1);
        var circuito = CircuitFalso();
        await handler.OnCircuitOpenedAsync(circuito, CancellationToken.None);
        await handler.OnCircuitClosedAsync(circuito, CancellationToken.None);

        await RevocarDelegacionAsync();
        await Task.Delay(TimeSpan.FromSeconds(3));

        seleccion.TenantIdSeleccionado.Should().Be(
            _clienteDelegante, "el temporizador ya debería estar detenido y no debe seguir leyendo tras cerrarse el circuito");
    }

    [Fact]
    public async Task Sembrar_en_OnCircuitOpenedAsync_memoiza_la_seleccion_antes_de_que_el_HttpContext_deje_de_estar_disponible()
    {
        // Hallazgo de Módulo 1 (CI, 1 de 3 en SeleccionSobreviveAlCircuitoTests):
        // el HttpContext de la negociación del circuito no sigue ambiental de
        // forma fiable para cuando el primer componente lee la selección. La
        // ventana se cierra por TIEMPO (la negociación termina), no porque
        // alguien la lea — de ahí que el doble de abajo separe "cerrar la
        // ventana" de "leerla", en vez de un accesor de un solo uso que
        // confundiría las dos cosas.
        var token = ClienteActivoSeleccionado.Proteger(_protector, _usuario, _clienteDelegante, null);
        var httpContext = new DefaultHttpContext { User = UsuarioAutenticado(_usuario) };
        httpContext.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={token}";

        var accesor = new HttpContextAccessorConVentana(httpContext);
        var seleccion = new ClienteActivoSeleccionado(accesor, _protector);

        await using var contexto = CrearContexto();
        var handler = CrearHandler(seleccion, contexto, intervaloSegundos: 60);
        var circuito = CircuitFalso();

        await handler.OnCircuitOpenedAsync(circuito, CancellationToken.None);
        accesor.CerrarVentana(); // la negociación del circuito termina — a partir de aquí, HttpContext ya no existe.

        try
        {
            seleccion.TenantIdSeleccionado.Should().Be(
                _clienteDelegante, "OnCircuitOpenedAsync ya memoizó el valor correcto mientras el HttpContext seguía disponible");
        }
        finally
        {
            await handler.OnCircuitClosedAsync(circuito, CancellationToken.None);
        }
    }

    [Fact]
    public void Sin_sembrar_una_lectura_que_llega_tarde_memoiza_nulo_aunque_la_seleccion_fuera_valida()
    {
        // El mecanismo del hallazgo, aislado de OnCircuitOpenedAsync: si nadie
        // fuerza la lectura mientras el HttpContext de la negociación sigue
        // disponible, la primera lectura real —la de un componente durante el
        // render— puede llegar después de que la ventana ya se cerró. No es
        // que la selección no existiera: es que se preguntó tarde.
        var token = ClienteActivoSeleccionado.Proteger(_protector, _usuario, _clienteDelegante, null);
        var httpContext = new DefaultHttpContext { User = UsuarioAutenticado(_usuario) };
        httpContext.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={token}";

        var accesor = new HttpContextAccessorConVentana(httpContext);
        accesor.CerrarVentana(); // nadie leyó nada todavía, y la ventana ya se cerró — la carrera perdida.
        var seleccion = new ClienteActivoSeleccionado(accesor, _protector);

        seleccion.TenantIdSeleccionado.Should().BeNull(
            "sin OnCircuitOpenedAsync forzando la lectura antes, esta llega con el HttpContext ya perdido y memoiza nulo");
    }

    private RevalidacionCircuitoActivoHandler CrearHandler(
        IClienteActivoSeleccionado seleccion, CaeManagerDbContext contexto, int intervaloSegundos)
    {
        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Circuit:RevalidacionIntervaloSegundos"] = intervaloSegundos.ToString()
            })
            .Build();

        return new RevalidacionCircuitoActivoHandler(
            seleccion,
            new CurrentUserServiceParaHandlerFalso(_usuario),
            contexto,
            contexto,
            SinSesionPrivilegiada,
            configuracion,
            NullLogger<RevalidacionCircuitoActivoHandler>.Instance);
    }

    /// <summary>
    /// El único constructor real de <c>Circuit</c> exige un <c>CircuitHost</c>
    /// interno de Blazor Server que no tiene sentido montar en un test de
    /// integración — el handler nunca lee el parámetro, solo lo recibe porque
    /// así lo exige la firma de <c>CircuitHandler</c>.
    /// </summary>
    private static Circuit CircuitFalso() => (Circuit)RuntimeHelpers.GetUninitializedObject(typeof(Circuit));

    private static async Task<bool> EsperarHastaAsync(Func<bool> condicion, TimeSpan timeout)
    {
        var cronometro = Stopwatch.StartNew();
        while (cronometro.Elapsed < timeout)
        {
            if (condicion()) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(150));
        }

        return condicion();
    }

    private static readonly ISesionPrivilegiadaActual SinSesionPrivilegiada = new SesionPrivilegiadaActualFalsa();

    private sealed class SesionPrivilegiadaActualFalsa : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class CurrentUserServiceParaHandlerFalso(Guid usuarioId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("GestorCae");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private async Task RevocarDelegacionAsync()
    {
        await using var contexto = CrearContexto();
        var delegacion = await contexto.DelegacionesTenant.FirstAsync(d => d.Id == _delegacionId);
        delegacion.Desactivar();
        await contexto.SaveChangesAsync();
    }

    /// <summary>Token emitido por la propia clase de producción, mismo criterio que RevalidacionClienteActivoTests.</summary>
    private ClienteActivoSeleccionado PrepararSeleccionConTokenValido()
    {
        var token = ClienteActivoSeleccionado.Proteger(_protector, _usuario, _clienteDelegante, null);

        var httpContext = new DefaultHttpContext { User = UsuarioAutenticado(_usuario) };
        httpContext.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={token}";

        return new ClienteActivoSeleccionado(new HttpContextAccessorFalso(httpContext), _protector);
    }

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

    private sealed class HttpContextAccessorFalso(HttpContext contexto) : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => contexto;
            set => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Simula la ventana real de <c>HttpContext</c> durante la negociación de
    /// un circuito de Blazor Server: disponible hasta que <see cref="CerrarVentana"/>
    /// se llama, nulo después — deliberadamente independiente de cuántas veces
    /// se haya leído antes. La ventana real se cierra porque la negociación
    /// termina (una cuestión de tiempo/scheduling), no porque algo la haya
    /// leído; un doble que se "gastara" con la primera lectura mediría otra
    /// cosa y dejaría pasar como éxito un caso que en producción sería un
    /// fallo (visto una vez: ver el commit que introdujo este comentario).
    /// </summary>
    private sealed class HttpContextAccessorConVentana(HttpContext contexto) : IHttpContextAccessor
    {
        private bool _ventanaCerrada;

        public void CerrarVentana() => _ventanaCerrada = true;

        public HttpContext? HttpContext
        {
            get => _ventanaCerrada ? null : contexto;
            set => throw new NotSupportedException();
        }
    }
}
