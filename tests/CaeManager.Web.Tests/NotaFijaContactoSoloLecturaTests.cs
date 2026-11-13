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
/// El rol Consulta lee la nota fija del contacto, pero no la edita: sin botones de escritura. El componente lo decide con
/// <c>SoloConEscritura</c>; el servidor lo impone aparte, esto fija que la interfaz no lo ofrece.
/// </summary>
public class NotaFijaContactoSoloLecturaTests : BunitContext
{
    private sealed class MediadorDeLectura(NotaContactoDto? nota) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object? respuesta = request switch
            {
                ObtenerNotaContactoQuery => nota,
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult((TResponse)respuesta!);
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

    [Fact]
    public void Con_rol_Consulta_la_nota_fija_se_lee_pero_no_se_edita()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura(Roles.Consulta);
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => new MediadorDeLectura(
            new NotaContactoDto(Guid.NewGuid(), "Carmen Ruiz", "Prefiere la mañana.", null, new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc))));
        Services.AddScoped<ToastService>();
        Services.AddSingleton<ILogger<NotaFijaContacto>>(_ => NullLogger<NotaFijaContacto>.Instance);

        var cut = Render<NotaFijaContacto>(p => p
            .Add(x => x.Tipo, TipoPropietarioAgenda.Cliente)
            .Add(x => x.PropietarioId, Guid.NewGuid())
            .Add(x => x.Email, "carmen@obra.es"));

        cut.WaitForAssertion(() => cut.Find(".nota-fija").TextContent.Should().Contain("Prefiere la mañana."));
        cut.FindAll(".nota-fija button").Should().BeEmpty();
    }
}
