using Bunit;
using CaeManager.Application.Contactos.Commands.FijarNotaContactoAgenda;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Contactos.Queries.ObtenerNotaContacto;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Revisión Codex r1 de la #2167, MEDIO 1: las cargas de la nota fija pueden terminar fuera de orden. Con el interlocutor A,
/// luego B y de nuevo A, la lectura antigua de A vuelve a coincidir con la clave (tipo, entidad y correo) y, si llega la
/// última, sustituiría a la nota de la lectura más reciente. Solo aplica su resultado la carga que aún es la última.
/// </summary>
public class NotaFijaContactoCargasTests : BunitContext
{
    private sealed class MediadorControlado : IMediator
    {
        public List<(ObtenerNotaContactoQuery Consulta, TaskCompletionSource<object?> Respuesta)> Lecturas { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is FijarNotaContactoAgendaCommand) return (TResponse)(object)Result.Exito();
            if (request is not ObtenerNotaContactoQuery consulta) throw new NotSupportedException(request.GetType().Name);

            var respuesta = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Lecturas.Add((consulta, respuesta));
            return (TResponse)(await respuesta.Task)!;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private static NotaContactoDto Nota(string nombre, string texto) =>
        new(Guid.NewGuid(), nombre, texto, null, new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task Una_lectura_antigua_del_interlocutor_A_no_pisa_la_mas_reciente_tras_A_B_A()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura(Roles.Consulta);
        Services.AddLocalization();
        var mediador = new MediadorControlado();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton<ILogger<NotaFijaContacto>>(_ => NullLogger<NotaFijaContacto>.Instance);
        var entidad = Guid.NewGuid();

        var cut = Render<NotaFijaContacto>(p => p
            .Add(x => x.Tipo, TipoPropietarioAgenda.Cliente).Add(x => x.PropietarioId, entidad).Add(x => x.Email, "a@obra.es"));
        cut.WaitForAssertion(() => mediador.Lecturas.Should().HaveCount(1));
        cut.Render(p => p.Add(x => x.Email, "b@obra.es"));
        cut.WaitForAssertion(() => mediador.Lecturas.Should().HaveCount(2));
        cut.Render(p => p.Add(x => x.Email, "a@obra.es"));
        cut.WaitForAssertion(() => mediador.Lecturas.Should().HaveCount(3));

        // Fuera de orden: primero la lectura más reciente de A, y después la antigua de A y la de B.
        await cut.InvokeAsync(() => mediador.Lecturas[2].Respuesta.SetResult(Nota("Ana", "Nota reciente de A.")));
        cut.WaitForAssertion(() => cut.Find(".nota-fija").TextContent.Should().Contain("Nota reciente de A."));

        await cut.InvokeAsync(() => mediador.Lecturas[0].Respuesta.SetResult(Nota("Ana", "Nota antigua de A.")));
        await cut.InvokeAsync(() => mediador.Lecturas[1].Respuesta.SetResult(Nota("Beto", "Nota de B.")));

        cut.Find(".nota-fija").TextContent.Should().Contain("Nota reciente de A.")
            .And.NotContain("Nota antigua de A.").And.NotContain("Nota de B.");
    }

    private (IRenderedComponent<NotaFijaContacto> Cut, MediadorControlado Mediador) MontarConEscritura(
        Func<IReadOnlyCollection<Guid>, Task<IReadOnlyDictionary<Guid, string>>>? resolverAutores = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura(Roles.GestorCae);
        Services.AddLocalization();
        var mediador = new MediadorControlado();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton<ILogger<NotaFijaContacto>>(_ => NullLogger<NotaFijaContacto>.Instance);
        var cut = Render<NotaFijaContacto>(p =>
        {
            p.Add(x => x.Tipo, TipoPropietarioAgenda.Cliente).Add(x => x.PropietarioId, Guid.NewGuid()).Add(x => x.Email, "carmen@obra.es");
            if (resolverAutores is not null) p.Add(x => x.ResolverAutores, resolverAutores);
        });
        cut.WaitForAssertion(() => mediador.Lecturas.Should().HaveCount(1));
        return (cut, mediador);
    }

    /// <summary>Abre la edición, escribe el texto y empieza a guardar: devuelve la tarea del guardado, que acaba con la recarga.</summary>
    private static async Task<Task> GuardarAsync(IRenderedComponent<NotaFijaContacto> cut, MediadorControlado mediador, string texto)
    {
        await cut.Find(".nota-fija-texto-boton").ClickAsync(new MouseEventArgs());
        await cut.Find(".nota-fija-area").InputAsync(new ChangeEventArgs { Value = texto });
        var guardado = cut.Find(".nota-fija-acciones button").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => mediador.Lecturas.Should().HaveCount(2, "tras escribir, el componente relee la nota"));
        return guardado;
    }

