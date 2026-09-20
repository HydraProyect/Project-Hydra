using System.Diagnostics;
using System.Runtime.CompilerServices;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Causa raíz de las <c>ArgumentOutOfRangeException</c> de
/// <c>NpgsqlDataReader.ProcessMessage</c> (<c>HandleUncommon</c>) que salían en
/// las consultas de bandeja del E2E de CI (run 35460271962: 22 fallos de acceso
/// a datos en 4 minutos, todos junto a un <c>POST /_blazor/disconnect</c>).
///
/// <para>
/// El circuito de Blazor se cierra mientras un componente todavía espera una
/// consulta, y el framework dispone el scope —y con él el
/// <c>CaeManagerDbContext</c> y su conexión— sin esperar a esa consulta. Con
/// PostgreSQL real, disponer el contexto con una consulta en vuelo hace que la
/// lectura y el cierre pisen el mismo protocolo: la consulta muere con
/// <c>ArgumentOutOfRangeException</c> u <c>ObjectDisposedException</c>, y la
/// conexión puede volver al pool desincronizada, de modo que la primera consulta
/// de OTRO circuito que la reciba falla o se cuelga (la navegación siguiente del
/// mismo test: el <c>GotoAsync("/clientes")</c> de 30 s).
/// </para>
///
/// <para>
/// Este test reproduce la secuencia del framework tal como es en
/// <c>CircuitHost.DisposeAsync</c>: <c>OnCircuitClosedAsync</c> de todos los
/// <see cref="CircuitHandler"/> del scope y, después, <c>DisposeAsync</c> del
/// scope. Los handlers se resuelven del contenedor —los mismos que registra
/// <c>Program.cs</c> para esta propiedad—, así que quitar el handler o vaciarlo
/// deja el test en rojo.
/// </para>
///
/// <para>
/// <b>La consulta en vuelo no se espera: se retiene.</b> La versión anterior
/// lanzaba <c>pg_sleep(0.4)</c> y sondeaba <c>pg_stat_activity</c> hasta verla —
/// pero la única ventana observable era la de la propia consulta (0,4 s), y el
/// observador abría una conexión nueva DENTRO de esa ventana. Medido el
/// 2026-09-21 con carga de CPU: la apertura tardaba 430–640 ms, más que la
/// consulta, y el test moría con «La consulta lenta no llegó a estar en vuelo en
/// el servidor» sin haber llegado a comprobar nada (consulta ya terminada, en
/// <c>RanToCompletion</c>; así salió la PR #766 de la cola de fusión, run
/// 35540032594). Ahora la consulta bloquea en el servidor sobre un
/// <c>pg_advisory_xact_lock</c> cuya llave tiene tomada el propio test desde
/// otra conexión: está en vuelo hasta que el test decide soltarla, el estado que
/// se sondea (una petición de bloqueo sin conceder en <c>pg_locks</c>) es
/// persistente y no puede perderse, y la observación no depende de lo que tarde
/// nada. Mismo patrón que
/// <c>SelectorTemaGuardadoTrasEscrituraConcurrenteTests</c>: el test controla el
/// momento en que se suelta lo retenido.
/// </para>
///
/// <para>
/// La propiedad se comprueba por ORDEN, no por tiempo: una sonda registrada en el
/// scope pregunta al SERVIDOR, en el instante en que el contenedor la dispone (con
/// una conexión propia abierta de antemano), si la consulta sigue retenida. Si el
/// cierre no espera a la consulta, la sonda se dispone con la consulta todavía
/// bloqueada y el rojo sale por esa aserción, no por un plazo. Se pregunta al
/// servidor y no a una marca del cliente porque una marca «llave soltada» puesta
/// antes del <c>pg_advisory_unlock</c> real dejaba una ventana en la que una
/// versión rota podía disponer el scope y pasar por buena (refutación de Codex,
/// 2026-09-21).
/// </para>
/// </summary>
public class CierreDeCircuitoConConsultaEnVueloTests : IAsyncLifetime
{
    private const int Vueltas = 8;

