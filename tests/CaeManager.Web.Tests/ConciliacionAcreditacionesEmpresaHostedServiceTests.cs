using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Documentos;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El disparador de la conciliación de la regla A-1 (<see cref="ConciliacionAcreditacionesEmpresaHostedService"/>):
/// un Tenant o un lote fallido deja la pasada incompleta (y se registra como error), y la instancia sigue intentándolo,
/// también cuando otra tiene el liderazgo, hasta completar una pasada o agotar los intentos. La pasada real bajo
/// <c>cae_app_runtime</c> la mide <c>AltaAcreditacionesBajoRuntimeTests</c>.
/// </summary>
public class ConciliacionAcreditacionesEmpresaHostedServiceTests
{
    private sealed class RegistroEnMemoria : ILogger<ConciliacionAcreditacionesEmpresaHostedService>
    {
        public List<(LogLevel Nivel, string Mensaje)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entradas.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Lo que devuelve la conciliación de cada Tenant, decidido por el Tenant del ámbito explícito.</summary>
    private sealed class ConciliacionPorTenant(Dictionary<Guid, Func<ResultadoConciliacionTenant>> porTenant, List<Guid?> ambitos)
        : IConciliacionAcreditacionesEmpresaService
    {
        public Task<ResultadoConciliacionTenant> ConciliarTenantEnCursoAsync(CancellationToken cancellationToken = default)
        {
            var tenantId = AmbitoTenantExplicito.TenantIdActual;
            ambitos.Add(tenantId);
            return Task.FromResult(porTenant[tenantId!.Value]());
        }
    }

    /// <summary>No obtiene el liderazgo las primeras veces indicadas; después ejecuta el trabajo.</summary>
    private sealed class LiderTrasEsperar(int vecesSinLiderazgo) : IEleccionLiderService
    {
        public int Llamadas { get; private set; }

        public async Task<bool> IntentarEjecutarComoLiderAsync(
            string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
        {
            Llamadas++;
            if (Llamadas <= vecesSinLiderazgo)
                return false;
            await trabajo(cancellationToken);
            return true;
        }
    }

    private static readonly ResultadoPasadaConciliacion PasadaCompleta = new(1, [], 0, 0);
    private static readonly ResultadoPasadaConciliacion PasadaIncompleta = new(1, [Guid.NewGuid()], 1, 0);

    private static ConciliacionAcreditacionesEmpresaHostedService Disparador(
        IEleccionLiderService lider, RegistroEnMemoria registro, IServiceProvider? servicios = null, int intentos = 5) =>
        new((servicios ?? new ServiceCollection().BuildServiceProvider()).GetRequiredService<IServiceScopeFactory>(),
            lider,
            new ConciliacionAcreditacionesEmpresaOptions
            {
                EjecutarAlArrancar = true,
                IntentosMaximos = intentos,
                EsperaEntreIntentos = TimeSpan.Zero,
            },
            TimeProvider.System, registro);

    [Fact]
    public async Task Un_Tenant_que_falla_y_otro_con_un_lote_fallido_dejan_la_pasada_incompleta_sin_detener_al_resto()
    {
        var falla = Guid.NewGuid();
        var conLoteFallido = Guid.NewGuid();
        var completo = Guid.NewGuid();
        var ambitos = new List<Guid?>();
        var servicios = new ServiceCollection()
            .AddScoped<IConciliacionAcreditacionesEmpresaService>(_ => new ConciliacionPorTenant(new()
            {
                [falla] = () => throw new InvalidOperationException("Fallo simulado del Tenant."),
                [conLoteFallido] = () => new ResultadoConciliacionTenant(3, 2, [new InvalidOperationException("Fallo simulado del lote.")]),
                [completo] = () => new ResultadoConciliacionTenant(5, 1, []),
            }, ambitos))
            .BuildServiceProvider();
        var registro = new RegistroEnMemoria();

        var pasada = await Disparador(new LiderTrasEsperar(0), registro, servicios)
            .ConciliarTenantsAsync([falla, conLoteFallido, completo], CancellationToken.None);

        ambitos.Should().Equal([falla, conLoteFallido, completo], "cada Tenant en su propio ámbito, también tras un fallo");
        pasada.Completa.Should().BeFalse();
        pasada.TenantsIncompletos.Should().Equal([falla, conLoteFallido]);
        pasada.LotesFallidos.Should().Be(1);
        pasada.Creadas.Should().Be(8, "lo que sí se guardó cuenta, también el resto del Tenant con un lote fallido");
        registro.Entradas.Should().NotContain(e => e.Mensaje.Contains("terminada"),
            "una pasada con fallos no se registra como terminada");
        registro.Entradas.Last().Nivel.Should().Be(LogLevel.Error);
        registro.Entradas.Last().Mensaje.Should().Contain("incompleta").And.Contain("2 de 3 Tenants");
    }