    /// <summary>
    /// Revisión Codex r2 de la #2167, MEDIO 2: tras escribir bien, la edición se cierra antes de acabar la recarga. Si la página
    /// renderiza mientras tanto, no puede ofrecer «Editar» con la nota antigua (guardarla revertiría el cambio): se ve lo recién
    /// guardado, sin «Editar», y al llegar la lectura vuelve a poder editarse. Detecta quitar «!_guardando» del botón y quitar la nota provisional.
    /// </summary>
    [Fact]
    public async Task Mientras_se_relee_tras_guardar_no_se_ofrece_Editar_ni_se_ve_la_nota_antigua()
    {
        var (cut, mediador) = MontarConEscritura();
        await cut.InvokeAsync(() => mediador.Lecturas[0].Respuesta.SetResult(Nota("Carmen", "Texto antiguo.")));
        cut.WaitForAssertion(() => cut.Find(".nota-fija").TextContent.Should().Contain("Texto antiguo."));

        var guardado = await GuardarAsync(cut, mediador, "Texto nuevo.");
        cut.Render(p => p.Add(x => x.Email, "carmen@obra.es")); // la página renderiza con la recarga en vuelo

        cut.FindAll(".nota-fija-texto-boton").Should().BeEmpty("con la recarga en vuelo no se abre la nota antigua para revertirla");
        cut.Find(".nota-fija-texto").TextContent.Trim().Should().Be("Texto nuevo.");
        cut.Find(".nota-fija").TextContent.Should().NotContain("Texto antiguo.").And.NotContain("Agenda ·", "autor y fecha aún no son los del texto nuevo");

        await cut.InvokeAsync(() => mediador.Lecturas[1].Respuesta.SetResult(Nota("Carmen", "Texto nuevo.")));
        await guardado;

        cut.Find(".nota-fija").TextContent.Should().Contain("Agenda ·");
        cut.FindAll(".nota-fija-texto-boton:not(.nota-fija-reintentar)").Should().ContainSingle("al llegar la lectura, «Editar» vuelve");
    }

    /// <summary>
    /// Revisión Codex r2 de la #2167, BAJO: si la lectura posterior a un guardado correcto falla, la nota guardada no desaparece:
    /// se conserva el texto guardado y se ofrece reintentar. Detecta volver a poner la nota a null en el catch.
    /// </summary>
    [Fact]
    public async Task Si_falla_la_lectura_tras_guardar_se_conserva_la_nota_guardada_y_se_puede_reintentar()
    {
        var (cut, mediador) = MontarConEscritura();
        await cut.InvokeAsync(() => mediador.Lecturas[0].Respuesta.SetResult(Nota("Carmen", "Texto antiguo.")));
        cut.WaitForAssertion(() => cut.Find(".nota-fija").TextContent.Should().Contain("Texto antiguo."));
        var guardado = await GuardarAsync(cut, mediador, "Texto nuevo.");

        await cut.InvokeAsync(() => mediador.Lecturas[1].Respuesta.SetException(new InvalidOperationException("boom")));
        await guardado;

        cut.Find(".nota-fija-texto").TextContent.Trim().Should().Be("Texto nuevo.", "el texto guardado sigue a la vista");
        cut.FindAll(".nota-fija-reintentar").Should().ContainSingle();

        var reintento = cut.Find(".nota-fija-reintentar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => mediador.Lecturas.Should().HaveCount(3, "«Reintentar» vuelve a leer la misma nota"));
        await cut.InvokeAsync(() => mediador.Lecturas[2].Respuesta.SetResult(Nota("Carmen", "Texto nuevo.")));
        await reintento;

        cut.FindAll(".nota-fija-reintentar").Should().BeEmpty();
        cut.Find(".nota-fija").TextContent.Should().Contain("Agenda ·");
    }

    /// <summary>
    /// Revisión Codex r2 de la #2167, BAJO: si la primera lectura falla no hay nota que conservar, pero tampoco queda un hueco
    /// mudo: aviso y «Reintentar».
    /// </summary>
    [Fact]
    public async Task Si_falla_la_primera_lectura_se_ofrece_reintentar()
    {
        var (cut, mediador) = MontarConEscritura();
        await cut.InvokeAsync(() => mediador.Lecturas[0].Respuesta.SetException(new InvalidOperationException("boom")));
        cut.WaitForAssertion(() => cut.FindAll(".nota-fija-reintentar").Should().ContainSingle());
        cut.FindAll(".nota-fija-area").Should().BeEmpty();

        var reintento = cut.Find(".nota-fija-reintentar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => mediador.Lecturas.Should().HaveCount(2));
        await cut.InvokeAsync(() => mediador.Lecturas[1].Respuesta.SetResult(Nota("Carmen", "Prefiere la mañana.")));
        await reintento;

        cut.Find(".nota-fija").TextContent.Should().Contain("Prefiere la mañana.");
        cut.FindAll(".nota-fija-reintentar").Should().BeEmpty();
    }

    /// <summary>
    /// Revisión Codex r2 de la #2167, BAJO: si el directorio de autores falla, la nota se pinta con el autor genérico en vez de
    /// desaparecer entera. Detecta que el fallo del resolutor tumbe la nota.
    /// </summary>
    [Fact]
    public async Task Si_falla_la_resolucion_del_autor_la_nota_se_ve_con_autor_generico()
    {
        var (cut, mediador) = MontarConEscritura(_ => Task.FromException<IReadOnlyDictionary<Guid, string>>(new InvalidOperationException("boom")));
        var nota = new NotaContactoDto(Guid.NewGuid(), "Carmen", "Prefiere la mañana.", Guid.NewGuid(), new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc));
        await cut.InvokeAsync(() => mediador.Lecturas[0].Respuesta.SetResult(nota));

        cut.WaitForAssertion(() => cut.Find(".nota-fija-texto").TextContent.Trim().Should().Be("Prefiere la mañana."));
        cut.FindAll(".nota-fija-reintentar").Should().BeEmpty("la nota se cargó: solo falló el nombre del autor");
    }
}
