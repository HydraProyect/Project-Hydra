using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Commands.EliminarDocumentacionRequeridaCentro;
using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerEstadoCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerProveedoresPlataformaCae;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerUltimaReclamacionCliente;
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
/// «Quitar ajuste» en la pestaña de requisitos del panel de Centro borra la
/// fila propia del centro (baja física de <c>TipoDocumentoCentro</c>): antes
/// salía con un solo clic. Ahora pasa por <see cref="DialogoConfirmacion"/>.
///
/// <para>
/// <b>Lo que SÍ observa:</b> si <see cref="EliminarDocumentacionRequeridaCentroCommand"/>
/// llega al mediador, y con qué tipo de documento, según se cancele o se
/// confirme; y que el diálogo avisa de que no se puede deshacer.
/// <b>Lo que NO observa:</b> la autorización del Command (Application.Tests)
/// ni el doble clic dentro del diálogo (<c>DialogoConfirmacionTests</c>).
/// </para>
/// </summary>
public class CentroWorkspacePanelQuitarAjusteTests : BunitContext
{
    private static readonly Guid TipoDocumentoId = Guid.NewGuid();

    public CentroWorkspacePanelQuitarAjusteTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
    }

    private sealed class MediatorFalso(CentroDetalleDto detalle) : IMediator
    {
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            object? valor = request switch
            {
                ObtenerCentroPorIdQuery => detalle,
                ObtenerUltimaReclamacionClienteQuery => null,
                ObtenerLoteReclamacionQuery => Array.Empty<LoteReclamacionClienteDto>(),
                ObtenerEstadoCentroQuery => null,
                ObtenerCanalesGestionDeCentroQuery => (IReadOnlyList<CanalGestionResumenDto>)[],
                ObtenerProveedoresPlataformaCaeQuery => (IReadOnlyList<ProveedorPlataformaCaeListaDto>)[],
                // Un requisito con ajuste propio del centro (Incluido no nulo): es el que ofrece «Quitar ajuste».
                ObtenerDocumentacionRequeridaDeCentroQuery => (IReadOnlyList<DocumentacionRequeridaCentroDto>)
                [
                    new DocumentacionRequeridaCentroDto(TipoDocumentoId, "Reconocimiento médico", AmbitoAplicacion.Trabajador,
                        EsObligatorioGlobal: true, Aplica: false, Incluido: false, PeriodicidadEspecialMeses: null,
                        BloqueaAcceso: false, ArchivoUrl: null, NombreArchivoOriginal: null)
                ],
                EliminarDocumentacionRequeridaCentroCommand => Result.Exito(),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)valor!);
        }

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

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("GestorCae");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class ResolucionProveedorQueNadieDebeTocar : IResolucionProveedorPlataformaCaeService
    {
        private static Exception NoDeberia() => new NotSupportedException("Este test no resuelve proveedores.");
        public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorUrlAsync(string url, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorDominioCorreoAsync(string email, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() => new NotSupportedException("Este test no sube archivos.");
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private MediatorFalso _mediador = default!;

    private IEnumerable<EliminarDocumentacionRequeridaCentroCommand> Eliminaciones =>
        _mediador.Enviadas.OfType<EliminarDocumentacionRequeridaCentroCommand>();

    private IRenderedComponent<CentroWorkspacePanel> RenderizarRequisitos()
    {
        var centroId = Guid.NewGuid();
        _mediador = new MediatorFalso(new CentroDetalleDto(
            centroId, Guid.NewGuid(), "Refrielectric S.A.", Guid.NewGuid(), "Montajes Ebro S.L.",
            "Centro Logístico Norte", "C-001", null, null, null, Guid.NewGuid()));
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IResolucionProveedorPlataformaCaeService, ResolucionProveedorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddLocalization();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/centros?ficha=norte&pestana=requisitos");

        var cut = Render<CentroWorkspacePanel>(p => p
            .Add(c => c.EntidadId, centroId)
            .Add(c => c.PestanaActiva, "requisitos"));
        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Quitar ajuste"));
        return cut;
    }

    private static IElement BotonDeLaTarjeta(IRenderedComponent<CentroWorkspacePanel> cut) =>
        cut.FindAll(".workspace-tarjeta-requisito-acciones button").Single(b => b.TextContent.Trim() == "Quitar ajuste");

    private static IElement BotonDelDialogo(IRenderedComponent<CentroWorkspacePanel> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == texto);

    [Fact]
    public async Task Quitar_ajuste_pide_confirmacion_y_cancelar_no_envia_el_Command()
    {
        var cut = RenderizarRequisitos();

        await BotonDeLaTarjeta(cut).ClickAsync(new MouseEventArgs());

        cut.Find(".modal-pie").Should().NotBeNull("control positivo: el diálogo se abrió");
        cut.Markup.Should().Contain("¿Quitar el ajuste de «Reconocimiento médico»?").And.Contain("No se puede deshacer");
        Eliminaciones.Should().BeEmpty("abrir el diálogo no borra el ajuste");

        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        Eliminaciones.Should().BeEmpty();
        cut.FindAll(".modal-pie").Should().BeEmpty("cancelar cierra el diálogo");
    }

    [Fact]
    public async Task Quitar_ajuste_y_confirmar_envia_el_Command_de_ese_requisito()
    {
        var cut = RenderizarRequisitos();

        await BotonDeLaTarjeta(cut).ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Quitar ajuste").ClickAsync(new MouseEventArgs());

        Eliminaciones.Should().ContainSingle().Which.TipoDocumentoId.Should().Be(TipoDocumentoId);
        Services.GetRequiredService<ToastService>().Mensajes.Should()
            .Contain(t => t.Mensaje.Contains("vuelve a seguir el criterio global", StringComparison.Ordinal));
        cut.FindAll(".modal-pie").Should().BeEmpty();
    }
}
