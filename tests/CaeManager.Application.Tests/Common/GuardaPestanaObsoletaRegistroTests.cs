using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Composición de la guarda de pestaña obsoleta (D-6 b, R-2) con el registro real de Application: el behavior está en
/// el pipeline y justo antes de <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>, así que un comando
/// de una pestaña obsoleta no llega ni a autorizarse. MediatR ejecuta los behaviors en el orden de registro, el
/// primero por fuera. El cableado de Web lo vigila <c>GuardaPestanaObsoletaCableadaTests</c> (Architecture).
/// </summary>
public class GuardaPestanaObsoletaRegistroTests
{
    [Fact]
    public void El_behavior_esta_registrado_justo_antes_de_la_autorizacion_de_escritura()
    {
        var behaviors = new ServiceCollection().AddApplication()
            .Where(d => d.ServiceType == typeof(IPipelineBehavior<,>))
            .Select(d => d.ImplementationType)
            .ToList();

        var guarda = behaviors.IndexOf(typeof(GuardaPestanaObsoletaBehavior<,>));
        guarda.Should().BeGreaterThanOrEqualTo(0, "sin el behavior, un comando de una pestaña obsoleta se ejecuta");
        behaviors.IndexOf(typeof(AutorizacionEscrituraBehavior<,>)).Should().Be(guarda + 1);
    }
}
