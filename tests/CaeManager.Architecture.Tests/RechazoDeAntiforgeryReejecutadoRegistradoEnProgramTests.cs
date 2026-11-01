using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Un POST de formulario sin token antiforgery válido recibe 400, no 500 (ZAP autenticado, run
/// 41250723975; ASVS 4.0.3 V7.4.1 y V13.2.3). La reejecución de <c>/not-found</c> tras ese 400 reentra en
/// <c>UseAntiforgery</c> y el framework lanza; <c>RechazoDeAntiforgeryReejecutadoMiddleware</c> la corta. El
/// comportamiento lo prueba <c>RechazoDeAntiforgeryEnPostDeCuentaTests</c> en Web.Tests con el orden del
/// pipeline copiado; este trinquete vigila que <c>Program.cs</c> mantenga ese orden, porque el proyecto no
/// tiene un <c>WebApplicationFactory</c> que lo observe.
/// </summary>
public class RechazoDeAntiforgeryReejecutadoRegistradoEnProgramTests
{
    [Fact]
    public void Program_corta_la_reejecucion_del_rechazo_justo_antes_de_UseAntiforgery_y_tras_las_paginas_de_estado()
    {
        var program = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        var paginasDeEstado = program.IndexOf("app.UseStatusCodePagesWithReExecute(", StringComparison.Ordinal);
        var correccion = program.IndexOf("app.UseRechazoDeAntiforgeryReejecutado();", StringComparison.Ordinal);
        var antiforgery = program.IndexOf("app.UseAntiforgery();", StringComparison.Ordinal);

        paginasDeEstado.Should().BeGreaterThan(-1);
        correccion.Should().BeGreaterThan(-1,
            "sin esa llamada un POST sin token antiforgery vuelve a acabar en 500 tras reejecutar /not-found");
        antiforgery.Should().BeGreaterThan(-1);
        correccion.Should().BeGreaterThan(paginasDeEstado, "la corta la reejecución, que nace en UseStatusCodePagesWithReExecute");
        correccion.Should().BeLessThan(antiforgery, "tiene que ver la petición antes de que UseAntiforgery la valide otra vez");
        program.Substring(correccion + "app.UseRechazoDeAntiforgeryReejecutado();".Length, antiforgery - correccion - "app.UseRechazoDeAntiforgeryReejecutado();".Length)
            .Should().NotContain("app.Use", "ningún otro middleware debe interponerse entre la corrección y UseAntiforgery");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
            " — este test necesita el árbol fuente del repositorio.");
    }
}