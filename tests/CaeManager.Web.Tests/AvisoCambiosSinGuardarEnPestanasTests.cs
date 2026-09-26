using System.Reflection;
using Bunit;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerEmpresasConSelloGuardado;
using CaeManager.Application.Documentos.Queries.ObtenerFirmaGuardadaUsuario;
using CaeManager.Application.Documentos.Queries.ObtenerFirmasEnCampoDocumento;
using CaeManager.Application.Plantillas.Commands.AgregarVersionPlantilla;
using CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Application.Plantillas.Queries.ObtenerTotalDocumentosGeneradosConAvisos;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b (a): los formularios que viven dentro de una pestaña y se pierden al cambiarla
/// (inventario medido): el contacto de agenda (pestaña Agenda de las fichas y reclamación
/// de /documentos), la firma en campo (pestaña Firma del panel de Documento; congelada por
/// D-1, solo el patrón) y la nueva versión de una plantilla (pestaña Plantillas). Llevan su
/// AvisoCambiosSinGuardar, que se registra en el ámbito de las pestañas (lo prueba
/// <see cref="PestanasAvisoCambiosSinGuardarTests"/>) y además frena la salida de la página.
/// </summary>
public class AvisoCambiosSinGuardarEnPestanasTests : BunitContext
{
    private sealed class MediatorFalso : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)((object?)(request switch
            {
                ObtenerTiposDocumentoQuery => (object)(IReadOnlyList<TipoDocumentoListaDto>)[],
                ObtenerDocumentoPorIdQuery => null,
                ObtenerFirmasEnCampoDocumentoQuery => (IReadOnlyList<FirmaEnCampoDocumentoDto>)[],
                ObtenerFirmaGuardadaUsuarioQuery => null,
                ObtenerEmpresasConSelloGuardadoQuery => (IReadOnlyList<EmpresaConSelloDto>)[],
                ObtenerPlantillasDocumentoQuery => (IReadOnlyList<PlantillaDocumentoListaDto>)[],
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[],
                ObtenerTotalDocumentosGeneradosConAvisosQuery => 0,
                ObtenerDocumentosGeneradosQuery => (IReadOnlyList<DocumentoGeneradoListaDto>)[],
                AgregarVersionPlantillaCommand => CaeManager.Domain.Common.Result.Exito(new AgregarVersionPlantillaResultadoDto(VersionSubidaId, false)),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }))!);

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

    public AvisoCambiosSinGuardarEnPestanasTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => new MediatorFalso());
        Services.AddScoped<ToastService>();
        this.ConRolDeEscritura();
    }

    private static readonly Guid VersionSubidaId = Guid.NewGuid();

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private bool _contactoVisible = true;

    private IRenderedComponent<ModalContactoAgenda> RenderizarContacto() =>
        Render<ModalContactoAgenda>(p => p
            .Add(x => x.Visible, _contactoVisible)
            .Add(x => x.VisibleChanged, v => _contactoVisible = v)
            .Add(x => x.Tipo, TipoPropietarioAgenda.Cliente)
            .Add(x => x.PropietarioId, Guid.NewGuid()));

    [Fact]
    public async Task Contacto_de_agenda_a_medias_pregunta_al_salir_y_al_cerrar_con_la_X()
    {
        var cut = RenderizarContacto();
        await cut.FindComponents<CampoTexto>().First(c => c.Instance.Etiqueta == "Nombre").Find("input")
            .InputAsync(new ChangeEventArgs { Value = "Marta Ruiz" });

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
        await cut.PulsarEnElAvisoAsync("Seguir editando");

        await cut.Find(".modal-cerrar").ClickAsync(new MouseEventArgs());
        _contactoVisible.Should().BeTrue("la X con cambios pregunta «¿Descartar cambios?» antes de cerrar");
        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?");
    }

    [Fact]
    public async Task Contacto_de_agenda_sin_tocar_no_pregunta()
    {
        var cut = RenderizarContacto();

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "abrir el contacto sin escribir no deja nada que perder");
    }

    [Fact]
    public async Task Firma_en_campo_con_trazo_sin_firmar_pregunta_al_salir()
    {
        var cut = Render<FirmaEnCampoTab>(p => p.Add(x => x.EntidadId, Guid.NewGuid()));
        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "sin trazo no hay nada que perder");

        await cut.InvokeAsync(() => cut.Instance.MarcarTrazoIniciadoAsync());

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Nueva_version_de_plantilla_con_PDF_elegido_pregunta_al_salir()
    {
        var cut = Render<PlantillasTab>();
        await cut.InvokeAsync(() =>
        {
            Escribir(cut.Instance, "_plantillaParaNuevaVersionId", (Guid?)Guid.NewGuid());
            Escribir(cut.Instance, "_archivoNuevaVersion", new byte[] { 0x25, 0x50, 0x44, 0x46 });
        });
        // Elegir el archivo de verdad exige InputFile (UploadFiles de bUnit bloquea con un
        // NavigationLock montado); escribir los campos no renderiza por sí solo.
        cut.Render();

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    /// <summary>Revisión Codex (PR 4): subir la nueva versión lleva al editor sin preguntar: ya no queda nada pendiente.</summary>
    [Fact]
    public async Task Subir_la_nueva_version_lleva_al_editor_sin_preguntar()
    {
        var cut = Render<PlantillasTab>();
        await cut.InvokeAsync(() =>
        {
            Escribir(cut.Instance, "_plantillaParaNuevaVersionId", (Guid?)Guid.NewGuid());
            Escribir(cut.Instance, "_archivoNuevaVersion", new byte[] { 0x25, 0x50, 0x44, 0x46 });
        });
        cut.Render();

        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Subir y configurar").ClickAsync(new MouseEventArgs());

        Navegacion.Uri.Should().EndWith($"/plantillas/{VersionSubidaId}/editar");
        cut.FindAll("h2").Should().NotContain(h => h.TextContent.Trim() == "¿Salir sin guardar?");
    }

    private static void Escribir(object instancia, string campo, object? valor)
    {
        var info = instancia.GetType().GetField(campo, BindingFlags.Instance | BindingFlags.NonPublic);
        info.Should().NotBeNull($"el test necesita escribir {campo} directamente");
        info!.SetValue(instancia, valor);
    }
}
