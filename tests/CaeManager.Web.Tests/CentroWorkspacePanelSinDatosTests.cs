using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerEstadoCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerProveedoresPlataformaCae;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerUltimaReclamacionCliente;
using CaeManager.Domain.Centros;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// D-17 en la vista previa (panel lateral) de un Centro: sin cumplimiento medido (ningún
/// Trabajador×TipoDocumento obligatorio aplicable) el badge dice «Sin datos», no «Vigente»; con cumplimiento
/// medido sigue diciendo «Vigente», y un estado distinto de Vigente no cambia nunca. Mismo cálculo que la lista
/// y la ficha (<c>EstadoCentroUi.Texto(estado, cumplimiento)</c>).
///
/// <para>
/// Con el mismo arnés, el defecto C4 del piloto Outbound (2026-10-08): la nota «Última reclamación a este Cliente
/// empresarial» enlazaba a Comunicaciones aunque el módulo estuviera apagado (<c>Comunicaciones:Activo</c>), y con él
/// apagado esa página responde «no encontrado». Mismo criterio que la entrada del menú.
/// </para>
/// </summary>
public class CentroWorkspacePanelSinDatosTests : BunitContext
{
    public CentroWorkspacePanelSinDatosTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
    }

    private sealed class MediatorFalso(CentroDetalleDto detalle, EstadoCentroDto estado, UltimaReclamacionClienteDto? ultimaReclamacion) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object? valor = request switch
            {
                ObtenerCentroPorIdQuery => detalle,
                ObtenerUltimaReclamacionClienteQuery => ultimaReclamacion,
                ObtenerLoteReclamacionQuery => Array.Empty<LoteReclamacionClienteDto>(),
                ObtenerEstadoCentroQuery => estado,
                ObtenerCanalesGestionDeCentroQuery => (IReadOnlyList<CanalGestionResumenDto>)[],
                ObtenerProveedoresPlataformaCaeQuery => (IReadOnlyList<ProveedorPlataformaCaeListaDto>)[],
                ObtenerDocumentacionRequeridaDeCentroQuery => (IReadOnlyList<DocumentacionRequeridaCentroDto>)[],
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

    private IRenderedComponent<CentroWorkspacePanel> Montar(
        EstadoCentro estado, int? cumplimiento, UltimaReclamacionClienteDto? ultimaReclamacion = null)
    {
        var centroId = Guid.NewGuid();
        var detalle = new CentroDetalleDto(
            centroId, Guid.NewGuid(), "Refrielectric S.A.", Guid.NewGuid(), "Montajes Ebro S.L.",
            "Centro Logístico Norte", "C-001", null, null, null, Guid.NewGuid());
        Services.AddScoped<IMediator>(_ => new MediatorFalso(detalle, new EstadoCentroDto(estado, [], cumplimiento), ultimaReclamacion));
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IResolucionProveedorPlataformaCaeService, ResolucionProveedorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddLocalization();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/centros?ficha=norte&pestana=informacion");

        var cut = Render<CentroWorkspacePanel>(p => p
            .Add(c => c.EntidadId, centroId)
            .Add(c => c.PestanaActiva, "informacion"));
        cut.WaitForAssertion(() => cut.Find(".workspace-cabecera-entidad .badge"));
        return cut;
    }

    private string TextoDelBadgeDeEstado(EstadoCentro estado, int? cumplimiento) =>
        Montar(estado, cumplimiento).Find(".workspace-cabecera-entidad .badge").TextContent.Trim();

    [Theory]
    [InlineData(EstadoCentro.Vigente, null, "Sin datos")]
    [InlineData(EstadoCentro.Vigente, 100, "Vigente")]
    [InlineData(EstadoCentro.Vigente, 0, "Vigente")]
    [InlineData(EstadoCentro.Vencido, null, "Vencido")]
    [InlineData(EstadoCentro.Faltante, null, "Pendiente")]
    public void El_badge_del_panel_dice_Sin_datos_solo_si_esta_Vigente_sin_cumplimiento_medido(
        EstadoCentro estado, int? cumplimiento, string esperado)
    {
        TextoDelBadgeDeEstado(estado, cumplimiento).Should().Be(esperado);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Ver_en_Comunicaciones_solo_se_ofrece_con_el_modulo_activo(bool activo, int esperados)
    {
        Services.AddSingleton<IOptions<ComunicacionesOptions>>(Options.Create(new ComunicacionesOptions { Activo = activo }));

        var cut = Montar(EstadoCentro.Vencido, cumplimiento: null,
            new UltimaReclamacionClienteDto(Guid.NewGuid(), DateTime.UtcNow.AddDays(-2), 3, ConversacionId: Guid.NewGuid()));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Última reclamación a este Cliente empresarial",
            "control: la nota de la última reclamación, con hilo, está pintada"));
        cut.FindAll("a").Count(a => a.TextContent.Trim() == "Ver en Comunicaciones").Should().Be(esperados);
    }
}
