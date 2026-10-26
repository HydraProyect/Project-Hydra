using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Plataforma;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using EstadoBootstrap = CaeManager.Domain.Plataforma.EstadoBootstrapPlataforma;

namespace CaeManager.Web.Tests;

/// <summary>
/// Z2-1 (revisión extensa 2026-10-23): un circuito de Blazor Server abierto con una Sesión Privilegiada seguía leyendo el
/// Tenant objetivo después de que la sesión caducara, se revocara su concesión o se cerrara desde otra pestaña. La memo de
/// <see cref="SesionPrivilegiadaActual"/> dura lo que el circuito, y la revalidación periódica le preguntaba a esa memo.
/// <para>
/// Se prueban las clases de producción —el handler del circuito, la selección con su token real y el resolutor— con un
/// reloj manual que dispara el temporizador del circuito, y un contexto de plataforma en memoria cuyas filas el test cambia
/// igual que lo haría otra pestaña o un Actor de Plataforma TALVEG. Cada caso arranca con la memo cebada, como la deja
/// cualquier lectura del circuito, porque el defecto era justamente que esa memo sobreviviera.
/// </para>
/// </summary>
public sealed class SesionPrivilegiadaVigenciaEnCircuitoTests : IAsyncLifetime
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(RevalidacionCircuitoActivoHandler.IntervaloPorDefectoSegundos);
    private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(5);

    private readonly RelojManual _reloj = new();
    private readonly IDataProtectionProvider _protector = new EphemeralDataProtectionProvider();
    private readonly Guid _usuarioPlataforma = Guid.NewGuid();
    private readonly Guid _tenantVisitado = Guid.NewGuid();
    private readonly PlataformaQueryContextFalso _plataforma = new();
    private readonly PuertaAccesoDatos _puerta = new();
    private readonly Circuit _circuito = (Circuit)RuntimeHelpers.GetUninitializedObject(typeof(Circuit));

    private ConcesionPrivilegio _concesion = null!;
    private SesionPrivilegiada _sesion = null!;
    private ClienteActivoSeleccionado _seleccion = null!;
    private SesionPrivilegiadaActual _resolutor = null!;
    private RevalidacionCircuitoActivoHandler? _handler;

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Un_fallo_al_revalidar_se_reintenta_una_vez_y_solo_se_conserva_el_contexto_si_se_comprueba(
        int fallos, bool cancelacionDelProveedor)
    {
        await AbrirCircuitoConSesionAsync(SesionPrivilegiada.VentanaMaxima);
        var consultasAntes = _plataforma.ConsultasDeSesion;
        _plataforma.FallosPendientes = fallos;
        _plataforma.CancelacionDelProveedor = cancelacionDelProveedor;

        await RevalidarUnCicloAsync(CancellationToken.None);

        _plataforma.ConsultasDeSesion.Should().Be(consultasAntes + 2, "hay exactamente un reintento");
        _seleccion.TenantIdSeleccionado.Should().Be(fallos == 1 ? _tenantVisitado : null);
    }

    [Fact]
    public async Task Un_rechazo_real_al_revalidar_no_se_reintenta()
    {
        await AbrirCircuitoConSesionAsync(SesionPrivilegiada.VentanaMaxima);
        var consultasAntes = _plataforma.ConsultasDeSesion;
        _concesion.Revocar(Ahora());

        await RevalidarUnCicloAsync(CancellationToken.None);

        _plataforma.ConsultasDeSesion.Should().Be(consultasAntes + 1, "el reintento es solo para un error, no para un «no»");
        _seleccion.TenantIdSeleccionado.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Con_el_circuito_cerrandose_un_fallo_termina_el_ciclo_sin_reintentar_ni_invalidar(bool puertaCerrada)
    {
        await AbrirCircuitoConSesionAsync(SesionPrivilegiada.VentanaMaxima);
        var consultasAntes = _plataforma.ConsultasDeSesion;
        _plataforma.FallosPendientes = 2;
        using var cts = new CancellationTokenSource();
        if (puertaCerrada)
            (await _puerta.CerrarAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
        else
            await cts.CancelAsync();

        var ciclo = () => RevalidarUnCicloAsync(cts.Token);

        await ciclo.Should().ThrowAsync<OperationCanceledException>("el bucle del circuito reconoce así su cierre");
        _plataforma.ConsultasDeSesion.Should().Be(consultasAntes, "un circuito que se cierra no vuelve a la base");
        _seleccion.TenantIdSeleccionado.Should().Be(_tenantVisitado,
            "el cierre del circuito no es un fallo de la comprobación y no dispara la retirada de la selección");
    }

    /// <summary>
    /// Un ciclo completo de revalidación, sin depender de cuándo termina el tick en segundo plano (el reloj manual no
    /// avanza, así que el temporizador no dispara a la vez).
    /// </summary>
    private Task RevalidarUnCicloAsync(CancellationToken cancellationToken)
    {
        var revalidar = typeof(RevalidacionCircuitoActivoHandler).GetMethod("RevalidarAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (Task)revalidar.Invoke(_handler, [cancellationToken])!;
    }

    [Fact]
    public async Task El_tick_del_circuito_corta_la_lectura_cuando_la_sesion_caduca()
    {
        // Ventana de 90 s: el tick de los 60 s la encuentra viva y el de los 120 s, caducada.
        await AbrirCircuitoConSesionAsync(TimeSpan.FromSeconds(90));

        await AvanzarUnTickYEsperarSuConsultaAsync();
        _seleccion.TenantIdSeleccionado.Should().Be(_tenantVisitado, "a los 60 s la sesión sigue dentro de su ventana");

        _reloj.Avanzar(Tick);

        (await EsperarHastaAsync(() => _seleccion.TenantIdSeleccionado is null)).Should().BeTrue(
            "a los 120 s la ventana de 90 s ya venció, y el tick siguiente tiene que retirar la selección");
        (await _resolutor.ObtenerAsync()).Should().BeNull("tras el corte, el alcance de datos tampoco ve ya la sesión");
    }

    [Fact]
    public async Task El_tick_del_circuito_corta_la_lectura_cuando_se_revoca_la_concesion()
    {
        await AbrirCircuitoConSesionAsync(SesionPrivilegiada.VentanaMaxima);

        _concesion.Revocar(Ahora());
        _reloj.Avanzar(Tick);

        (await EsperarHastaAsync(() => _seleccion.TenantIdSeleccionado is null)).Should().BeTrue(
            "la concesión revocada tiene que cortar el circuito en el tick siguiente, no cuando venza la ventana");
        (await _resolutor.ObtenerAsync()).Should().BeNull();
    }

    [Fact]
    public async Task El_tick_del_circuito_corta_la_lectura_cuando_otra_pestana_cierra_la_sesion()
    {
        await AbrirCircuitoConSesionAsync(SesionPrivilegiada.VentanaMaxima);

        // Otra pestaña cierra la sesión: cambia la fila, no la memo de este circuito.
        _sesion.Cerrar(Ahora());
        _reloj.Avanzar(Tick);

        (await EsperarHastaAsync(() => _seleccion.TenantIdSeleccionado is null)).Should().BeTrue(
            "una sesión cerrada desde otra pestaña tiene que cortar también este circuito en el tick siguiente");
        (await _resolutor.ObtenerAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Sin_cambios_la_sesion_sobrevive_a_los_ticks_y_cada_tick_pregunta_a_la_base()
    {
        // Control: si el handler cortara sin condición, los tres casos de arriba pasarían por la razón equivocada.
        await AbrirCircuitoConSesionAsync(SesionPrivilegiada.VentanaMaxima);

        await AvanzarUnTickYEsperarSuConsultaAsync();
        await AvanzarUnTickYEsperarSuConsultaAsync();

        _seleccion.TenantIdSeleccionado.Should().Be(_tenantVisitado);
        (await _resolutor.ObtenerAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task El_tick_pasa_por_la_puerta_de_acceso_a_datos_y_con_ella_cerrada_no_toca_la_base()
    {
        // El tick comparte el DbContext scoped del circuito con el renderizado: tiene que serializarse con él.
        await AbrirCircuitoConSesionAsync(SesionPrivilegiada.VentanaMaxima);
        var consultasAntes = _plataforma.ConsultasDeSesion;

        (await _puerta.CerrarAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
        _reloj.Avanzar(Tick);
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        _plataforma.ConsultasDeSesion.Should().Be(consultasAntes,
            "con la puerta cerrada el circuito se está cerrando y su DbContext se va a disponer");
    }

    [Fact]
    public async Task ObtenerAsync_deja_de_devolver_la_memo_en_cuanto_vence_la_ventana_sin_esperar_al_tick()
    {
        await AbrirCircuitoConSesionAsync(TimeSpan.FromSeconds(30));

        _reloj.Avanzar(TimeSpan.FromSeconds(31)); // antes del primer tick: nadie ha revalidado todavía.

        (await _resolutor.ObtenerAsync()).Should().BeNull(
            "la caducidad se decide con el reloj sobre la memo; no necesita que la base ni el circuito digan nada");
    }

    [Fact]
    public async Task ObtenerAsync_deja_de_devolver_la_memo_cuando_la_concesion_vence_antes_que_la_sesion()
    {
        // Concesión que acaba a los 20 s; sesión de 4 h abierta bajo ella.
        await AbrirCircuitoConSesionAsync(SesionPrivilegiada.VentanaMaxima, vigenciaConcesion: TimeSpan.FromSeconds(20));

        _reloj.Avanzar(TimeSpan.FromSeconds(21));

        (await _resolutor.ObtenerAsync()).Should().BeNull("el primer vencimiento de los dos manda");
    }

    [Fact]
    public async Task ObtenerAsync_no_reutiliza_la_memo_si_la_seleccion_ya_no_nombra_esa_sesion()
    {
        await AbrirCircuitoConSesionAsync(SesionPrivilegiada.VentanaMaxima);
        var consultasAntes = _plataforma.ConsultasDeSesion;

        _seleccion.Invalidar();

        (await _resolutor.ObtenerAsync()).Should().BeNull(
            "sin sesión en la selección no hay privilegio, aunque la memo dijera otra cosa");
        _plataforma.ConsultasDeSesion.Should().Be(consultasAntes, "sin sesión seleccionada no hace falta preguntar a la base");
    }

    private async Task AbrirCircuitoConSesionAsync(TimeSpan ventana, TimeSpan? vigenciaConcesion = null)
    {
        var ahora = Ahora();
        _concesion = ConcesionPrivilegio.SoporteLecturaGlobal(
            _usuarioPlataforma, ahora.AddHours(-1), ahora + (vigenciaConcesion ?? TimeSpan.FromDays(30)));
        _sesion = SesionPrivilegiada.Abrir(_concesion, _tenantVisitado, "Incidencia de soporte en pruebas", ahora, ventana);
        _plataforma.Concesiones.Add(_concesion);
        _plataforma.Sesiones.Add(_sesion);

        var token = ClienteActivoSeleccionado.Proteger(
            _protector, _usuarioPlataforma, _tenantVisitado, asignacionOperacionId: null, sesionPrivilegiadaId: _sesion.Id);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _usuarioPlataforma.ToString())], "prueba"))
        };
        httpContext.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={token}";
        _seleccion = new ClienteActivoSeleccionado(new HttpContextAccessor { HttpContext = httpContext }, _protector);

        var usuario = new UsuarioDePlataformaFalso(_usuarioPlataforma);
        _resolutor = new SesionPrivilegiadaActual(_plataforma, _seleccion, usuario, _reloj);

        _handler = new RevalidacionCircuitoActivoHandler(
            _seleccion,
            usuario,
            SinEncargoDeAdministracion.Instancia,
            new PrincipalSinEncargo(),
            null!, // vías de Operación y heredada: el camino de plano 3 no las consulta.
            null!,
            _resolutor,
            _puerta,
            new ConfigurationBuilder().Build(),
            NullLogger<RevalidacionCircuitoActivoHandler>.Instance,
            _reloj);

        await _handler.OnCircuitOpenedAsync(_circuito, CancellationToken.None);

        // La memo cebada, como la deja cualquier lectura del circuito.
        (await _resolutor.ObtenerAsync()).Should().NotBeNull("precondición: el circuito arranca con la sesión viva");
        _seleccion.TenantIdSeleccionado.Should().Be(_tenantVisitado);
    }

    /// <summary>
    /// Avanza un periodo y espera a que el tick llegue a la base. Dos llamadas seguidas garantizan además que la primera
    /// revalidación terminó entera —decisión incluida— antes de mirar, porque el bucle no pide el tick siguiente hasta
    /// acabar la anterior.
    /// </summary>
    private async Task AvanzarUnTickYEsperarSuConsultaAsync()
    {
        var antes = _plataforma.ConsultasDeSesion;
        _reloj.Avanzar(Tick);
        (await EsperarHastaAsync(() => _plataforma.ConsultasDeSesion > antes)).Should().BeTrue(
            "cada tick del circuito tiene que revalidar contra la base, no contra la memo");
    }

    private DateTime Ahora() => _reloj.GetUtcNow().UtcDateTime;

    private static async Task<bool> EsperarHastaAsync(Func<bool> condicion)
    {
        var cronometro = Stopwatch.StartNew();
        while (cronometro.Elapsed < EsperaMaxima)
        {
            if (condicion()) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        return condicion();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_handler is null) return;

        await _handler.OnCircuitClosedAsync(_circuito, CancellationToken.None);
        await _handler.DisposeAsync();
    }

    private sealed class UsuarioDePlataformaFalso(Guid usuarioId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(null);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(null);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class PrincipalSinEncargo : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    /// <summary>
    /// Las filas viven en listas que el test modifica; cada lectura de <see cref="SesionesPrivilegiadas"/> cuenta como una
    /// consulta, que es la señal de que alguien preguntó a la base en vez de a la memo.
    /// </summary>
    private sealed class PlataformaQueryContextFalso : IPlataformaQueryContext
    {
        private int _consultasDeSesion;

        public List<ConcesionPrivilegio> Concesiones { get; } = [];
        public List<SesionPrivilegiada> Sesiones { get; } = [];

        public int ConsultasDeSesion => Volatile.Read(ref _consultasDeSesion);
        public int FallosPendientes { get; set; }
        public bool CancelacionDelProveedor { get; set; }

        public IQueryable<EstadoBootstrapPlataforma> EstadoBootstrapPlataforma =>
            new ConsultaAsincrona<EstadoBootstrap>(Enumerable.Empty<EstadoBootstrap>().AsQueryable());

        public IQueryable<ConcesionPrivilegio> ConcesionesPrivilegio =>
            new ConsultaAsincrona<ConcesionPrivilegio>(Concesiones.ToList().AsQueryable());

        public IQueryable<SesionPrivilegiada> SesionesPrivilegiadas
        {
            get
            {
                Interlocked.Increment(ref _consultasDeSesion);
                if (FallosPendientes > 0)
                {
                    FallosPendientes--;
                    if (CancelacionDelProveedor) throw new OperationCanceledException("Tiempo de espera del proveedor");
                    throw new InvalidOperationException("Fallo de consulta");
                }
                return new ConsultaAsincrona<SesionPrivilegiada>(Sesiones.ToList().AsQueryable());
            }
        }

        public IQueryable<TenantAlcanzadoPorConcesion> TenantsAlcanzadosPorConcesion =>
            new ConsultaAsincrona<TenantAlcanzadoPorConcesion>(Enumerable.Empty<TenantAlcanzadoPorConcesion>().AsQueryable());
    }

    /// <summary>LINQ-to-Objects con los operadores asíncronos de EF Core (<c>FirstOrDefaultAsync</c>, <c>AnyAsync</c>).</summary>
    private sealed class ConsultaAsincrona<T>(IQueryable<T> interna) : IOrderedQueryable<T>, IAsyncEnumerable<T>
    {
        public Type ElementType => interna.ElementType;
        public Expression Expression => interna.Expression;
        public IQueryProvider Provider { get; } = new ProveedorAsincrono(interna.Provider);

        public IEnumerator<T> GetEnumerator() => interna.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => interna.GetEnumerator();

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new EnumeradorAsincrono(interna.GetEnumerator());

        private sealed class EnumeradorAsincrono(IEnumerator<T> interno) : IAsyncEnumerator<T>
        {
            public T Current => interno.Current;
            public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(interno.MoveNext());

            public ValueTask DisposeAsync()
            {
                interno.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ProveedorAsincrono(IQueryProvider interno) : IAsyncQueryProvider
    {
        public IQueryable CreateQuery(Expression expression) => interno.CreateQuery(expression);

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
            new ConsultaAsincrona<TElement>(interno.CreateQuery<TElement>(expression));

        public object? Execute(Expression expression) => interno.Execute(expression);

        public TResult Execute<TResult>(Expression expression) => interno.Execute<TResult>(expression);

        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            var tipoResultado = typeof(TResult).GetGenericArguments().FirstOrDefault() ?? typeof(TResult);
            var resultado = interno.Execute(expression);
            var desdeResultado = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(tipoResultado);
            return (TResult)desdeResultado.Invoke(null, [resultado])!;
        }
    }
}
