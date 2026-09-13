using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Plataforma.Commands.AutoConcederPrivilegio;
using CaeManager.Application.Plataforma.Queries.PuedeInicializarPlataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Web.Features.Plataforma.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace CaeManager.Web.Tests;

public class PlataformaGen2Tests : BunitContext
{
    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken()
            => new("prueba-antiforgery", "__RequestVerificationToken");
    }

    private sealed class MediatorRegistrador(Func<object, object> responder) : IMediator
    {
        public List<object> Enviados { get; } = [];
        public List<CancellationToken> TokensDeConsulta { get; } = [];

        public Task<TResponse> Send<TResponse>(
            IRequest<TResponse> request,
            CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            if (request is PuedeInicializarPlataformaQuery)
            {
                TokensDeConsulta.Add(cancellationToken);
            }
            var respuesta = responder(request);
            return respuesta is Task<TResponse> tarea ? tarea : Task.FromResult((TResponse)respuesta);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
        {
            Enviados.Add(request!);
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return Task.FromResult<object?>(null);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(
            object request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => Task.CompletedTask;
    }

    public PlataformaGen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        AddAuthorization().SetAuthorized("raiz@ejemplo.test");
        Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();
    }

    private (IRenderedComponent<Plataforma> Cut, MediatorRegistrador Mediator) Renderizar(Func<object, object> responder)
    {
        var mediator = new MediatorRegistrador(responder);
        Services.AddScoped<IMediator>(_ => mediator);
        return (Render<Plataforma>(), mediator);
    }

    private static Task CargarDirectamente(Plataforma instancia)
        => (Task)typeof(Plataforma).GetMethod("CargarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instancia, null)!;

    private static long LeerGeneracion(Plataforma instancia)
        => (long)typeof(Plataforma).GetField("_generacion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instancia)!;

    [Fact]
    public void La_puerta_disponible_explica_el_acto_y_ofrece_solo_las_dos_rutas_globales_dibujadas()
    {
        var (cut, _) = Renderizar(peticion => peticion switch
        {
            PuedeInicializarPlataformaQuery => true,
            _ => throw new NotSupportedException()
        });

        cut.Find("#acto-fundacional").TextContent.Should().Be("Acto fundacional");
        cut.Find(".administracion-global-tarjeta").TextContent.Should().Contain("no permite concederla a otra persona");
        var rutas = cut.FindAll(".administracion-global-enlaces a").ToList();
        rutas.Should().HaveCount(2, "control positivo: hay enlaces globales que observar");
        rutas.Select(ruta => ruta.GetAttribute("href")).Should().OnlyContain(href => href == "/delegaciones" || href == "/configuracion/comercial");
        rutas.Select(ruta => ruta.TextContent.Trim()).Should()
            .Contain(texto => texto.StartsWith("Organizaciones externas")).And
            .Contain(texto => texto.StartsWith("Estado comercial"));
    }

    [Fact]
    public async Task Inicializar_exige_confirmacion_explicita_y_solo_entonces_despacha_el_comando_fundacional()
    {
        var (cut, mediator) = Renderizar(peticion => peticion switch
        {
            PuedeInicializarPlataformaQuery => true,
            AutoConcederPrivilegioCommand => Result.Exito(Guid.NewGuid()),
            _ => throw new NotSupportedException()
        });

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Inicializar administración global").ClickAsync(new MouseEventArgs());
        mediator.Enviados.OfType<AutoConcederPrivilegioCommand>().Should().BeEmpty("abrir la confirmación no escribe nada");
        cut.FindAll("[role=dialog]").Should().ContainSingle();
        cut.Find("[role=dialog]").TextContent.Should().Contain("vigencia indefinida");

        await cut.Find(".administracion-global-confirmacion input").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Sí, inicializar").ClickAsync(new MouseEventArgs());

        var comando = mediator.Enviados.OfType<AutoConcederPrivilegioCommand>().Should().ContainSingle().Subject;
        comando.TenantObjetivoId.Should().Be(Guid.Empty);
        comando.Capacidad.Should().Be(CapacidadPrivilegio.AdminPlataforma);
        comando.DiasDeVigencia.Should().Be(1);
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        cut.Find(".administracion-global-estado-consumido").TextContent.Should().Contain("No hay nada que inicializar aquí");
    }

    [Fact]
    public async Task Un_fallo_del_comando_se_muestra_y_deja_la_confirmacion_abierta_para_reintentar()
    {
        var (cut, mediator) = Renderizar(peticion => peticion switch
        {
            PuedeInicializarPlataformaQuery => true,
            AutoConcederPrivilegioCommand => Result.Fallo<Guid>(Error.Crear("ConcesionPrivilegio.SinDobleFactor", "Activa la autenticación en dos pasos.")),
            _ => throw new NotSupportedException()
        });

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Inicializar administración global").ClickAsync(new MouseEventArgs());
        await cut.Find(".administracion-global-confirmacion input").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Sí, inicializar").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<AutoConcederPrivilegioCommand>().Should().ContainSingle();
        cut.FindAll("[role=dialog]").Should().ContainSingle("el desenlace fallido no se trata como éxito");
        cut.Find(".administracion-global-error-comando").TextContent.Should().Contain("Activa la autenticación en dos pasos.");
    }

    [Fact]
    public async Task La_guarda_del_panel_ignora_un_segundo_confirmar_mientras_el_primero_esta_en_vuelo()
    {
        var pendiente = new TaskCompletionSource<Result<Guid>>();
        var (cut, mediator) = Renderizar(peticion => peticion switch
        {
            PuedeInicializarPlataformaQuery => true,
            AutoConcederPrivilegioCommand => pendiente.Task,
            _ => throw new NotSupportedException()
        });

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Inicializar administración global").ClickAsync(new MouseEventArgs());
        await cut.Find(".administracion-global-confirmacion input").ChangeAsync(new ChangeEventArgs { Value = true });
        var confirmar = cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Sí, inicializar");
        var primero = confirmar.ClickAsync(new MouseEventArgs());
        var segundo = confirmar.ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<AutoConcederPrivilegioCommand>().Should().ContainSingle("control positivo: el primer clic llegó al panel");
        await cut.InvokeAsync(() => pendiente.SetResult(Result.Exito(Guid.NewGuid())));
        await primero.WaitAsync(TimeSpan.FromSeconds(10));
        await segundo.WaitAsync(TimeSpan.FromSeconds(10));
        mediator.Enviados.OfType<AutoConcederPrivilegioCommand>().Should().ContainSingle("la segunda entrada encuentra la guarda del panel");
    }

    [Fact]
    public async Task Un_error_de_descubrimiento_no_afirma_un_estado_y_reintentar_vuelve_a_consultar()
    {
        var intentos = 0;
        var (cut, mediator) = Renderizar(peticion => peticion switch
        {
            PuedeInicializarPlataformaQuery when intentos++ == 0 => throw new InvalidOperationException("fallo simulado"),
            PuedeInicializarPlataformaQuery => true,
            _ => throw new NotSupportedException()
        });

        cut.Find("[role=alert]").TextContent.Should().Contain("No pudimos comprobar el estado");
        cut.Find("[role=alert] span").TextContent.Should().Be("No se pudo comprobar el estado. No se ha ejecutado ninguna acción.");
        cut.Markup.Should().NotContain("La administración global todavía no está inicializada");
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reintentar").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<PuedeInicializarPlataformaQuery>().Should().HaveCount(2, "control positivo: la segunda consulta fue observable");
        cut.Find(".administracion-global-tarjeta").TextContent.Should().Contain("todavía no está inicializada");
    }

    [Fact]
    public async Task Una_carga_antigua_cancelada_no_sobrescribe_el_resultado_de_la_carga_nueva()
    {
        var primera = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var segunda = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consultas = 0;
        var (cut, mediator) = Renderizar(peticion => peticion switch
        {
            PuedeInicializarPlataformaQuery => ++consultas switch { 1 => true, 2 => primera.Task, _ => segunda.Task },
            _ => throw new NotSupportedException()
        });

        var cargaAntigua = CargarDirectamente(cut.Instance);
        var cargaNueva = CargarDirectamente(cut.Instance);
        mediator.TokensDeConsulta.Should().HaveCount(3, "control positivo: la carga inicial y las dos cargas solapadas recibieron token observable");
        mediator.TokensDeConsulta[1].IsCancellationRequested.Should().BeTrue("la carga nueva cancela el ciclo antiguo");
        await cut.InvokeAsync(() => segunda.SetResult(false));
        await cargaNueva.WaitAsync(TimeSpan.FromSeconds(10));
        await cut.InvokeAsync(() => primera.SetResult(true));
        await cargaAntigua.WaitAsync(TimeSpan.FromSeconds(10));
        cut.Render();

        cut.FindAll(".administracion-global-estado-consumido").Should().ContainSingle("el resultado nuevo dejó una superficie observable");
        cut.Markup.Should().NotContain("todavía no está inicializada");
    }

    [Fact]
    public async Task Dispose_invalida_la_carga_y_cancela_su_token()
    {
        var pendiente = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (cut, mediator) = Renderizar(peticion => peticion switch
        {
            PuedeInicializarPlataformaQuery => pendiente.Task,
            _ => throw new NotSupportedException()
        });

        mediator.TokensDeConsulta.Should().ContainSingle("control positivo: la carga inicial recibió un token");
        var token = mediator.TokensDeConsulta.Single();
        var instancia = cut.Instance;
        var generacionAnterior = LeerGeneracion(instancia);
        instancia.Dispose();

        token.IsCancellationRequested.Should().BeTrue();
        LeerGeneracion(instancia).Should().BeGreaterThan(generacionAnterior, "Dispose invalida los desenlaces de este ciclo");
        pendiente.SetResult(true);
        await pendiente.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task El_resultado_de_un_comando_de_un_ciclo_anterior_no_modifica_el_modal_actual()
    {
        var pendiente = new TaskCompletionSource<Result<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (cut, mediator) = Renderizar(peticion => peticion switch
        {
            PuedeInicializarPlataformaQuery => true,
            AutoConcederPrivilegioCommand => pendiente.Task,
            _ => throw new NotSupportedException()
        });

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Inicializar administración global").ClickAsync(new MouseEventArgs());
        await cut.Find(".administracion-global-confirmacion input").ChangeAsync(new ChangeEventArgs { Value = true });
        var confirmar = cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Sí, inicializar");
        var comandoEnVuelo = confirmar.ClickAsync(new MouseEventArgs());
        mediator.Enviados.OfType<AutoConcederPrivilegioCommand>().Should().ContainSingle("control positivo: el comando comenzó antes del cambio de ciclo");

        await CargarDirectamente(cut.Instance).WaitAsync(TimeSpan.FromSeconds(10));
        await cut.InvokeAsync(() => pendiente.SetResult(Result.Exito(Guid.NewGuid())));
        await comandoEnVuelo.WaitAsync(TimeSpan.FromSeconds(10));
        cut.Render();

        cut.FindAll("[role=dialog]").Should().ContainSingle("el desenlace tardío no cerró el modal del ciclo nuevo");
        cut.Find(".administracion-global-tarjeta").TextContent.Should().Contain("todavía no está inicializada");
        cut.FindAll("[role=dialog] button")
            .Single(b => b.TextContent.Trim() == "Cancelar")
            .GetAttribute("disabled")
            .Should()
            .NotBeNull("el desenlace tardío no liberó la operación del ciclo anterior");
    }
}
