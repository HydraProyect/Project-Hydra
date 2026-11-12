using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Application.Tests.HerramientasPdf;

/// <summary>
/// La pantalla de Dividir PDF envía el lote de Guardar con <c>Mediator.Send</c>, sin envolverlo ella en la puerta: lo
/// serializa el pipeline, con la misma <see cref="PuertaAccesoDatos"/> scoped que la revalidación del circuito. Aquí se
/// comprueba con el registro real de <c>AddApplication</c>, no con una instancia construida a mano.
/// </summary>
public class CrearDocumentosDesdeDivisionPorLaPuertaTests
{
    [Fact]
    public async Task El_lote_de_Guardar_no_entra_mientras_otra_operacion_del_circuito_tiene_la_puerta()
    {
        var registrados = new ServiceCollection();
        registrados.AddApplication();

        // Solo la serialización y la puerta, con sus registros tal cual: el resto del pipeline pide Infrastructure.
        IServiceCollection servicios = new ServiceCollection();
        foreach (var registro in registrados.Where(d =>
                     d.ImplementationType == typeof(SerializacionAccesoDatosBehavior<,>) || d.ServiceType == typeof(PuertaAccesoDatos)))
            servicios.Add(registro);

        await using var proveedor = servicios.BuildServiceProvider();
        await using var circuito = proveedor.CreateAsyncScope();
        var puerta = circuito.ServiceProvider.GetRequiredService<PuertaAccesoDatos>();
        var behaviors = circuito.ServiceProvider
            .GetServices<IPipelineBehavior<CrearDocumentosDesdeDivisionCommand, Result<IReadOnlyList<ResultadoTramoCreado>>>>()
            .ToList();
        behaviors.Should().ContainSingle("AddApplication registra la serialización para todo request, también este Command");

        var tomada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var soltar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Como la revalidación del circuito: otra operación de datos del mismo scope con la puerta tomada.
        var revalidacion = puerta.EjecutarAsync(async () =>
        {
            tomada.SetResult();
            await soltar.Task;
        });
        await tomada.Task;

        var entro = false;
        var comando = new CrearDocumentosDesdeDivisionCommand([1], "paquete.pdf", Guid.NewGuid(), Guid.NewGuid(), []);
        var lote = behaviors[0].Handle(comando, _ =>
        {
            entro = true;
            return Task.FromResult(Result.Exito<IReadOnlyList<ResultadoTramoCreado>>([]));
        }, CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(200));
        entro.Should().BeFalse("el lote espera a que la otra operación suelte la puerta del DbContext");

        soltar.SetResult();
        await revalidacion;
        (await lote).EsExitoso.Should().BeTrue();
        entro.Should().BeTrue();
    }
}
