using System.Collections.Concurrent;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Tests;

/// <summary>
/// Límites de la recuperación de contraseña (revisión de seguridad): longitud del correo,
/// límite por cuenta que no se llena, tiempo máximo por envío y aviso acotado de cola llena.
/// </summary>
public class RecuperacionContrasenaLimitesTests
{
    private static readonly Uri Base = new("https://cuentas.example/");

    private sealed class Reloj : TimeProvider
    {
        public DateTimeOffset Ahora { get; set; } = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Ahora;
    }

    private sealed class Registro : ILogger<RecuperacionContrasena>
    {
        public ConcurrentQueue<(LogLevel Nivel, string Mensaje)> Entradas { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Enqueue((logLevel, formatter(state, exception)));
    }

    private static RecuperacionContrasena Crear(
        Func<string, Uri, CancellationToken, Task>? procesador = null, TimeSpan? limite = null, TimeProvider? reloj = null, ILogger<RecuperacionContrasena>? registro = null) =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            reloj ?? TimeProvider.System, registro ?? new Registro())
        {
            ProcesadorDePrueba = procesador,
            TiempoMaximoPorEnvio = limite ?? TimeSpan.FromSeconds(30),
        };

    [Fact]
    public void El_limite_permite_tres_por_hora_y_vuelve_a_permitir_pasada_la_hora()
    {
        var reloj = new Reloj();
        var limite = new LimiteSolicitudesPorCuenta(reloj);

        for (var i = 0; i < 3; i++)
        {
            limite.HayCupo("ANA@EXAMPLE.TEST").Should().BeTrue();
            limite.Consumir("ANA@EXAMPLE.TEST");
        }

        limite.HayCupo("ANA@EXAMPLE.TEST").Should().BeFalse();
        reloj.Ahora += TimeSpan.FromHours(1);
        limite.HayCupo("ANA@EXAMPLE.TEST").Should().BeTrue();
    }

    [Fact]
    public void La_estructura_del_limite_no_se_llena_con_correos_distintos()
    {
        var limite = new LimiteSolicitudesPorCuenta(new Reloj());
        var denegadas = 0;
        const int Distintos = 30_000; // por encima del antiguo tope de 10.000 cuentas

        for (var i = 0; i < Distintos; i++)
        {
            var correo = $"anonimo{i}@example.test".ToUpperInvariant();
            if (!limite.HayCupo(correo)) denegadas++;
            else limite.Consumir(correo);
        }

        // Solo se niega por colisión de posición saturada (rara); nunca por "lleno".
        denegadas.Should().BeLessThan(Distintos / 20);

        // Una cuenta legítima cuya posición nadie ha usado sigue teniendo cupo.
        var usadas = Enumerable.Range(0, Distintos)
            .Select(i => LimiteSolicitudesPorCuenta.Posicion($"anonimo{i}@example.test".ToUpperInvariant())).ToHashSet();
        var legitima = Enumerable.Range(0, 100_000).Select(i => $"legitima{i}@example.test".ToUpperInvariant())
            .First(c => !usadas.Contains(LimiteSolicitudesPorCuenta.Posicion(c)));
        limite.HayCupo(legitima).Should().BeTrue();
    }

    [Fact]
    public async Task Un_correo_mas_largo_que_una_direccion_se_descarta_sin_procesarlo()
    {
        var procesados = new ConcurrentQueue<string>();
        var servicio = Crear((correo, _, _) => { procesados.Enqueue(correo); return Task.CompletedTask; });
        var largo = new string('A', RecuperacionContrasena.LongitudMaximaCorreo + 1) + "@EXAMPLE.TEST";
        var limite = new string('B', RecuperacionContrasena.LongitudMaximaCorreo - 13) + "@EXAMPLE.TEST";

        servicio.Solicitar(largo, Base);
        servicio.Solicitar(limite, Base);
        await servicio.StartAsync(CancellationToken.None);
        try
        {
            await EsperarAsync(() => procesados.Count >= 1);
            await Task.Delay(100);
            procesados.Should().Equal(limite);
        }
        finally
        {
            await servicio.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Un_envio_colgado_se_corta_por_tiempo_y_la_cola_sigue()
    {
        var segundoProcesado = new TaskCompletionSource();
        var servicio = Crear(async (correo, _, ct) =>
        {
            if (correo == "COLGADO@EXAMPLE.TEST") await Task.Delay(Timeout.Infinite, ct);
            else segundoProcesado.TrySetResult();
        }, limite: TimeSpan.FromMilliseconds(150));

        servicio.Solicitar("COLGADO@EXAMPLE.TEST", Base);
        servicio.Solicitar("SIGUIENTE@EXAMPLE.TEST", Base);
        await servicio.StartAsync(CancellationToken.None);
        try
        {
            await segundoProcesado.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await servicio.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void La_cola_llena_descarta_y_avisa_una_sola_vez_por_minuto_sin_datos_personales()
    {
        var reloj = new Reloj();
        var registro = new Registro();
        var servicio = Crear(reloj: reloj, registro: registro); // sin arrancar: nadie consume

        for (var i = 0; i < 300; i++) servicio.Solicitar($"CUENTA{i}@EXAMPLE.TEST", Base);

        var avisos = registro.Entradas.Where(e => e.Nivel == LogLevel.Warning).ToList();
        avisos.Should().ContainSingle("el aviso está acotado: no hay una línea por solicitud descartada");
        avisos[0].Mensaje.Should().NotContain("@").And.NotContain("EXAMPLE");

        reloj.Ahora += TimeSpan.FromMinutes(2);
        servicio.Solicitar("OTRA@EXAMPLE.TEST", Base);
        registro.Entradas.Count(e => e.Nivel == LogLevel.Warning).Should().Be(2, "pasado el minuto vuelve a avisar");
    }

    [Fact]
    public async Task Una_solicitud_descartada_por_cola_llena_no_gasta_el_cupo_de_la_cuenta()
    {
        var procesados = new ConcurrentQueue<string>();
        var servicio = Crear((correo, _, _) => { procesados.Enqueue(correo); return Task.CompletedTask; });
        for (var i = 0; i < 256; i++) servicio.Solicitar($"RELLENO{i}@EXAMPLE.TEST", Base);
        for (var i = 0; i < 5; i++) servicio.Solicitar("VICTIMA@EXAMPLE.TEST", Base); // cola llena: se descartan

        await servicio.StartAsync(CancellationToken.None);
        try
        {
            await EsperarAsync(() => procesados.Count >= 256);
            servicio.Solicitar("VICTIMA@EXAMPLE.TEST", Base);
            await EsperarAsync(() => procesados.Contains("VICTIMA@EXAMPLE.TEST"));
        }
        finally
        {
            await servicio.StopAsync(CancellationToken.None);
        }
    }

    private static async Task EsperarAsync(Func<bool> condicion)
    {
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (!condicion())
        {
            if (DateTime.UtcNow > limite) throw new TimeoutException("La condición no llegó a cumplirse.");
            await Task.Delay(20);
        }
    }
}
