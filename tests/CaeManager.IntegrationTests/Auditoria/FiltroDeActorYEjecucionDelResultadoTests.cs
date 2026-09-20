using CaeManager.Application.Common;
using CaeManager.Infrastructure.Autenticacion;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// <b>Dónde termina el alcance del filtro de actor</b> (P41c, seguimiento). Medido
/// sobre el pipeline real de ASP.NET Core con Kestrel, no supuesto.
///
/// <para>
/// En las API mínimas el filtro de endpoint envuelve la <i>invocación del handler</i>;
/// el <c>IResult</c> que este devuelve se ejecuta DESPUÉS, cuando el filtro ya ha
/// retornado y su <c>using</c> ha liberado el ámbito. Si algún camino de un grupo
/// filtrado escribiera algo auditable durante <c>ExecuteAsync</c> del resultado —y no
/// dentro del handler—, esa fila saldría como <c>Desconocido</c> otra vez.
/// </para>
///
/// <para>
/// Este test fija el hecho: el handler ve <c>IntegracionExterna</c> y la ejecución del
/// resultado no ve nada. Lo que hace que hoy no haya ninguna fila afectada NO es el
/// filtro sino el contenido de los grupos —ningún handler devuelve un resultado que
/// escriba—, y eso lo sostiene <c>SuperficiesAnonimasClasificadasPorActorTests</c>.
/// Si alguna vez hace falta cubrir la ejecución del resultado, el filtro no basta:
/// habría que declarar el ámbito en un middleware que envuelva el endpoint entero.
/// </para>
/// </summary>
public class FiltroDeActorYEjecucionDelResultadoTests
{
    [Fact]
    public async Task El_handler_ve_IntegracionExterna_y_la_ejecucion_del_resultado_no_ve_ambito()
    {
        TipoActor? enElHandler = null;
        TipoActor? enTaskRunDelHandler = null;
        TipoActor? enLaEjecucionDelResultado = null;
        var ejecutado = false;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();

        app.MapGroup("/grupo")
            .AddEndpointFilter<ActorIntegracionExternaEndpointFilter>()
            .MapGet("/x", async () =>
            {
                enElHandler = AmbitoActorAuditoria.TipoActorActual;
                // Trabajo que el PROPIO handler lanza: el ExecutionContext fluye, hereda el ámbito.
                enTaskRunDelHandler = await Task.Run(() => AmbitoActorAuditoria.TipoActorActual);
                return new ResultadoQueObserva(() =>
                {
                    ejecutado = true;
                    enLaEjecucionDelResultado = AmbitoActorAuditoria.TipoActorActual;
                });
            });

        await app.StartAsync();
        try
        {
            var direccion = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();

            using var cliente = new HttpClient { BaseAddress = new Uri(direccion) };
            var respuesta = await cliente.GetAsync("/grupo/x");

            respuesta.IsSuccessStatusCode.Should().BeTrue("el endpoint de prueba responde 200");
        }
        finally
        {
            await app.StopAsync();
        }

        ejecutado.Should().BeTrue(
            "control positivo: el resultado SÍ se ejecutó; sin esto, un null en la línea de abajo " +
            "podría ser 'no llegó a observarse' y no 'no había ámbito'");
        enElHandler.Should().Be(TipoActor.IntegracionExterna,
            "el handler corre dentro del filtro");
        enTaskRunDelHandler.Should().Be(TipoActor.IntegracionExterna,
            "el trabajo que el propio handler lanza con Task.Run hereda el ámbito: por eso el ratchet " +
            "no lo trata como «ejecución del resultado»");
        enLaEjecucionDelResultado.Should().BeNull(
            "el resultado se ejecuta después de que el filtro retorne: una escritura auditable hecha " +
            "ahí saldría como Desconocido. Hoy ningún grupo filtrado la hace, y eso lo sostiene el " +
            "ratchet de superficies, no el filtro");
    }

    private sealed class ResultadoQueObserva(Action alEjecutar) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            alEjecutar();
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        }
    }
}
