using CaeManager.Application.Common;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Seguimiento de S-C-3 (OWASP API4:2023, ASVS 4.0.3 V12.1.1): sin hueco en el presupuesto de memoria
/// de documentos, una petición HTTP responde 503 con <c>Retry-After</c> y las cabeceras de seguridad,
/// y cualquier otra excepción sigue su camino hasta <c>UseExceptionHandler</c>. Mismo orden que en
/// Program.cs: <c>UseCabecerasSeguridad</c> y, detrás, <c>UsePresupuestoMemoriaAgotadoComo503</c>.
/// </summary>
public sealed class PresupuestoMemoriaAgotadoComo503Tests
{
    [Fact]
    public async Task Sin_hueco_responde_503_con_Retry_After_y_conserva_las_cabeceras_de_seguridad()
    {
        var paginasEstado = new PaginasEstado();

        var contexto = await EjecutarAsync(contexto =>
        {
            // Lo que el endpoint fijó antes de fallar no sale en el 503.
            contexto.Response.Headers.ContentDisposition = "attachment; filename=secreto.pdf";
            contexto.Response.ContentType = "application/pdf";
            throw new PresupuestoMemoriaAgotadoException();
        }, paginasEstado);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        contexto.Response.Headers.RetryAfter.ToString().Should().Be(PresupuestoMemoriaAgotadoExtensions.SegundosReintento);
        contexto.Response.Headers.ContentSecurityPolicy.ToString().Should().Contain("frame-ancestors 'none'");
        contexto.Response.Headers.XContentTypeOptions.ToString().Should().Be("nosniff");
        contexto.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
        contexto.Response.Headers.ContentDisposition.Should().BeEmpty("lo que escribió el endpoint se descarta");
        contexto.Response.ContentType.Should().StartWith("text/plain");
        Cuerpo(contexto).Should().Be(PresupuestoMemoriaAgotadoException.MensajeUsuario);
        paginasEstado.Enabled.Should().BeFalse("el 503 no se reejecuta como «no encontrado»");
    }

    [Fact]
    public async Task Cualquier_otra_excepcion_no_se_traduce()
    {
        var ejecutar = () => EjecutarAsync(_ => throw new InvalidOperationException("otro fallo"));

        await ejecutar.Should().ThrowExactlyAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Sin_excepcion_la_respuesta_no_cambia()
    {
        var contexto = await EjecutarAsync(async contexto =>
        {
            contexto.Response.StatusCode = StatusCodes.Status200OK;
            await contexto.Response.WriteAsync("contenido");
        });

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        contexto.Response.Headers.RetryAfter.Should().BeEmpty();
        Cuerpo(contexto).Should().Be("contenido");
    }

    private static async Task<DefaultHttpContext> EjecutarAsync(Func<HttpContext, Task> terminal, PaginasEstado? paginasEstado = null)
    {
        var servicios = new ServiceCollection().AddLogging().BuildServiceProvider();
        var app = new ApplicationBuilder(servicios);
        app.UseCabecerasSeguridad();
        app.UsePresupuestoMemoriaAgotadoComo503();
        app.Run(contexto => terminal(contexto));
        var pipeline = app.Build();

        var contexto = new DefaultHttpContext { RequestServices = servicios };
        contexto.Response.Body = new MemoryStream();
        if (paginasEstado is not null)
            contexto.Features.Set<IStatusCodePagesFeature>(paginasEstado);
        await pipeline(contexto);
        return contexto;
    }

    private static string Cuerpo(HttpContext contexto)
    {
        contexto.Response.Body.Position = 0;
        return new StreamReader(contexto.Response.Body).ReadToEnd();
    }

    private sealed class PaginasEstado : IStatusCodePagesFeature
    {
        public bool Enabled { get; set; } = true;
    }
}