    // Llave del bloqueo consultivo que retiene la consulta (par de int4: objsubid = 2 en
    // pg_locks). Aleatoria: el bloqueo es de la base y esa base la comparten otros tests.
    private readonly int _llaveA = Random.Shared.Next(1, int.MaxValue);
    private readonly int _llaveB = Random.Shared.Next(1, int.MaxValue);
    private readonly string _nombreAplicacion = $"cierre-circuito-{Guid.NewGuid():N}";
    private readonly string _cadenaDeTrafico;
    private readonly string _cadenaDeControl;
    private NpgsqlConnection _control = default!;
    private NpgsqlConnection _conexionDeLaSonda = default!;
    private ServiceProvider _servicios = default!;

    public CierreDeCircuitoConConsultaEnVueloTests()
    {
        // Pool propio (el nombre de aplicación distingue la cadena) y pequeño: un
        // pool con pocas conexiones es donde una conexión desincronizada vuelve
        // a tocarle a alguien enseguida — como en producción bajo carga.
        var constructor = new NpgsqlConnectionStringBuilder(BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool())
        {
            Pooling = true,
            MaxPoolSize = 4,
            MinPoolSize = 0,
            Timeout = 10,
            CommandTimeout = 20,
            ApplicationName = _nombreAplicacion,
        };
        _cadenaDeTrafico = constructor.ConnectionString;

        constructor.Pooling = false;
        constructor.ApplicationName = _nombreAplicacion + "-control";
        _cadenaDeControl = constructor.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        // La conexión de control se abre AQUÍ, antes de cualquier consulta: abrirla dentro del
        // test —tras lanzar la consulta— era lo que perdía la carrera contra su ventana.
        _control = new NpgsqlConnection(_cadenaDeControl);
        await _control.OpenAsync();
        // Conexión de uso exclusivo de la sonda, también abierta de antemano: abrirla al
        // disponerse el scope volvería a poner una espera dentro de la ventana que se mide.
        _conexionDeLaSonda = new NpgsqlConnection(_cadenaDeControl);
        await _conexionDeLaSonda.OpenAsync();

        var servicios = new ServiceCollection();
        servicios.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        servicios.AddSingleton<ITenantActual>(new TenantActualAmbiental { TenantId = Guid.NewGuid() });
        servicios.AddScoped<PuertaAccesoDatos>();
        // Con la estrategia de reintentos de producción: es lo que hace que EF
        // bufferice el lector (BufferedDataReader, en la pila del E2E).
        servicios.AddDbContext<CaeManagerDbContext>(opciones => opciones.UseNpgsql(
            _cadenaDeTrafico, npgsql => npgsql.EnableRetryOnFailure(6, TimeSpan.FromSeconds(30), null)));
        // Igual que Program.cs.
        servicios.AddScoped<CircuitHandler, LiberacionDeAccesoADatosAlCerrarCircuito>();
        servicios.AddSingleton(new ObservadorDeRetencion(_conexionDeLaSonda, ConsultaRetenidaSql));
        servicios.AddScoped<SondaDeDisposicion>();

        _servicios = servicios.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        // Cerrar la conexión de control suelta el bloqueo si el test murió reteniéndolo.
        await _control.DisposeAsync();
        await _conexionDeLaSonda.DisposeAsync();

        // Acotado: si el escenario dejó una conexión rota, que la limpieza no cuelgue la suite.
        await Task.WhenAny(_servicios.DisposeAsync().AsTask(), Task.Delay(TimeSpan.FromSeconds(20)));
        NpgsqlConnection.ClearPool(new NpgsqlConnection(_cadenaDeTrafico));
    }

