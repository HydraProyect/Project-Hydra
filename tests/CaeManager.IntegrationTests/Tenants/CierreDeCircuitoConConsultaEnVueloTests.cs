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
/// </summary>
public class CierreDeCircuitoConConsultaEnVueloTests : IAsyncLifetime
{
    private const int Vueltas = 8;

    private readonly string _nombreAplicacion = $"cierre-circuito-{Guid.NewGuid():N}";
    private readonly string _cadenaDeTrafico;
    private readonly string _cadenaDeObservacion;
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
        constructor.ApplicationName = _nombreAplicacion + "-observador";
        _cadenaDeObservacion = constructor.ConnectionString;
    }

    public Task InitializeAsync()
    {
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

        _servicios = servicios.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // Acotado: si el escenario dejó una conexión rota, que la limpieza no cuelgue la suite.
        await Task.WhenAny(_servicios.DisposeAsync().AsTask(), Task.Delay(TimeSpan.FromSeconds(20)));
        NpgsqlConnection.ClearPool(new NpgsqlConnection(_cadenaDeTrafico));
    }

    [Fact]
    public async Task Cerrar_el_circuito_con_una_consulta_en_vuelo_la_deja_terminar_y_no_corrompe_el_pool()
    {
        for (var vuelta = 0; vuelta < Vueltas; vuelta++)
        {
            var scope = _servicios.CreateAsyncScope();
            var puerta = scope.ServiceProvider.GetRequiredService<PuertaAccesoDatos>();
            var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

            // Como un componente que se inicializa: la consulta entra por la puerta.
            var consulta = Task.Run(() => puerta.EjecutarAsync(() => contexto.Database
                .SqlQueryRaw<int>("select 1 as \"Value\" from pg_sleep(0.4)")
                .ToListAsync()));

            await EsperarConsultaEnVueloAsync();

            await ConTope(CerrarCircuitoComoElFrameworkAsync(scope), "el cierre del circuito (disposición del scope)", vuelta);

            var resultado = await ConTope(consulta, "la consulta en vuelo", vuelta);
            resultado.Should().BeEquivalentTo(new[] { 1 },
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
    /// Espera a que el servidor vea la consulta lenta ejecutándose: sin esto, el
    /// cierre podría llegar antes de que la consulta arrancara y el test no
    /// probaría nada.
    /// </summary>
    private async Task EsperarConsultaEnVueloAsync()
    {
        var cronometro = Stopwatch.StartNew();
        await using var conexion = new NpgsqlConnection(_cadenaDeObservacion);
        await conexion.OpenAsync();

        while (cronometro.Elapsed < TimeSpan.FromSeconds(10))
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText =
                "select count(*) from pg_stat_activity where application_name = @app and state = 'active' and query like '%pg_sleep%'";
            comando.Parameters.AddWithValue("app", _nombreAplicacion);
            if ((long)(await comando.ExecuteScalarAsync())! > 0) return;

            await Task.Delay(10);
        }

        throw new TimeoutException("La consulta lenta no llegó a estar en vuelo en el servidor.");
    }

    /// <summary>
    /// Un cuelgue no debe colgar la suite: es justo uno de los síntomas medidos, así
    /// que se convierte en un fallo con nombre.
    /// </summary>
    private static async Task<T> ConTope<T>(Task<T> tarea, string que, int vuelta)
    {
        await ConTope((Task)tarea, que, vuelta);
        return await tarea;
    }

    private static async Task ConTope(Task tarea, string que, int vuelta)
    {
        var terminada = await Task.WhenAny(tarea, Task.Delay(TimeSpan.FromSeconds(30)));
        if (terminada != tarea)
            throw new TimeoutException($"{que} se quedó colgada más de 30 s (vuelta {vuelta}).");

        await tarea;
    }
}
