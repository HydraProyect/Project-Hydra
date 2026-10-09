using Bunit;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Resto del defecto C1 del piloto Outbound (lote 2). <c>/alertas</c> la abre también el rol
/// Consulta, y su enlace «Ver tu cola de trabajo en Mi trabajo» lleva a <c>/bandeja</c>, que a ese rol
/// le deniega el acceso. El enlace se ofrece a quien el menú ofrece «Mi trabajo»
/// (<c>CatalogoMenuLateral.RolesDeMiTrabajo</c>).
///
/// <para>
/// <b>Qué observa y qué no.</b> El marcado de la página con una cuenta de cada rol; la fila del
/// Gestor CAE es el control positivo de la del rol Consulta. No prueba que <c>/bandeja</c> deniegue
/// el acceso al rol Consulta: eso es el <c>[Authorize]</c> de esa página, que este cambio no toca.
/// </para>
/// </summary>
public class AlertasEnlaceMiTrabajoTests : BunitContext
{
    /// <summary>La columna Vencimiento usa TextoFechaCopiable, que importa clipboard.js al pintarse; el JavaScript queda fuera.</summary>
    public AlertasEnlaceMiTrabajoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    [Theory]
    [InlineData(Roles.GestorCae, 1)]
    [InlineData(Roles.Consulta, 0)]
    public void El_enlace_a_Mi_trabajo_solo_se_ofrece_a_quien_puede_abrirlo(string rol, int esperados)
    {
        this.ConRolDeEscritura(rol);
        Services.AddScoped<IMediator>(_ => new MediatorConUnaAlerta());
        Services.AddScoped<ToastService>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("alertas");

        var cut = Render<Features.Alertas.Pages.Alertas>();

        cut.Markup.Should().Contain("Juan Pérez", "control: la página está pintada, con su lista");
        cut.FindAll("a").Count(a => a.GetAttribute("href") == "/bandeja").Should().Be(esperados);
    }

    private sealed class MediatorConUnaAlerta : IMediator
    {
        private static readonly IReadOnlyList<AlertaDto> Alertas =
        [
            new(Guid.NewGuid(), Guid.NewGuid(), "Juan Pérez", Guid.NewGuid(), "Reconocimiento médico",
                new DateOnly(2026, 10, 1), EstadoDocumento.Vencido, ArchivoUrl: null, CentroNombre: "Centro Zorrotzaurre")
        ];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerAlertasQuery => Alertas,
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