    [Fact]
    public async Task Cerrar_el_circuito_con_una_consulta_en_vuelo_la_deja_terminar_y_no_corrompe_el_pool()
    {
        for (var vuelta = 0; vuelta < Vueltas; vuelta++)
        {
            // La llave, en poder del test ANTES de lanzar la consulta: en cuanto la consulta
            // llegue al servidor se queda esperándola.
            await EjecutarEnControlAsync($"select pg_advisory_lock({_llaveA}, {_llaveB})");

            var scope = _servicios.CreateAsyncScope();
            var puerta = scope.ServiceProvider.GetRequiredService<PuertaAccesoDatos>();
            var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            // Se resuelve DESPUÉS del contexto: el contenedor dispone en orden inverso, así que
            // la sonda se dispone antes que el DbContext y anota si la consulta seguía retenida.
            var sonda = scope.ServiceProvider.GetRequiredService<SondaDeDisposicion>();

            // Como un componente que se inicializa: la consulta entra por la puerta.
            var consulta = Task.Run(() => puerta.EjecutarAsync(() => contexto.Database
                .SqlQueryRaw<int>($"select 1 as \"Value\" from pg_advisory_xact_lock({_llaveA}, {_llaveB})")
                .ToListAsync()));

            await EsperarConsultaRetenidaEnElServidorAsync(consulta, vuelta);

            // El cierre arranca con la consulta retenida —en vuelo— y sin poder terminar. Va en
            // su propia tarea: si una versión rota dispusiera el contexto bloqueándose, no debe
            // bloquear también al test.
            var cierre = Task.Run(() => CerrarCircuitoComoElFrameworkAsync(scope));

            // Barrera: o el cierre ya cerró la puerta (y queda esperando a la consulta), o ya
            // dispuso el scope (una versión que no espera). Ambos estados son definitivos.
            await EsperarAsync(() => puerta.Cerrada || sonda.Eliminada,
                "que el cierre del circuito cerrara la puerta o dispusiera el scope", vuelta);

            await EjecutarEnControlAsync($"select pg_advisory_unlock({_llaveA}, {_llaveB})");

            // Sin lanzar aquí: una versión rota puede colgar o romper cierre y consulta, y el rojo
            // tiene que salir por las aserciones de abajo, no por un tiempo agotado.
            await Task.WhenAny(Task.WhenAll(cierre, consulta), Task.Delay(TimeSpan.FromSeconds(30)));

            sonda.ErrorAlObservar.Should().BeNull("la sonda tiene que haber podido preguntar al servidor");
            sonda.RetenidaAlDisponerse.Should().BeFalse(
                "el scope —y con él el DbContext y su conexión— se dispuso con la consulta todavía retenida en " +
                $"el servidor: el cierre del circuito NO esperó a la consulta en vuelo (vuelta {vuelta})");
            sonda.Eliminada.Should().BeTrue(
                $"con la consulta ya liberada el cierre tiene que haber terminado disponiendo el scope (vuelta {vuelta})");
            cierre.IsCompletedSuccessfully.Should().BeTrue(
                $"el cierre termina cuando la consulta termina (vuelta {vuelta}); estado: {cierre.Status}, " +
                cierre.Exception?.GetBaseException().Message);
            consulta.IsCompletedSuccessfully.Should().BeTrue(
                $"la consulta ya estaba en vuelo al cerrar el circuito y tenía que terminar (vuelta {vuelta}); " +
                $"estado: {consulta.Status}, {consulta.Exception?.GetBaseException().Message}");
            consulta.Result.Should().BeEquivalentTo(new[] { 1 },
                $"la consulta ya estaba en vuelo al cerrar el circuito y tenía que terminar (vuelta {vuelta})");

            // El pool no ha recibido una conexión desincronizada: el circuito siguiente
            // (aquí, tres a la vez) toma conexiones del mismo pool y consulta sin fallar.
            var siguientes = Enumerable.Range(0, 3).Select(async _ =>
            {
                await using var siguiente = _servicios.CreateAsyncScope();
                var otroContexto = siguiente.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
                return await otroContexto.Database.SqlQueryRaw<int>("select 1 as \"Value\"").ToListAsync();
            }).ToArray();

            foreach (var siguiente in siguientes)
                (await ConTope(siguiente, "la consulta del circuito siguiente", vuelta)).Should().Equal(1);
        }
    }

    /// <summary>
    /// Lo que hace el framework al cerrar un circuito (<c>CircuitHost.DisposeAsync</c>):
    /// primero los handlers, después el scope.
    /// </summary>
    private static async Task CerrarCircuitoComoElFrameworkAsync(AsyncServiceScope scope)
    {
        var circuito = (Circuit)RuntimeHelpers.GetUninitializedObject(typeof(Circuit));
        foreach (var handler in scope.ServiceProvider.GetServices<CircuitHandler>())
            await handler.OnCircuitClosedAsync(circuito, CancellationToken.None);

        await scope.DisposeAsync();
    }

