using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// El acumulador del coste de carga (Server-Timing y registro por carga del circuito):
/// suma lo que recibe, ignora duraciones negativas, no pierde cuentas con escrituras
/// concurrentes y la resta de instantáneas da el coste de un tramo.
/// </summary>
public class CosteCargaTests
{
    [Fact]
    public void Acumula_comandos_conexiones_espera_y_despachos()
    {
        var coste = new CosteCarga();

        coste.RegistrarComando(TimeSpan.FromMilliseconds(3));
        coste.RegistrarComando(TimeSpan.FromMilliseconds(4.5));
        coste.RegistrarConexion();
        coste.RegistrarEsperaPuerta(TimeSpan.FromMilliseconds(2));
        coste.RegistrarDespacho();
        coste.RegistrarDespacho();
        coste.RegistrarDespacho();

        coste.Leer().Should().Be(new InstantaneaCosteCarga(
            TimeSpan.FromMilliseconds(7.5), 2, 1, TimeSpan.FromMilliseconds(2), 3));
    }

    [Fact]
    public void Una_duracion_negativa_cuenta_el_comando_pero_no_resta_tiempo()
    {
        var coste = new CosteCarga();

        coste.RegistrarComando(TimeSpan.FromMilliseconds(-5));
        coste.RegistrarEsperaPuerta(TimeSpan.FromMilliseconds(-5));

        var lectura = coste.Leer();
        lectura.Comandos.Should().Be(1);
        lectura.BaseDatos.Should().Be(TimeSpan.Zero);
        lectura.EsperaPuerta.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task No_pierde_cuentas_con_escrituras_concurrentes()
    {
        var coste = new CosteCarga();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                coste.RegistrarComando(TimeSpan.FromTicks(10));
                coste.RegistrarConexion();
                coste.RegistrarDespacho();
            }
        })));

        var lectura = coste.Leer();
        lectura.Comandos.Should().Be(8000);
        lectura.Conexiones.Should().Be(8000);
        lectura.Despachos.Should().Be(8000);
        lectura.BaseDatos.Should().Be(TimeSpan.FromTicks(80_000));
    }

    [Fact]
    public void La_resta_de_instantaneas_da_solo_lo_ocurrido_en_el_tramo()
    {
        var coste = new CosteCarga();
        coste.RegistrarComando(TimeSpan.FromMilliseconds(10));
        coste.RegistrarDespacho();
        var inicio = coste.Leer();

        coste.RegistrarComando(TimeSpan.FromMilliseconds(1));
        coste.RegistrarConexion();

        coste.Leer().Menos(inicio).Should().Be(new InstantaneaCosteCarga(
            TimeSpan.FromMilliseconds(1), 1, 1, TimeSpan.Zero, 0));
    }

    [Fact]
    public async Task La_puerta_suma_la_espera_en_cola_al_acumulador()
    {
        var coste = new CosteCarga();
        var puerta = new PuertaAccesoDatos(coste);
        var dentro = new TaskCompletionSource();
        var soltar = new TaskCompletionSource();

        var primera = puerta.EjecutarAsync(async () =>
        {
            dentro.SetResult();
            await soltar.Task;
        });
        await dentro.Task;
        var segunda = puerta.EjecutarAsync(() => Task.CompletedTask);
        await Task.Delay(50);
        soltar.SetResult();
        await Task.WhenAll(primera, segunda);

        // La segunda esperó al menos los ~50 ms que la primera tuvo la puerta (margen por la
        // resolución del temporizador); la primera entró sin cola.
        coste.Leer().EsperaPuerta.Should().BeGreaterThan(TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public async Task Una_operacion_anidada_no_suma_espera_porque_no_hace_cola()
    {
        var coste = new CosteCarga();
        var puerta = new PuertaAccesoDatos(coste);

        await puerta.EjecutarAsync(() => puerta.EjecutarAsync(() => Task.Delay(30)));

        coste.Leer().EsperaPuerta.Should().BeLessThan(TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public async Task El_behavior_cuenta_un_despacho_y_devuelve_el_resultado_intacto()
    {
        var coste = new CosteCarga();
        var behavior = new CosteCargaBehavior<string, int>(coste);

        var resultado = await behavior.Handle("x", _ => Task.FromResult(42), default);

        resultado.Should().Be(42);
        coste.Leer().Despachos.Should().Be(1);
    }

    [Fact]
    public async Task La_puerta_del_contenedor_mide_sobre_el_acumulador_de_su_mismo_scope()
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        using var proveedor = servicios.BuildServiceProvider();
        using var ambito = proveedor.CreateScope();
        var puerta = ambito.ServiceProvider.GetRequiredService<PuertaAccesoDatos>();
        var coste = ambito.ServiceProvider.GetRequiredService<CosteCarga>();

        var soltar = new TaskCompletionSource();
        var dentro = new TaskCompletionSource();
        var primera = puerta.EjecutarAsync(async () => { dentro.SetResult(); await soltar.Task; });
        await dentro.Task;
        var segunda = puerta.EjecutarAsync(() => Task.CompletedTask);
        await Task.Delay(50);
        soltar.SetResult();
        await Task.WhenAll(primera, segunda);

        coste.Leer().EsperaPuerta.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void AddApplication_registra_el_acumulador_por_scope_y_el_behavior_por_fuera_de_todos()
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        using var proveedor = servicios.BuildServiceProvider();

        using var a = proveedor.CreateScope();
        using var b = proveedor.CreateScope();
        a.ServiceProvider.GetRequiredService<CosteCarga>()
            .Should().BeSameAs(a.ServiceProvider.GetRequiredService<CosteCarga>())
            .And.NotBeSameAs(b.ServiceProvider.GetRequiredService<CosteCarga>());

        var behaviors = servicios.Where(d => d.ServiceType == typeof(IPipelineBehavior<,>))
            .Select(d => d.ImplementationType).ToList();
        behaviors.IndexOf(typeof(CosteCargaBehavior<,>)).Should().Be(0);
    }
}
