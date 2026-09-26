using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Commands.CrearCanalGestion;
using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerEstadoCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerProveedoresPlataformaCae;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerUltimaReclamacionCliente;
using CaeManager.Domain.Common;
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
/// P1-I2 (hueco declarado en #900): dar de alta un acceso de gestión documental
/// con usuario o contraseña escribe datos de credencial, y exige el mismo 2FA
/// que leerlos. La regla vive en <c>AutorizacionSecretosDeTenantBehavior</c>; el
/// mediador falso la imita. Aquí se prueba qué hace el cajón con la denegación:
/// no anuncia un guardado que no hubo y lleva a activar el 2FA con la vuelta a
/// la ficha del Centro.
/// </summary>
public class CentroWorkspacePanelCanalSegundoFactorTests : BunitContext
{
    private const string FichaCentro = "/centros?ficha=norte&pestana=plataforma";

    public CentroWorkspacePanelCanalSegundoFactorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
    }

    private sealed class MediatorFalso(CentroDetalleDto detalle) : IMediator
    {
        public bool SinDobleFactor { get; set; }
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (SinDobleFactor && request is IEscrituraDeDatosDeCredencial { EscribeDatosDeCredencial: true })
                return Task.FromException<TResponse>(new SegundoFactorRequeridoParaCredencialesException());

            object? valor = request switch
            {
                ObtenerCentroPorIdQuery => detalle,
                ObtenerUltimaReclamacionClienteQuery => null,
                ObtenerLoteReclamacionQuery => Array.Empty<LoteReclamacionClienteDto>(),
                ObtenerEstadoCentroQuery => null,
                ObtenerCanalesGestionDeCentroQuery => (IReadOnlyList<CanalGestionResumenDto>)[],
                ObtenerProveedoresPlataformaCaeQuery => (IReadOnlyList<ProveedorPlataformaCaeListaDto>)[],
                CrearCanalGestionCommand => Result.Exito(Guid.NewGuid()),
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

    /// <summary>La URL queda vacía en estos tests: nadie debe resolver un proveedor.</summary>
    private sealed class ResolucionProveedorQueNadieDebeTocar : IResolucionProveedorPlataformaCaeService
    {
        private static Exception NoDeberia() => new NotSupportedException("Sin URL no debería resolverse ningún proveedor.");
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

    private async Task<(IRenderedComponent<CentroWorkspacePanel> Cut, MediatorFalso Mediador, NavigationManager Navegacion)> AbrirAltaDeAccesoAsync()
    {
        var centroId = Guid.NewGuid();
        var mediador = new MediatorFalso(new CentroDetalleDto(
            centroId, Guid.NewGuid(), "Refrielectric S.A.", Guid.NewGuid(), "Montajes Ebro S.L.",
            "Centro Logístico Norte", "C-001", null, null, null, Guid.NewGuid()));
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IResolucionProveedorPlataformaCaeService, ResolucionProveedorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddLocalization();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo(FichaCentro);

        var cut = Render<CentroWorkspacePanel>(p => p
            .Add(c => c.EntidadId, centroId)
            .Add(c => c.PestanaActiva, "plataforma"));
        cut.WaitForAssertion(() => Boton(cut, "Añadir acceso"));
        await Boton(cut, "Añadir acceso").ClickAsync(new MouseEventArgs());
        await Control(cut, "Para qué sirve este acceso").InputAsync(new ChangeEventArgs { Value = "Subir la documentación del Centro" });
        await Control(cut, "Usuario").InputAsync(new ChangeEventArgs { Value = "norte.prl" });
        return (cut, mediador, navegacion);
    }

    private static IElement Control(IRenderedComponent<CentroWorkspacePanel> cut, string etiqueta) =>
        cut.Find("#" + cut.FindAll("label").Single(l => l.TextContent.Trim() == etiqueta).GetAttribute("for"));

    private static IElement Boton(IRenderedComponent<CentroWorkspacePanel> cut, string texto) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == texto);

    private IReadOnlyList<ToastMensaje> Toasts => Services.GetRequiredService<ToastService>().Mensajes;

    [Fact]
    public async Task Sin_2FA_dar_de_alta_un_acceso_con_usuario_no_se_da_por_hecho_y_lleva_a_activarlo()
    {
        var (cut, mediador, navegacion) = await AbrirAltaDeAccesoAsync();
        mediador.SinDobleFactor = true;

        await Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearCanalGestionCommand>().Should().ContainSingle(
            "barrera: el comando se envió y fue la denegación la que respondió").Which.Usuario.Should().Be("norte.prl");
        navegacion.Uri.Should().Be(navegacion.BaseUri.TrimEnd('/')
            + "/cuenta/configurar-2fa?motivo=credenciales&returnUrl=" + Uri.EscapeDataString(FichaCentro));
        Toasts.Should().NotContain(t => t.Tono == TonoToast.Exito);
    }

    [Fact]
    public async Task Con_2FA_dar_de_alta_un_acceso_con_usuario_se_guarda_sin_salir_de_la_ficha()
    {
        var (cut, mediador, navegacion) = await AbrirAltaDeAccesoAsync();

        await Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearCanalGestionCommand>().Should().ContainSingle().Which.Usuario.Should().Be("norte.prl");
        navegacion.Uri.Should().Be(navegacion.BaseUri.TrimEnd('/') + FichaCentro, "control positivo del test de arriba");
        Toasts.Should().Contain(t => t.Tono == TonoToast.Exito);
    }
}
