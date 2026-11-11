using Bunit;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Contactos.Queries.ObtenerNotaContacto;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using MediatR;
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
}
