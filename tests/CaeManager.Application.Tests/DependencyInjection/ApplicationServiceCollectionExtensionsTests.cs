using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Plataforma;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Application.Tests.DependencyInjection;

/// <summary>
/// Regresión directa de un fallo real en CI: los fixtures de
/// <c>CaeManager.IntegrationTests</c> montan un <c>ServiceCollection</c>
/// mínimo con solo <c>AddApplication()</c> (sin <c>AddInfrastructure()</c>,
/// que trae la implementación real de Sentry) — sin
/// <see cref="AlertaOperativaInerte"/> registrado con <c>TryAddSingleton</c>,
/// resolver <see cref="VentanaSaludOperativa"/> (que <c>LoggingBehavior</c>
/// necesita, y por tanto cualquier request de MediatR) lanzaba
/// <c>InvalidOperationException</c> por no poder resolver
/// <see cref="IAlertaOperativa"/>.
/// </summary>
public class ApplicationServiceCollectionExtensionsTests
{
    [Fact]
    public void AddApplication_por_si_sola_resuelve_IAlertaOperativa_sin_Infrastructure()
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        using var proveedor = servicios.BuildServiceProvider();

        var accion = () => proveedor.GetRequiredService<IAlertaOperativa>();

        accion.Should().NotThrow();
        accion().Should().BeOfType<AlertaOperativaInerte>();
    }

    [Fact]
    public void Una_implementacion_registrada_despues_de_AddApplication_sustituye_al_valor_inerte()
    {
        // Mismo orden que Program.cs: AddApplication() y luego AddInfrastructure()
        // (aquí simulado con un AddSingleton directo) — así resuelve la app real.
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<IAlertaOperativa, AlertaOperativaFalsaDePrueba>();
        using var proveedor = servicios.BuildServiceProvider();

        proveedor.GetRequiredService<IAlertaOperativa>().Should().BeOfType<AlertaOperativaFalsaDePrueba>();
    }

    [Fact]
    public void AddApplication_por_si_sola_resuelve_ISesionPrivilegiadaActual_sin_Infrastructure()
    {
        // Mismo fallo de forma que el de IAlertaOperativa: MediatR construye
        // TODOS los behaviors para cualquier request, Queries incluidas, asi que
        // AutorizacionEscrituraBehavior tiene que poder resolverse en un
        // contenedor minimo.
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        using var proveedor = servicios.BuildServiceProvider();

        proveedor.GetRequiredService<ISesionPrivilegiadaActual>()
            .Should().BeOfType<SesionPrivilegiadaAusente>();
    }

    [Fact]
    public async Task El_valor_por_defecto_de_sesion_privilegiada_no_concede_nada()
    {
        // Un default de una pieza de autorizacion tiene que decir "no" — si
        // alguna vez devolviera una sesion, este test lo veria.
        (await new SesionPrivilegiadaAusente().ObtenerAsync()).Should().BeNull();
    }

    [Fact]
    public void Una_implementacion_de_sesion_privilegiada_registrada_despues_sustituye_al_valor_por_defecto()
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddScoped<ISesionPrivilegiadaActual, SesionPrivilegiadaFalsaDePrueba>();
        using var proveedor = servicios.BuildServiceProvider();

        proveedor.GetRequiredService<ISesionPrivilegiadaActual>()
            .Should().BeOfType<SesionPrivilegiadaFalsaDePrueba>();
    }

    private sealed class SesionPrivilegiadaFalsaDePrueba : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class AlertaOperativaFalsaDePrueba : IAlertaOperativa
    {
        public void Emitir(string mensaje, NivelAlertaOperativa nivel)
        {
        }

        public void CapturarExcepcion(Exception excepcion)
        {
        }
    }
}
