using Bunit;
using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b (lote A): las Asignaciones de un Centro. La baja en lote solo pide una fecha, que
/// llega puesta a hoy: dejarla así no es un cambio, cambiarla sí. El alta N×M llega con la
/// fecha de hoy y, si la pantalla los trae, con centros ya marcados: eso tampoco es un
/// cambio; marcar trabajadores, centros o celdas sí. Sus pestañas Lista/Matriz son dos
/// vistas del mismo formulario: pasar de una a otra no pregunta.
/// </summary>
public class AsignacionesCentroAvisoCambiosSinGuardarTests : BunitContext
{
    private static readonly Guid TrabajadorId = Guid.NewGuid();
    private static readonly Guid CentroId = Guid.NewGuid();

    public AsignacionesCentroAvisoCambiosSinGuardarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => new MediatorFalso());
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    }

    private sealed class MediatorFalso : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerAsignacionesDocumentacionPorCentroQuery => (IReadOnlyList<TrabajadorAsignacionDocumentacionDto>)
                [
                    new TrabajadorAsignacionDocumentacionDto(Guid.NewGuid(), TrabajadorId, "Ruiz Peña, Ana",
                        new DateOnly(2026, 1, 15), EstadoDocumento.Vigente, [])
                ],
                ObtenerTrabajadoresParaSelectorQuery => new[] { TrabajadorSelectorFalso.Crear(TrabajadorId, "Bea Alonso Ruiz") },
                ObtenerCentrosParaSelectorQuery => new[] { new CentroSelectorDto(CentroId, "Planta Zaragoza", "Refrielectric S.A.", "Montajes Ebro S.L.") },
                ObtenerDocumentosFaltantesParaAsignacionQuery => (IReadOnlyList<DocumentoFaltanteDto>)[],
                CrearAsignacionesCommand => Result.Exito(new ResultadoAsignacionLoteDto(1, 0, 0, [])),
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

    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() => new NotSupportedException("Este test no abre ni sube archivos.");
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Este test no convierte nada.");
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private async Task<IRenderedComponent<AcordeonAsignacionesCentro>> AbrirBajaEnLoteAsync()
    {
        Navegacion.NavigateTo("/centros");
        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, CentroId)
            .Add(a => a.CentroNombre, "Planta Zaragoza")
            .Add(a => a.SeleccionMultiple, true));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));

        await cut.Find("input[aria-label^='Seleccionar a Ruiz Peña, Ana']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Dar de baja seleccionados").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Etiqueta == "Fecha de baja"));
        return cut;
    }

    private static Task CambiarFechaDeBajaAsync(IRenderedComponent<AcordeonAsignacionesCentro> cut) =>
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de baja").Find("input")
            .InputAsync(new ChangeEventArgs { Value = "2026-12-31" });

    private async Task<(IRenderedComponent<DrawerAsignacionMasiva> Cut, DrawerAsignacionMasiva Drawer)> AbrirAltaMasivaAsync()
    {
        Navegacion.NavigateTo("/centros");
        var cut = Render<DrawerAsignacionMasiva>(p => p.Add(d => d.OnGuardado, () => Task.CompletedTask));
        // Es su entrada real: los hosts (acordeón de un Centro, lista de Centros) llaman a AbrirAsync.
        await cut.InvokeAsync(() => cut.Instance.AbrirAsync([CentroId]));
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        return (cut, cut.Instance);
    }

    private static Task MarcarTrabajadorAsync(IRenderedComponent<DrawerAsignacionMasiva> cut)
    {
        var selector = cut.FindComponents<SelectorMultiple>().Single(c => c.Instance.Etiqueta == "Trabajadores");
        return cut.InvokeAsync(() => selector.Instance.OnAlternar.InvokeAsync((TrabajadorId, true)));
    }

    [Fact]
    public async Task Baja_en_lote_con_la_fecha_de_hoy_sin_tocar_no_pregunta()
    {
        var cut = await AbrirBajaEnLoteAsync();

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "la fecha llega puesta a hoy: dejarla así no es un cambio");
    }

    [Fact]
    public async Task Baja_en_lote_con_otra_fecha_pregunta_al_salir_y_al_cerrar_con_la_X()
    {
        var cut = await AbrirBajaEnLoteAsync();
        await CambiarFechaDeBajaAsync(cut);

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
        await cut.PulsarEnElAvisoAsync("Seguir editando");

        await cut.Find(".modal-contenido button.modal-cerrar").ClickAsync(new MouseEventArgs());
        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?");
    }

    [Fact]
    public async Task Alta_masiva_con_el_centro_preseleccionado_y_la_fecha_de_hoy_no_pregunta()
    {
        var (cut, _) = await AbrirAltaMasivaAsync();
        cut.FindComponents<SelectorMultiple>().Single(c => c.Instance.Etiqueta == "Centros").Instance.Seleccionados
            .Should().Contain(CentroId, "el test necesita que el centro llegue preseleccionado");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "lo que la pantalla trae marcado no es un cambio de quien edita");
    }

    [Fact]
    public async Task Alta_masiva_con_un_trabajador_marcado_pregunta_al_salir_y_al_cerrar_con_la_X()
    {
        var (cut, _) = await AbrirAltaMasivaAsync();
        await MarcarTrabajadorAsync(cut);

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
        await cut.PulsarEnElAvisoAsync("Seguir editando");

        await cut.Find(".drawer-panel button.drawer-cerrar").ClickAsync(new MouseEventArgs());
        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?");
    }

    [Fact]
    public async Task Alta_masiva_pasar_a_la_matriz_no_pregunta_ni_pierde_lo_marcado()
    {
        var (cut, _) = await AbrirAltaMasivaAsync();
        await MarcarTrabajadorAsync(cut);

        // Sin await: si el aviso quedara dentro de las pestañas Lista/Matriz, el clic se quedaría
        // esperando una respuesta a la pregunta; así el fallo es rojo y no un cuelgue.
        _ = cut.FindAll("button[role=tab]").Single(b => b.TextContent.Trim() == "Matriz").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll(".matriz-asignaciones").Should().ContainSingle("la vista cambió a la matriz sin preguntar"));
        cut.FindAll(".modal-pie button").Should().NotContain(b => b.TextContent.Trim() == "Salir y descartar");
    }

    [Fact]
    public async Task Alta_masiva_guardada_no_deja_nada_pendiente()
    {
        var (cut, _) = await AbrirAltaMasivaAsync();
        await MarcarTrabajadorAsync(cut);

        await cut.Find(".drawer-pie button:last-child").ClickAsync(new MouseEventArgs());

        cut.FindAll(".drawer-panel").Should().BeEmpty("barrera: el alta se guardó y el drawer se cerró");
        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "cerrado tras guardar no hay nada que perder");
    }
}
