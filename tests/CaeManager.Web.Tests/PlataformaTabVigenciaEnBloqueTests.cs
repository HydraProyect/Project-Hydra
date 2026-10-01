using Bunit;
using CaeManager.Application.Documentos.Commands.ConfirmarVigenciaAcreditacion;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// D-23: «Anotar vigencia» en bloque en la pestaña Plataformas CAE. La pantalla
/// no tiene comando propio: despacha ConfirmarVigenciaAcreditacionCommand por
/// cada fila marcada, así que cada una pasa por la misma autorización y el mismo
/// alcance que anotarla de una en una. Observa los comandos despachados y el
/// resumen de la cabecera; no observa la autorización real del handler.
/// </summary>
public sealed class PlataformaTabVigenciaEnBloqueTests : BunitContext
{
    private readonly Mediador _mediador = new();

    public PlataformaTabVigenciaEnBloqueTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
        Services.AddSingleton<IMediator>(_mediador);
        Services.AddSingleton(new ToastService());
        Services.AddLocalization();
    }

    [Fact]
    public void La_cabecera_cuenta_las_aceptadas_sin_vigencia()
    {
        var cut = Render<PlataformaTab>();

        cut.Find(".plataforma-proveedor-resumen").TextContent.Should().Contain("2 aceptada(s) sin vigencia");
    }

    [Fact]
    public async Task Anotar_vigencia_en_bloque_manda_el_comando_unitario_por_cada_fila_marcada()
    {
        var cut = Render<PlataformaTab>();

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Seleccionar sin vigencia").ClickAsync(new MouseEventArgs());
        await cut.FindAll("button").Single(b => b.TextContent.Contains("Anotar vigencia (2)")).ClickAsync(new MouseEventArgs());

        var fecha = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de vencimiento");
        await cut.InvokeAsync(() => fecha.Instance.ValorChanged.InvokeAsync("2027-03-01"));
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Guardar vigencia").ClickAsync(new MouseEventArgs());

        var comandos = _mediador.Recibidas.OfType<ConfirmarVigenciaAcreditacionCommand>().ToList();
        comandos.Should().HaveCount(2);
        comandos.Select(c => c.AcreditacionId).Should().BeEquivalentTo(_mediador.SinVigenciaIds);
        comandos.Should().OnlyContain(c => c.Vigencia.FechaVencimiento == new DateOnly(2027, 3, 1));
    }

    [Fact]
    public void Sin_nada_marcado_el_boton_en_bloque_esta_deshabilitado_y_dice_por_que()
    {
        var cut = Render<PlataformaTab>();

        var boton = cut.FindAll("button").Single(b => b.TextContent.Contains("Anotar vigencia (0)"));
        boton.HasAttribute("disabled").Should().BeTrue();
        boton.GetAttribute("title").Should().NotBeNullOrWhiteSpace();
    }

    private sealed class Mediador : IMediator
    {
        public List<object> Recibidas { get; } = [];
        public List<Guid> SinVigenciaIds { get; } = [];
        private readonly IReadOnlyList<ProveedorAcreditacionesDto> _datos;

        public Mediador()
        {
            var a = Fila("Documento A", EstadoAcreditacion.Aceptada);
            var b = Fila("Documento B", EstadoAcreditacion.Aceptada);
            SinVigenciaIds.AddRange([a.AcreditacionId, b.AcreditacionId]);
            _datos =
            [
                new ProveedorAcreditacionesDto(Guid.NewGuid(), "Dokify", "dokify",
                [
                    new ClienteAcreditacionesDto(Guid.NewGuid(), "Cliente Norte S.A.",
                        [a, b, Fila("Documento C", EstadoAcreditacion.PendienteDeSubir)])
                ])
            ];
        }

        private static AcreditacionDrillDownDto Fila(string tipo, EstadoAcreditacion estado) =>
            new(Guid.NewGuid(), Guid.NewGuid(), "Iker Etxeberria", tipo, estado, null);

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Recibidas.Add(request);
            object valor = request switch
            {
                ObtenerAcreditacionesPorProveedorQuery => _datos,
                ConfirmarVigenciaAcreditacionCommand => Result.Exito(),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult((TResponse)valor);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }
}
