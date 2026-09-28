using System.Reflection;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculosParaSelector;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// «+ Subir documento» en la pestaña Documentación de Vehículo 360 (revisión UX
/// pre-piloto 2026-09-28, D.1): abre el mismo DrawerGestionDocumento que el resto
/// de pantallas, con el vehículo preseleccionado. Solo lo pide Vehículo 360
/// (PermiteSubir) y solo con escritura.
/// </summary>
public class PestanaDocumentacionSubirDocumentoTests : BunitContext
{
    private sealed class MediatorFalso(Guid vehiculoId) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerDocumentosQuery => new ResultadoPaginado<DocumentoListaDto>([], 0, 1, 50),
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[],
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)[],
                ObtenerVehiculosParaSelectorQuery => (IReadOnlyList<VehiculoSelectorDto>)[new VehiculoSelectorDto(vehiculoId, "Furgoneta", "1234ABC")],
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private readonly Guid _vehiculoId = Guid.NewGuid();

    private IRenderedComponent<PestanaDocumentacion> Renderizar(AmbitoAplicacion ambito, bool permiteSubir, bool conEscritura = true)
    {
        Services.AddScoped<IMediator>(_ => new MediatorFalso(_vehiculoId));
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddLocalization();
        if (conEscritura)
            this.ConRolDeEscritura();
        else
            AddAuthorization().SetAuthorized("consulta@prueba.es").SetRoles(CaeManager.Infrastructure.Identity.Roles.Consulta);

        return Render<PestanaDocumentacion>(p => p
            .Add(x => x.Ambito, ambito)
            .Add(x => x.PropietarioId, _vehiculoId)
            .Add(x => x.PermiteSubir, permiteSubir));
    }

    private static List<AngleSharp.Dom.IElement> BotonesSubir(IRenderedComponent<PestanaDocumentacion> cut) =>
        cut.FindAll("button").Where(b => b.TextContent.Trim() == "+ Subir documento").ToList();

    [Fact]
    public async Task En_Vehiculo_abre_el_alta_con_el_vehiculo_preseleccionado()
    {
        var cut = Renderizar(AmbitoAplicacion.Vehiculo, permiteSubir: true);

        await BotonesSubir(cut).Should().ContainSingle().Subject.ClickAsync(new MouseEventArgs());

        var drawer = cut.FindComponent<DrawerGestionDocumento>().Instance;
        Campo(drawer, "_drawerVisible").Should().Be(true);
        Campo(drawer, "_ambitoAplicacion").Should().Be(nameof(AmbitoAplicacion.Vehiculo));
        Campo(drawer, "_vehiculoId").Should().Be(_vehiculoId.ToString());
    }

    [Fact]
    public void Sin_PermiteSubir_no_se_ofrece()
    {
        var cut = Renderizar(AmbitoAplicacion.Vehiculo, permiteSubir: false);

        cut.Find(".estado-vacio").Should().NotBeNull("control del instrumento: la pestaña se ha pintado");
        BotonesSubir(cut).Should().BeEmpty();
        cut.FindComponents<DrawerGestionDocumento>().Should().BeEmpty();
    }

    [Fact]
    public void Sin_escritura_no_se_ofrece()
    {
        var cut = Renderizar(AmbitoAplicacion.Vehiculo, permiteSubir: true, conEscritura: false);

        cut.Find(".estado-vacio").Should().NotBeNull("control del instrumento: la pestaña se ha pintado");
        BotonesSubir(cut).Should().BeEmpty("crear un documento es un ICommand que se deniega a Consulta");
    }

    private static object? Campo(DrawerGestionDocumento instancia, string nombre) =>
        typeof(DrawerGestionDocumento).GetField(nombre, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instancia);
}
