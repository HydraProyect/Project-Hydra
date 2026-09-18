using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="EstadoDelCircuito"/> necesita registrarse dos veces sobre LA
/// MISMA instancia scoped: como servicio inyectable (para que
/// <c>MainLayout</c> lo consulte) y como <see cref="CircuitHandler"/> (para
/// que el framework llame a <see cref="CircuitHandler.OnCircuitClosedAsync"/>
/// sobre ella). <c>Program.cs</c> lo consigue con un <c>AddScoped&lt;EstadoDelCircuito&gt;()</c>
/// más un <c>AddScoped&lt;CircuitHandler&gt;(sp => sp.GetRequiredService&lt;EstadoDelCircuito&gt;())</c>
/// — este test fija esa propiedad frente a la forma con la que se rompe:
/// un <c>AddScoped&lt;CircuitHandler, EstadoDelCircuito&gt;()</c> directo, que
/// compila igual pero crea una <b>segunda</b> instancia. Con esa segunda
/// instancia, el framework marca una y <c>MainLayout</c> lee la otra: el
/// guard de seguridad leería <c>Cerrado == false</c> para siempre, incluso
/// con el circuito ya cerrado.
///
/// <para>
/// Las dos líneas se reproducen aquí en vez de invocarlas desde
/// <c>Program.cs</c> — el proyecto no expone un <c>WebApplicationFactory</c>
/// de pruebas, así que este test no puede detectar por sí solo que
/// <c>Program.cs</c> haya cambiado a la forma equivocada; fija la propiedad
/// en abstracto y confía en que quien edite ese registro copie el patrón.
/// </para>
/// </summary>
public class EstadoDelCircuitoRegistroDITests
{
    [Fact]
    public void El_registro_correcto_resuelve_la_misma_instancia_como_servicio_y_como_CircuitHandler()
    {
        var servicios = new ServiceCollection();
        servicios.AddScoped<EstadoDelCircuito>();
        servicios.AddScoped<CircuitHandler>(sp => sp.GetRequiredService<EstadoDelCircuito>());

        using var scope = servicios.BuildServiceProvider().CreateScope();
        var comoServicio = scope.ServiceProvider.GetRequiredService<EstadoDelCircuito>();
        var comoCircuitHandler = scope.ServiceProvider.GetServices<CircuitHandler>().OfType<EstadoDelCircuito>().Single();

        // Si el framework marca comoCircuitHandler.Cerrado y MainLayout lee
        // comoServicio.Cerrado, y no son el mismo objeto, la marca nunca la ve
        // quien pregunta: el guard leería "vivo" para siempre.
        ReferenceEquals(comoServicio, comoCircuitHandler).Should().BeTrue();
    }

    /// <summary>
    /// El lado que demuestra que el test de arriba observa algo real: la
    /// forma que Codex propuso como mutación (compila, pasa desapercibida en
    /// revisión) SÍ produce dos instancias con este mismo arnés.
    /// </summary>
    [Fact]
    public void El_registro_directo_como_CircuitHandler_crea_una_instancia_distinta()
    {
        var servicios = new ServiceCollection();
        servicios.AddScoped<EstadoDelCircuito>();
        servicios.AddScoped<CircuitHandler, EstadoDelCircuito>(); // MUTACIÓN: la forma que rompe la identidad.

        using var scope = servicios.BuildServiceProvider().CreateScope();
        var comoServicio = scope.ServiceProvider.GetRequiredService<EstadoDelCircuito>();
        var comoCircuitHandler = scope.ServiceProvider.GetServices<CircuitHandler>().OfType<EstadoDelCircuito>().Single();

        ReferenceEquals(comoServicio, comoCircuitHandler).Should().BeFalse(
            "dos AddScoped independientes para el mismo tipo concreto crean instancias independientes");
    }
}
