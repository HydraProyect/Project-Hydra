using System.Reflection;
using CaeManager.Infrastructure.Importacion;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

public class LimitadorAnalisisPorTenantTests
{
    private static LimitadorAnalisisImportacion Crear(int global = 4, int porTenant = 2, int espera = 0) =>
        new(Options.Create(new OpcionesLimitesImportacion
        {
            AnalisisConcurrentes = global, AnalisisConcurrentesPorTenant = porTenant, EsperaSegundos = espera
        }));

    private static int Entradas(LimitadorAnalisisImportacion limitador) =>
        ((Dictionary<Guid, int>)typeof(LimitadorAnalisisImportacion)
            .GetField("_porTenant", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(limitador)!).Count;

    [Fact]
    public async Task Un_tenant_agotado_no_impide_entrar_a_otro()
    {
        using var limitador = Crear();
        var a = Guid.NewGuid();
        using (var primero = await limitador.IntentarEntrarAsync(a))
        using (var segundo = await limitador.IntentarEntrarAsync(a))
        using (var otro = await limitador.IntentarEntrarAsync(Guid.NewGuid()))
        {
            primero.Should().NotBeNull();
            segundo.Should().NotBeNull();
            otro.Should().NotBeNull();
            (await limitador.IntentarEntrarAsync(a)).Should().BeNull();
        }
        Entradas(limitador).Should().Be(0);
    }

    [Fact]
    public async Task Sin_tenant_resuelto_se_rechaza_sin_cubo_compartido()
    {
        using var limitador = Crear(1, 1);
        (await limitador.IntentarEntrarAsync(null)).Should().BeNull();
        (await limitador.IntentarEntrarAsync(Guid.Empty)).Should().BeNull();
        Entradas(limitador).Should().Be(0);
        using var conTenant = await limitador.IntentarEntrarAsync(Guid.NewGuid());
        conTenant.Should().NotBeNull("los rechazos sin Tenant no ocupan el hueco global");
    }

    [Fact]
    public async Task Excepcion_y_cancelacion_de_espera_liberan_las_reservas()
    {
        using var limitador = Crear(1, 1, 30);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var permiso = await limitador.IntentarEntrarAsync(a);
            permiso.Should().NotBeNull();
            throw new InvalidOperationException();
        });
        Entradas(limitador).Should().Be(0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var permiso = await limitador.IntentarEntrarAsync(a);
            permiso.Should().NotBeNull();
            throw new OperationCanceledException();
        });
        Entradas(limitador).Should().Be(0);
        using (var ocupado = await limitador.IntentarEntrarAsync(a))
        using (var cts = new CancellationTokenSource())
        {
            var espera = limitador.IntentarEntrarAsync(b, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => espera);
            Entradas(limitador).Should().Be(1);
        }
        using (var libre = await limitador.IntentarEntrarAsync(b)) libre.Should().NotBeNull();
        Entradas(limitador).Should().Be(0);
    }

    [Fact]
    public async Task Concurrencia_respeta_los_topes_y_limpia_el_diccionario()
    {
        using var limitador = Crear();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var inicio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tareas = Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
        {
            await inicio.Task;
            return await limitador.IntentarEntrarAsync(i % 2 == 0 ? a : b);
        })).ToArray();
        inicio.SetResult();
        var permisos = await Task.WhenAll(tareas);
        permisos.Count(p => p is not null).Should().Be(4);
        await Task.WhenAll(permisos.Where(p => p is not null).Select(p => Task.Run(() =>
        {
            p!.Dispose();
            p.Dispose();
        })));
        Entradas(limitador).Should().Be(0);
        using (var nuevoA = await limitador.IntentarEntrarAsync(a)) nuevoA.Should().NotBeNull();
        Entradas(limitador).Should().Be(0);
    }
}
