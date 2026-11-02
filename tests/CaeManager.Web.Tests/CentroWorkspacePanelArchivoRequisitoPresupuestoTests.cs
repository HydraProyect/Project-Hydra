using System.Reflection;
using System.Text;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerEstadoCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerUltimaReclamacionCliente;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Hallazgo de la auditoría del 2026-11-02 (OWASP API4:2023, ASVS 4.0.3 V12.1.1): el adjunto de los Requisitos del Centro
/// se copiaba a un <c>MemoryStream</c> y a un <c>byte[]</c> sin reservar del presupuesto de memoria de documentos, que se
/// comparte entre Tenants (hasta 10 MiB, unos 26 en vuelo). Ahora la lectura reserva antes, y devuelve la reserva antes
/// de guardar, porque el almacenamiento reserva por su cuenta.
/// </summary>
public class CentroWorkspacePanelArchivoRequisitoPresupuestoTests : BunitContext
{
    private readonly PresupuestoMemoriaDocumentos _presupuesto = new(
        Options.Create(new OpcionesPresupuestoMemoriaDocumentos { MaximoBytes = 1_000_000, EsperaMaxima = TimeSpan.FromMilliseconds(50) }),
        TimeProvider.System);

    private readonly AlmacenRegistrador _almacen;

    public CentroWorkspacePanelArchivoRequisitoPresupuestoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
        _almacen = new AlmacenRegistrador(_presupuesto);
    }

    private sealed class MediatorFalso(CentroDetalleDto detalle) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerCentroPorIdQuery => detalle,
                ObtenerUltimaReclamacionClienteQuery => null,
                ObtenerLoteReclamacionQuery => Array.Empty<LoteReclamacionClienteDto>(),
                ObtenerEstadoCentroQuery => null,
                ObtenerDocumentacionRequeridaDeCentroQuery => (IReadOnlyList<DocumentacionRequeridaCentroDto>)[],
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            })!);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);

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
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class ResolucionProveedorQueNadieDebeTocar : IResolucionProveedorPlataformaCaeService
    {
        private static Exception NoDeberia() => new NotSupportedException("No se visita la pestaña 'plataforma'.");
        public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorUrlAsync(string url, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorDominioCorreoAsync(string email, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    /// <summary>Anota lo que había reservado en el momento de guardar: el guardado real reserva por su cuenta.</summary>
    private sealed class AlmacenRegistrador(PresupuestoMemoriaDocumentos presupuesto) : IFileStorageService
    {
        public int Guardados { get; private set; }
        public long ReservadoAlGuardar { get; private set; } = -1;

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
        {
            Guardados++;
            ReservadoAlGuardar = presupuesto.Reservado;
            return Task.FromResult("blob-requisito");
        }

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private IRenderedComponent<CentroWorkspacePanel> Renderizar()
    {
        var centroId = Guid.NewGuid();
        var detalle = new CentroDetalleDto(
            centroId, Guid.NewGuid(), "Refrielectric S.A.", Guid.NewGuid(), "Montajes Ebro S.L.",
            "Centro Logístico Norte", "C-001", null, null, null, Guid.NewGuid());
        Services.AddScoped<IMediator>(_ => new MediatorFalso(detalle));
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddSingleton<IFileStorageService>(_almacen);
        Services.AddSingleton<IPresupuestoMemoriaDocumentos>(_presupuesto);
        Services.AddScoped<IResolucionProveedorPlataformaCaeService, ResolucionProveedorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddLocalization();

        var cut = Render<CentroWorkspacePanel>(p => p.Add(c => c.EntidadId, centroId).Add(c => c.PestanaActiva, "requisitos"));
        cut.WaitForAssertion(() => cut.Markup.Should().NotBeEmpty());
        return cut;
    }

    private static Task ElegirArchivoAsync(IRenderedComponent<CentroWorkspacePanel> cut, ArchivoNavegadorFalso archivo)
    {
        var manejador = typeof(CentroWorkspacePanel)
            .GetMethod("ManejarArchivoDocumentacionSeleccionadoAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return cut.InvokeAsync(() => (Task)manejador.Invoke(cut.Instance, [new InputFileChangeEventArgs([archivo])])!);
    }

    private static byte[] Pdf(int longitud)
    {
        var bytes = new byte[longitud];
        Encoding.ASCII.GetBytes("%PDF-1.4\n").CopyTo(bytes, 0);
        return bytes;
    }

    private IReadOnlyList<ToastMensaje> Toasts => Services.GetRequiredService<ToastService>().Mensajes;

    [Fact]
    public async Task La_lectura_reserva_antes_de_abrir_el_flujo_y_devuelve_la_reserva_antes_de_guardar()
    {
        var cut = Renderizar();
        long reservadoAlAbrir = -1;
        var archivo = new ArchivoNavegadorFalso("requisito.pdf", Pdf(2_000)) { AlAbrir = () => reservadoAlAbrir = _presupuesto.Reservado };

        await ElegirArchivoAsync(cut, archivo);

        reservadoAlAbrir.Should().Be(2_000L * ArchivoSubidoReservadoFactor, "la reserva existe antes de leer un solo byte");
        _almacen.Guardados.Should().Be(1);
        _almacen.ReservadoAlGuardar.Should().Be(0, "el almacenamiento reserva por su cuenta: la de la lectura no puede esperarla");
        _presupuesto.Reservado.Should().Be(0);
    }

    private const int ArchivoSubidoReservadoFactor = CaeManager.Web.Documentos.ArchivoSubidoReservado.FactorMemoriaLectura;

    [Fact]
    public async Task Sin_hueco_avisa_con_el_mensaje_del_presupuesto_sin_leer_ni_guardar_nada()
    {
        var cut = Renderizar();
        using var ocupado = await _presupuesto.ReservarAsync(_presupuesto.Maximo - 100);
        var archivo = new ArchivoNavegadorFalso("requisito.pdf", Pdf(2_000));

        await ElegirArchivoAsync(cut, archivo);

        Toasts.Should().ContainSingle(t => t.Mensaje == PresupuestoMemoriaAgotadoException.MensajeUsuario && t.Tono == TonoToast.Error);
        archivo.Aperturas.Should().Be(0, "sin reserva no se lee");
        _almacen.Guardados.Should().Be(0);
        _presupuesto.Reservado.Should().Be(ocupado.Bytes);
    }

    [Fact]
    public async Task Un_archivo_cuyo_contenido_no_es_un_documento_devuelve_la_reserva()
    {
        var cut = Renderizar();
        var archivo = new ArchivoNavegadorFalso("requisito.pdf", new byte[2_000]);

        await ElegirArchivoAsync(cut, archivo);

        Toasts.Should().Contain(t => t.Tono == TonoToast.Error);
        _almacen.Guardados.Should().Be(0);
        _presupuesto.Reservado.Should().Be(0);
    }
}
