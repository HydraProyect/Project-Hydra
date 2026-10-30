using System.Collections;
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
            AnalisisConcurrentes = global,
            AnalisisConcurrentesPorTenant = porTenant,
            EsperaSegundos = espera
        }));

    private static int Entradas(LimitadorAnalisisImportacion limitador) =>
        ((ICollection)typeof(LimitadorAnalisisImportacion)
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
    public async Task Sin_hueco_global_a_tiempo_se_devuelve_el_cupo_del_tenant()
    {
        using var limitador = Crear(1, 1);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var ocupado = await limitador.IntentarEntrarAsync(a);
        ocupado.Should().NotBeNull();

        (await limitador.IntentarEntrarAsync(b)).Should().BeNull("el hueco global está ocupado y la espera es 0");
        Entradas(limitador).Should().Be(1, "el cupo de B se retira al agotarse la espera del hueco global");

        ocupado!.Dispose();
        Entradas(limitador).Should().Be(0);
        using var libre = await limitador.IntentarEntrarAsync(b);
        libre.Should().NotBeNull("B no debe haberse quedado con su cupo bloqueado");
    }

    [Fact]
    public async Task Un_segundo_analisis_del_mismo_tenant_espera_su_cupo_en_vez_de_rechazarse()
    {
        using var limitador = Crear(2, 1, 30);
        var a = Guid.NewGuid();
        var primero = await limitador.IntentarEntrarAsync(a);
        primero.Should().NotBeNull();

        var segundo = limitador.IntentarEntrarAsync(a);
        await Task.Delay(50);
        segundo.IsCompleted.Should().BeFalse("con el cupo del Tenant lleno, el segundo espera");
        using (var otro = await limitador.IntentarEntrarAsync(Guid.NewGuid()))
            otro.Should().NotBeNull("quien espera a su Tenant no ocupa el hueco global");

        primero!.Dispose();
        using (var permiso = await segundo.WaitAsync(TimeSpan.FromSeconds(10))) permiso.Should().NotBeNull();
        Entradas(limitador).Should().Be(0);
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

        using (var ocupado = await limitador.IntentarEntrarAsync(a))
        using (var cts = new CancellationTokenSource())
        {
            var esperaGlobal = limitador.IntentarEntrarAsync(b, cts.Token);
            var esperaCupo = limitador.IntentarEntrarAsync(a, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => esperaGlobal);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => esperaCupo);
            Entradas(limitador).Should().Be(1, "solo queda el cupo de A, que sigue ocupado");
        }
        Entradas(limitador).Should().Be(0);
        using (var libreA = await limitador.IntentarEntrarAsync(a)) libreA.Should().NotBeNull();
        using (var libreB = await limitador.IntentarEntrarAsync(b)) libreB.Should().NotBeNull();
        Entradas(limitador).Should().Be(0);
    }

    // (4, 1): el global sobra y manda el tope por Tenant (1 + 1). (3, 2): manda el global, por debajo de la suma de cupos
    // (2 + 2), y los que consiguen su cupo pero no el hueco global ejercitan la devolución del cupo.
    [Theory]
    [InlineData(4, 1, 2)]
    [InlineData(3, 2, 3)]
    public async Task Concurrencia_respeta_los_topes_y_limpia_el_diccionario(int global, int porTenant, int esperados)
    {
        using var limitador = Crear(global, porTenant);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var inicio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tareas = Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
        {
            await inicio.Task;
            var tenant = i % 2 == 0 ? a : b;
            return (Tenant: tenant, Permiso: await limitador.IntentarEntrarAsync(tenant));
        })).ToArray();
        inicio.SetResult();
        var resultados = await Task.WhenAll(tareas);
        resultados.Count(r => r.Permiso is not null).Should().Be(esperados);
        resultados.Where(r => r.Permiso is not null).GroupBy(r => r.Tenant)
            .Should().OnlyContain(g => g.Count() <= porTenant, "ningún Tenant pasa de su tope");
        var permisos = resultados.Select(r => r.Permiso).ToArray();
        await Task.WhenAll(permisos.Where(p => p is not null).Select(p => Task.Run(() =>
        {
            p!.Dispose();
            p.Dispose();
        })));
        Entradas(limitador).Should().Be(0);
        using (var nuevoA = await limitador.IntentarEntrarAsync(a))
        using (var nuevoB = await limitador.IntentarEntrarAsync(b))
        {
            nuevoA.Should().NotBeNull();
            nuevoB.Should().NotBeNull();
        }
        Entradas(limitador).Should().Be(0);
    }
}
