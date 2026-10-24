using Bunit;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class TrabajadoresListaGen2Tests
{
    [Fact]
    public async Task Frescura_no_pide_datos_sin_Empresa_elegida_ni_mientras_se_resuelve()
    {
        var circuito = await CircuitoFrescuraDePrueba.RegistrarAsync(this);
        var mediador = ConCartera(origenGestionado: false);
        var puerta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediador.RetenerAutorizados = puerta.Task;
        Registrar(mediador, "trabajadores");
        var cut = Render<Trabajadores>();
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados.ObtenerClientesAutorizadosQuery>().Should().ContainSingle());
        try
        {
            circuito.Publicar(2);
            await Task.Delay(CircuitoFrescuraDePrueba.EsperaNegativa);
            circuito.Cache.AvisosRecibidos.Should().Be(1);
            mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().BeEmpty("la Empresa todavía se está resolviendo");
            cut.FindAll(".lista-filas-acordeon").Should().BeEmpty();
        }
        finally
        {
            puerta.TrySetResult();
        }
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Selecciona una empresa de tu cartera"));
        circuito.Publicar(3);
        await Task.Delay(CircuitoFrescuraDePrueba.EsperaNegativa);
        circuito.Cache.AvisosRecibidos.Should().Be(2);
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().BeEmpty("no hay Empresa elegida");
        cut.FindAll(".lista-filas-acordeon").Should().BeEmpty();
    }

    [Fact]
    public async Task Frescura_conserva_la_lista_buena_si_falla_la_relectura_del_aviso()
    {
        var circuito = await CircuitoFrescuraDePrueba.RegistrarAsync(this);
        var mediador = new MediatorFalso { Almacen = { Trabajador("Ana", "Conservada") } };
        var cut = Renderizar(mediador);
        cut.WaitForAssertion(() => cut.FindAll(".enlace-nombre-fila").Should().ContainSingle());
        var nombres = cut.FindAll(".enlace-nombre-fila").Select(e => e.TextContent).ToArray();
        var lecturas = mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Count();
        var fallo = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var iniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediador.Retener = p =>
        {
            if (p is not ObtenerTrabajadoresQuery) return null;
            iniciada.TrySetResult();
            return fallo.Task;
        };
        circuito.Publicar(2);
        await iniciada.Task.WaitAsync(CircuitoFrescuraDePrueba.EsperaPositiva);
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().HaveCount(lecturas + 1);
        fallo.SetException(new InvalidOperationException("Fallo de relectura provocado por el test."));
        await Task.Delay(CircuitoFrescuraDePrueba.EsperaNegativa);
        circuito.Cache.AvisosRecibidos.Should().Be(1);
        cut.FindAll(".enlace-nombre-fila").Select(e => e.TextContent).Should().Equal(nombres);
        cut.Markup.Should().NotContain("No pudimos cargar");
        cut.FindComponents<CaeManager.Web.Components.DesignSystem.EstadoVacio>().Should().BeEmpty();
        cut.Markup.Should().NotContain("aria-busy=\"true\"");
    }
}
