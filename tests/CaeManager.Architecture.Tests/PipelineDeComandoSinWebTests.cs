using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// MediatR construye todos los behaviors para cualquier petición, Queries incluidas. Los arneses de integración que
/// componen <c>AddApplication()</c> a mano, sin Infrastructure ni Web, registran solo lo que piden sus handlers: un
/// behavior que en su constructor arrastre, directa o transitivamente por los registros de Application, el
/// <see cref="IActorAuditoria"/> de Web o un repositorio de Domain (los registra Infrastructure) tumba toda petición de
/// esos contenedores. Pasó con la guarda de pestaña obsoleta (D-6 b, R-2), que pedía el registro de auditoría del
/// cambio de contexto: 129 fallos en los cuatro bloques de integración del run 44195422480, todos por esa resolución.
/// Lo que solo hace falta en un camino raro se resuelve del proveedor del ámbito en ese camino, como
/// <c>DuplicadosEmpresaBehavior</c> y <c>GuardaPestanaObsoletaBehavior</c>.
/// </summary>
/// <remarks>
/// Se recorre el grafo de constructores en vez de construir el contenedor con Infrastructure: esa composición no
/// existe sin el actor de Web (el <c>AuditoriaInterceptor</c> del DbContext ya lo pide), y la que falló es la de
/// solo Application. Un registro por fábrica no se puede recorrer y se da por bueno; hoy los behaviors no tienen ninguno.
/// </remarks>
public class PipelineDeComandoSinWebTests
{
    [Fact]
    public void Ningun_behavior_arrastra_en_su_constructor_lo_que_solo_registran_Web_o_Infrastructure()
    {
        var servicios = new ServiceCollection().AddApplication();
        var behaviors = servicios
            .Where(d => d.ServiceType == typeof(IPipelineBehavior<,>) || d.ServiceType == typeof(IStreamPipelineBehavior<,>))
            .Select(d => d.ImplementationType!)
            .ToList();
        behaviors.Should().Contain(typeof(GuardaPestanaObsoletaBehavior<,>), "el instrumento tiene que ver la guarda");

        var arrastres = behaviors
            .SelectMany(b => Arrastres(b, servicios, [b.Name]))
            .ToList();

        arrastres.Should().BeEmpty(
            "un behavior se construye para toda petición, y estos caminos piden lo que un contenedor de solo Application no tiene");
    }

    private static IEnumerable<string> Arrastres(Type tipo, IServiceCollection servicios, List<string> camino)
    {
        var constructor = tipo.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
        if (constructor is null)
            yield break;

        foreach (var parametro in constructor.GetParameters())
        {
            var dependencia = parametro.ParameterType;
            var siguiente = new List<string>(camino) { dependencia.Name };

            if (EsDeWebODeInfrastructure(dependencia))
            {
                yield return string.Join(" -> ", siguiente);
                continue;
            }

            // Solo se recorre lo que Application registra por tipo; el último registro es el que se resuelve.
            var registro = servicios.LastOrDefault(d => d.ServiceType == dependencia
                || (dependencia.IsGenericType && d.ServiceType == dependencia.GetGenericTypeDefinition()));
            if (registro?.ImplementationType is not { } implementacion || camino.Count > 12 || camino.Contains(implementacion.Name))
                continue;

            foreach (var arrastre in Arrastres(implementacion, servicios, siguiente))
                yield return arrastre;
        }
    }

    private static bool EsDeWebODeInfrastructure(Type tipo) =>
        tipo == typeof(IActorAuditoria)
        || (tipo.IsInterface
            && tipo.Assembly == typeof(CaeManager.Domain.Common.Result).Assembly
            && tipo.Name.EndsWith("Repository", StringComparison.Ordinal));
}