    /// <summary>
    /// Espera a que la consulta esté EN VUELO en el servidor: una petición de
    /// <c>pg_advisory_xact_lock</c> sin conceder en <c>pg_locks</c>, que solo puede
    /// existir con la consulta ya ejecutándose y bloqueada. Es un estado persistente
    /// —la consulta no avanza hasta que el test suelta la llave—, así que sondearlo no
    /// puede llegar tarde; el plazo solo acota un fallo real (la consulta nunca llega).
    /// Si la consulta termina o falla antes de quedar retenida, el fallo es ese, no un tiempo.
    /// </summary>
    private string ConsultaRetenidaSql =>
        "select count(*) from pg_locks where locktype = 'advisory' and not granted " +
        $"and classid::bigint = {_llaveA} and objid::bigint = {_llaveB} and objsubid = 2";

    private async Task EsperarConsultaRetenidaEnElServidorAsync(Task consulta, int vuelta)
    {
        await EsperarAsync(() =>
        {
            if (consulta.IsCompleted)
                throw new InvalidOperationException(
                    $"La consulta terminó sin haber quedado retenida en el servidor (vuelta {vuelta}); estado: " +
                    $"{consulta.Status}, {consulta.Exception?.GetBaseException().Message}");

            using var comando = _control.CreateCommand();
            comando.CommandText = ConsultaRetenidaSql;
            return (long)comando.ExecuteScalar()! > 0;
        }, "que la consulta quedara retenida en el servidor", vuelta);
    }

    private async Task EjecutarEnControlAsync(string sql)
    {
        await using var comando = _control.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task EsperarAsync(Func<bool> condicion, string que, int vuelta)
    {
        var cronometro = Stopwatch.StartNew();
        while (cronometro.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (condicion()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException($"No se llegó a observar {que} (vuelta {vuelta}).");
    }

    /// <summary>
    /// Un cuelgue no debe colgar la suite: es justo uno de los síntomas medidos, así
    /// que se convierte en un fallo con nombre.
    /// </summary>
    private static async Task<T> ConTope<T>(Task<T> tarea, string que, int vuelta)
    {
        var terminada = await Task.WhenAny(tarea, Task.Delay(TimeSpan.FromSeconds(30)));
        if (terminada != tarea)
            throw new TimeoutException($"{que} se quedó colgada más de 30 s (vuelta {vuelta}).");

        return await tarea;
    }

    /// <summary>
    /// Pregunta al servidor si la consulta sigue esperando la llave. Una sola conexión, la
    /// de la sonda: los scopes de las vueltas se disponen de uno en uno.
    /// </summary>
    private sealed class ObservadorDeRetencion(NpgsqlConnection conexion, string sql)
    {
        public async Task<bool> HayConsultaRetenidaAsync()
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = sql;
            return (long)(await comando.ExecuteScalarAsync())! > 0;
        }
    }

    /// <summary>
    /// Servicio del scope que anota, en el instante en que el scope se dispone, si la
    /// consulta seguía retenida en el servidor. Un test que solo mirase el resultado de la
    /// consulta no vería que el scope se dispuso con ella en vuelo cuando la consulta acaba
    /// bien de todos modos.
    /// </summary>
    private sealed class SondaDeDisposicion(ObservadorDeRetencion observador) : IAsyncDisposable
    {
        private volatile bool _eliminada;
        private volatile bool _retenidaAlDisponerse;
        private volatile string? _errorAlObservar;

        public bool Eliminada => _eliminada;

        public bool RetenidaAlDisponerse => _retenidaAlDisponerse;

        public string? ErrorAlObservar => _errorAlObservar;

        public async ValueTask DisposeAsync()
        {
            try
            {
                _retenidaAlDisponerse = await observador.HayConsultaRetenidaAsync();
            }
            catch (Exception ex)
            {
                _errorAlObservar = ex.ToString();
            }
            finally
            {
                _eliminada = true;
            }
        }
    }
}
