using Bunit;
using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El preflight del drawer N×M es una lectura aparte (<c>ObtenerDocumentosFaltantesParaAsignacionQuery</c>),
/// hecha al marcar trabajadores o centros — <c>CrearAsignacionesCommand</c> ni
/// la repite ni se detiene por documentos (no recalcula, no bloquea). El aviso
/// tiene que hablar de lo que faltaba al comprobarlo, no prometer qué
/// «quedará» tras guardar, y de configuración («se pide»), no de una
/// obligación legal.
/// </summary>
public class DrawerAsignacionMasivaFaltantesTests : BunitContext
{
    public DrawerAsignacionMasivaFaltantesTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid TrabajadorId = Guid.NewGuid();
    private static readonly Guid CentroId = Guid.NewGuid();

    private sealed class MediatorFalso : IMediator
    {
        public IReadOnlyList<DocumentoFaltanteDto> Faltantes { get; set; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerTrabajadoresParaSelectorQuery => (object)new[] { new TrabajadorSelectorDto(TrabajadorId, "Bea Alonso Ruiz", "12345678A", null) },
                ObtenerCentrosParaSelectorQuery => new[] { new CentroSelectorDto(CentroId, "Planta Zaragoza", "Refrielectric S.A.", "Montajes Ebro S.L.") },
                ObtenerDocumentosFaltantesParaAsignacionQuery => Faltantes,
                CrearAsignacionesCommand => Result.Exito(new ResultadoAsignacionLoteDto(0, 0, 0, [])),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
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

    private (IRenderedComponent<DrawerAsignacionMasiva> Cut, DrawerAsignacionMasiva Componente) Renderizar(MediatorFalso mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();

        DrawerAsignacionMasiva? componente = null;
        var cut = Render<DrawerAsignacionMasiva>(p => p.Add(d => d.OnGuardado, () => Task.CompletedTask));
        componente = cut.Instance;
        return (cut, componente);
    }

    private static async Task MarcarTrabajadorYCentroAsync(IRenderedComponent<DrawerAsignacionMasiva> cut)
    {
        var selectorTrabajadores = cut.FindComponents<SelectorMultiple>().Single(c => c.Instance.Etiqueta == "Trabajadores");
        var selectorCentros = cut.FindComponents<SelectorMultiple>().Single(c => c.Instance.Etiqueta == "Centros");

        await cut.InvokeAsync(() => selectorTrabajadores.Instance.OnAlternar.InvokeAsync((TrabajadorId, true)));
        await cut.InvokeAsync(() => selectorCentros.Instance.OnAlternar.InvokeAsync((CentroId, true)));
    }

    [Fact]
    public async Task El_aviso_de_faltantes_dice_lo_que_faltaba_al_comprobarlo_sin_hablar_de_obligacion_ni_de_lo_que_quedara()
    {
        var mediador = new MediatorFalso
        {
            Faltantes = [new DocumentoFaltanteDto(TrabajadorId, "Bea Alonso Ruiz", CentroId, "Planta Zaragoza", Guid.NewGuid(), "Formación PRL específica")]
        };
        var (cut, componente) = Renderizar(mediador);
        await cut.InvokeAsync(() => componente.AbrirAsync());

        await MarcarTrabajadorYCentroAsync(cut);

        var aviso = string.Join(' ', cut.Find(".alerta-preflight-asignacion").TextContent
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        aviso.Should().Contain("Al comprobarlo faltaban 1 documento(s) que se piden")
            .And.Contain("Bea Alonso Ruiz — Formación PRL específica (Planta Zaragoza)")
            .And.Contain("Guardar no crea esos documentos, y que falten no impide el alta.")
            .And.NotContain("quedarán sin", "la lectura previa no sabe qué quedará al confirmar")
            .And.NotContainEquivalentOf("obligatori", "es configuración (se pide), no una obligación legal");
        cut.Find(".drawer-pie button:last-child").TextContent.Trim().Should().Be("Asignar igualmente");
    }

    [Fact]
    public async Task Sin_faltantes_no_pinta_ningun_aviso()
    {
        var mediador = new MediatorFalso { Faltantes = [] };
        var (cut, componente) = Renderizar(mediador);
        await cut.InvokeAsync(() => componente.AbrirAsync());

        await MarcarTrabajadorYCentroAsync(cut);

        cut.FindAll(".alerta-preflight-asignacion").Should().BeEmpty();
        cut.Find(".drawer-pie button:last-child").TextContent.Trim().Should().Be("Guardar");
    }
}
