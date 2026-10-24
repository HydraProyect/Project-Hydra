using Bunit;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

public partial class Empresa360PaginaTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public async Task Frescura_conserva_paginas_y_filas_si_falla_la_relectura(int lecturaQueFalla, bool agrupada)
    {
        var circuito = await CircuitoFrescuraDePrueba.RegistrarAsync(this);
        var mediador = Registrar(Empresa());
        var cut = Renderizar(agrupada ? "?pestana=trabajadores&agrupar=oficio" : "?pestana=trabajadores");
        await cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
        var nombres = cut.FindAll(".fila-relacion-nombre").Select(e => e.TextContent).ToArray();
        nombres.Should().NotBeEmpty();
        var fallo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var iniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lecturas = 0;
        mediador.Retener = p =>
        {
            if (p is not ObtenerTrabajadoresQuery || ++lecturas != lecturaQueFalla) return null;
            iniciada.TrySetResult();
            return fallo.Task;
        };
        circuito.Publicar(2);
        await iniciada.Task.WaitAsync(CircuitoFrescuraDePrueba.EsperaPositiva);
        cut.FindAll(".fila-relacion-nombre").Select(e => e.TextContent).Should().Equal(nombres);
        fallo.SetException(new InvalidOperationException("Fallo de relectura provocado por el test."));
        await Task.Delay(CircuitoFrescuraDePrueba.EsperaNegativa);
        circuito.Cache.AvisosRecibidos.Should().Be(1);
        cut.FindAll(".fila-relacion-nombre").Select(e => e.TextContent).Should().Equal(nombres);
        cut.Markup.Should().NotContain("No pudimos cargar");
        cut.Markup.Should().NotContain("aria-busy=\"true\"");
        if (!agrupada)
        {
            var antes = mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Count();
            await cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Anterior")).ClickAsync(new MouseEventArgs());
            await cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
            mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().HaveCount(antes, "las dos páginas buenas siguen en caché");
            cut.FindAll(".fila-relacion-nombre").Select(e => e.TextContent).Should().Equal(nombres);
        }
    }

    [Fact]
    public async Task Frescura_sustituye_la_cache_cuando_la_relectura_acierta()
    {
        var circuito = await CircuitoFrescuraDePrueba.RegistrarAsync(this);
        var mediador = Registrar(Empresa());
        var cut = Renderizar("?pestana=trabajadores");
        await cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
        mediador.Trabajadores[EmpresaId] = [.. mediador.Trabajadores[EmpresaId].Select(t => t with { Apellidos = "Nuevo " + t.Apellidos })];

        circuito.Publicar(2);

        cut.WaitForAssertion(
            () => cut.FindAll(".fila-relacion-nombre").Should().OnlyContain(e => e.TextContent.Contains("Nuevo")),
            CircuitoFrescuraDePrueba.EsperaPositiva);
        await cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Anterior")).ClickAsync(new MouseEventArgs());
        cut.FindAll(".fila-relacion-nombre").Should().OnlyContain(e => e.TextContent.Contains("Nuevo"), "la primera página sale de la caché nueva");
    }

    [Fact]
    public async Task Frescura_repinta_con_los_datos_nuevos_si_el_usuario_cambio_de_pagina_durante_la_relectura()
    {
        var circuito = await CircuitoFrescuraDePrueba.RegistrarAsync(this);
        var mediador = Registrar(Empresa());
        var cut = Renderizar("?pestana=trabajadores");
        await cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
        mediador.Trabajadores[EmpresaId] = [.. mediador.Trabajadores[EmpresaId].Select(t => t with { Apellidos = "Nuevo " + t.Apellidos })];
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var iniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lecturas = 0;
        mediador.Retener = p =>
        {
            if (p is not ObtenerTrabajadoresQuery || ++lecturas != 6) return null;
            iniciada.TrySetResult();
            return liberar.Task;
        };

        circuito.Publicar(2);
        await iniciada.Task.WaitAsync(CircuitoFrescuraDePrueba.EsperaPositiva);
        await cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Anterior")).ClickAsync(new MouseEventArgs());
        liberar.SetResult();

        cut.WaitForAssertion(
            () => cut.FindAll(".fila-relacion-nombre").Should().OnlyContain(e => e.TextContent.Contains("Nuevo")),
            CircuitoFrescuraDePrueba.EsperaPositiva);
    }
}
