using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// H-D1 del piloto Outbound: tras «Marcar subido» la acreditación quedaba en
/// <c>Subida</c> y la pestaña Plataforma no la volvía a pedir, así que la fila
/// desaparecía y el estado no tenía salida. La pestaña tiene que pedir las
/// subidas y ofrecer sobre ellas solo lo que sigue siendo cierto: anotar la
/// respuesta de la plataforma (aceptada o rechazada), no marcarla subida otra vez.
/// </summary>
public sealed class PlataformaTabAcreditacionesSubidasTests : BunitContext
{
    private readonly Mediador _mediador = new();

    public PlataformaTabAcreditacionesSubidasTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IMediator>(_mediador);
        Services.AddSingleton(new ToastService());
    }

    [Fact]
    public void La_pestana_pide_las_subidas_y_ofrece_solo_aceptar_o_rechazar_sobre_ellas()
    {
        var cut = Render<PlataformaTab>();

        var consulta = _mediador.Recibidas.OfType<ObtenerAcreditacionesPorProveedorQuery>().Should().ContainSingle().Subject;
        consulta.IncluirSubidas.Should().BeTrue("sin pedirlas, la fila desaparece al marcarla subida");

        var filas = cut.FindAll(".plataforma-fila-documento");
        filas.Should().HaveCount(3, "control positivo: pendiente, subida y aceptada");

        BotonesDe(filas, "Documento pendiente").Should().Equal("Marcar subido", "Marcar aceptado", "Marcar rechazado…");
        BotonesDe(filas, "Documento subido").Should().Equal("Marcar aceptado", "Marcar rechazado…");
        BotonesDe(filas, "Documento aceptado").Should().Equal("Anotar vigencia…");
    }

    private static List<string> BotonesDe(IEnumerable<AngleSharp.Dom.IElement> filas, string tipo) =>
        filas.Single(f => f.TextContent.Contains(tipo)).QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ToList();

    private sealed class Mediador : IMediator
    {
        public List<object> Recibidas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Recibidas.Add(request);
            object valor = request switch
            {
                ObtenerAcreditacionesPorProveedorQuery => (IReadOnlyList<ProveedorAcreditacionesDto>)
                [
                    new ProveedorAcreditacionesDto(Guid.NewGuid(), "Dokify", "dokify",
                    [
                        new ClienteAcreditacionesDto(Guid.NewGuid(), "Cliente Norte S.A.",
                        [
                            Fila("Documento pendiente", EstadoAcreditacion.PendienteDeSubir),
                            Fila("Documento subido", EstadoAcreditacion.Subida),
                            Fila("Documento aceptado", EstadoAcreditacion.Aceptada)
                        ])
                    ])
                ],
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult((TResponse)valor);
        }

        private static AcreditacionDrillDownDto Fila(string tipo, EstadoAcreditacion estado) =>
            new(Guid.NewGuid(), Guid.NewGuid(), "Iker Etxeberria", tipo, estado, null);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }
}