    [Fact]
    public async Task Si_todos_los_Tenants_completan_la_pasada_queda_terminada()
    {
        var tenant = Guid.NewGuid();
        var servicios = new ServiceCollection()
            .AddScoped<IConciliacionAcreditacionesEmpresaService>(_ => new ConciliacionPorTenant(new()
            {
                [tenant] = () => new ResultadoConciliacionTenant(2, 1, []),
            }, []))
            .BuildServiceProvider();
        var registro = new RegistroEnMemoria();

        var pasada = await Disparador(new LiderTrasEsperar(0), registro, servicios)
            .ConciliarTenantsAsync([tenant], CancellationToken.None);

        pasada.Completa.Should().BeTrue();
        pasada.Creadas.Should().Be(2);
        registro.Entradas.Last().Nivel.Should().Be(LogLevel.Information);
        registro.Entradas.Last().Mensaje.Should().Contain("terminada");
    }

    [Fact]
    public async Task La_instancia_sin_liderazgo_lo_reintenta_y_completa_la_pasada_cuando_la_lider_lo_suelta()
    {
        var lider = new LiderTrasEsperar(vecesSinLiderazgo: 2);
        var pasadas = 0;

        var completada = await Disparador(lider, new RegistroEnMemoria()).ConciliarHastaCompletarAsync(_ =>
        {
            pasadas++;
            return Task.FromResult(PasadaCompleta);
        }, CancellationToken.None);

        completada.Should().BeTrue();
        lider.Llamadas.Should().Be(3, "dos sin liderazgo y la tercera como líder");
        pasadas.Should().Be(1);
    }

    [Fact]
    public async Task Una_pasada_incompleta_o_que_lanza_se_repite_hasta_completarla()
    {
        var lider = new LiderTrasEsperar(0);
        var pasadas = 0;

        var completada = await Disparador(lider, new RegistroEnMemoria()).ConciliarHastaCompletarAsync(_ =>
        {
            pasadas++;
            return pasadas switch
            {
                1 => Task.FromResult(PasadaIncompleta),
                2 => throw new InvalidOperationException("Fallo simulado de la pasada."),
                _ => Task.FromResult(PasadaCompleta),
            };
        }, CancellationToken.None);

        completada.Should().BeTrue();
        pasadas.Should().Be(3);
    }

    [Fact]
    public async Task Agotados_los_intentos_lo_registra_como_error_y_para()
    {
        var lider = new LiderTrasEsperar(vecesSinLiderazgo: int.MaxValue);
        var registro = new RegistroEnMemoria();

        var completada = await Disparador(lider, registro, intentos: 3)
            .ConciliarHastaCompletarAsync(_ => Task.FromResult(PasadaCompleta), CancellationToken.None);

        completada.Should().BeFalse();
        lider.Llamadas.Should().Be(3);
        registro.Entradas.Last().Nivel.Should().Be(LogLevel.Error);
        registro.Entradas.Last().Mensaje.Should().Contain("no se completó en 3 intentos");
    }
}
