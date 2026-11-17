using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Visitas.Antelacion;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// D-4 (#2005, decisión del 2026-11-15): el sello del expediente de una Visita usa la regla de bloqueo común, en la que un
/// Pendiente en plataforma bloquea. Una orden que cambia el estado de una Acreditación puede, por tanto, completar el
/// expediente igual que subir un documento, y tiene que reevaluarlo: lo hace el comportamiento
/// <see cref="ReevaluarExpedientesTrasCambioDeAcreditacionBehavior{TRequest,TResponse}"/> con las órdenes que llevan el
/// marcador <see cref="ICambiaEstadoDeAcreditacion"/>. Este test fija que toda orden con un <c>AcreditacionId</c> lo lleve:
/// una orden nueva sin él dejaría Visitas sin sellar hasta una edición que no tiene nada que ver.
/// </summary>
public class OrdenesDeAcreditacionReevaluanElExpedienteTests
{
    [Fact]
    public void Toda_orden_con_AcreditacionId_reevalua_el_expediente_de_las_visitas()
    {
        var ordenes = typeof(ICommand).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetInterfaces().Any(i => i == typeof(ICommand)
                || (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>))))
            .Where(t => t.GetProperty("AcreditacionId")?.PropertyType == typeof(Guid))
            .ToList();

        ordenes.Should().NotBeEmpty("si no encuentra ninguna orden, el test mide un instrumento roto y no la regla");

        ordenes.Where(t => !typeof(ICambiaEstadoDeAcreditacion).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .Should().BeEmpty("una orden que cambia una Acreditación puede completar el expediente de una Visita (D-4, #2005)");
    }
}
