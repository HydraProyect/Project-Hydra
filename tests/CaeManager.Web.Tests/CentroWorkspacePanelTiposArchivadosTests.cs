using Bunit;
using CaeManager.Application.Centros.Commands.EliminarDocumentacionRequeridaCentro;
using CaeManager.Application.Centros.Commands.EstablecerDocumentacionRequeridaCentro;
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
/// Pestaña «Requisitos» del panel de Centro y los tipos de documento archivados (revisión del catálogo del 2026-10-17):
/// marcar uno como requerido lo rechaza el Command («exige su sustituto»), así que no se ofrece; el que el Centro YA exige
/// sí se muestra, con el nombre de su sustituto, y se puede desmarcar.
///
/// <para>
/// <b>Lo que SÍ observa:</b> qué tarjetas pinta el panel según lo que devuelve la consulta, el texto «sustituido por …» y
/// el Command que envía al desmarcar. <b>Lo que NO observa:</b> qué filtra la consulta (Application.Tests) ni el rechazo
/// del Command (Application.Tests).
/// </para>
/// </summary>
public class CentroWorkspacePanelTiposArchivadosTests : BunitContext
{
    private static readonly Guid ArchivadoExigidoId = Guid.NewGuid();

    public CentroWorkspacePanelTiposArchivadosTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
    }

    private static DocumentacionRequeridaCentroDto Fila(
        Guid id, string nombre, bool aplica, bool? incluido, bool archivado = false, string? sustituto = null) =>
        new(id, nombre, AmbitoAplicacion.Empresa, EsObligatorioGlobal: false, Aplica: aplica, Incluido: incluido,
            PeriodicidadEspecialMeses: null, BloqueaAcceso: false, ArchivoUrl: null, NombreArchivoOriginal: null,
            Archivado: archivado, NombreSustituto: sustituto);

    private sealed class MediatorFalso(CentroDetalleDto detalle, IReadOnlyList<DocumentacionRequeridaCentroDto> filas) : IMediator
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
                ObtenerDocumentacionRequeridaDeCentroQuery => filas,
                EstablecerDocumentacionRequeridaCentroCommand => Result.Exito(),
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

    private IRenderedComponent<CentroWorkspacePanel> Renderizar(params DocumentacionRequeridaCentroDto[] filas)
    {
        var centroId = Guid.NewGuid();
        _mediador = new MediatorFalso(new CentroDetalleDto(
            centroId, Guid.NewGuid(), "Refrielectric S.A.", Guid.NewGuid(), "Montajes Ebro S.L.",
            "Centro Logístico Norte", "C-001", null, null, null, Guid.NewGuid()), filas);
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
        cut.WaitForAssertion(() => cut.FindAll(".workspace-tarjeta-requisito").Should().NotBeEmpty());
        return cut;
    }

    [Fact]
    public void Un_tipo_archivado_que_el_Centro_no_exige_no_se_ofrece_para_marcarlo()
    {
        var cut = Renderizar(
            Fila(Guid.NewGuid(), "RLC (TC1)", aplica: false, incluido: null),
            Fila(Guid.NewGuid(), "Recibo de pago RLC/TC1", aplica: false, incluido: null, archivado: true, sustituto: "RLC (TC1)"));

        var tarjetas = cut.FindAll(".workspace-tarjeta-requisito-cabecera span:first-child").Select(s => s.TextContent.Trim());
        tarjetas.Should().BeEquivalentTo(new[] { "RLC (TC1)" }, "control positivo: el tipo vigente sí se ofrece, el archivado no");
        cut.Markup.Should().NotContain("Recibo de pago RLC/TC1");
    }

    [Fact]
    public void Un_tipo_archivado_que_el_Centro_ya_exige_muestra_su_sustituto()
    {
        var cut = Renderizar(
            Fila(ArchivadoExigidoId, "Recibo de pago RLC/TC1", aplica: true, incluido: true, archivado: true, sustituto: "RLC (TC1)"));

        cut.Find("[data-testid='requisito-archivado']").TextContent.Should().Contain("sustituido por RLC (TC1)");
    }

    [Fact]
    public void Un_tipo_archivado_sin_sustituto_lo_dice()
    {
        var cut = Renderizar(
            Fila(ArchivadoExigidoId, "Recibo de pago RLC/TC1", aplica: true, incluido: true, archivado: true));

        cut.Find("[data-testid='requisito-archivado']").TextContent.Should().Contain("sin sustituto");
    }

    [Fact]
    public async Task Un_tipo_archivado_que_el_Centro_ya_exige_se_puede_desmarcar()
    {
        var cut = Renderizar(
            Fila(ArchivadoExigidoId, "Recibo de pago RLC/TC1", aplica: true, incluido: true, archivado: true, sustituto: "RLC (TC1)"));

        var avisos = new List<(EntidadWorkspace Tipo, Guid Id)>();
        Services.GetRequiredService<ContextWorkspaceService>().OnEntidadGuardada += (tipo, id) => avisos.Add((tipo, id));

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Configurar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".campo-checkbox").Should().NotBeEmpty());
        cut.Find(".campo-checkbox input[type=checkbox]").Change(false);
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        var envio = _mediador.Enviadas.OfType<EstablecerDocumentacionRequeridaCentroCommand>().Should().ContainSingle().Subject;
        envio.TipoDocumentoId.Should().Be(ArchivadoExigidoId);
        envio.Incluido.Should().BeFalse("desmarcar un archivado es lo que sí permite el Command");
        avisos.Should().ContainSingle().Which.Should().Be((EntidadWorkspace.Centro, envio.CentroId),
            "tras guardar el requisito el panel avisa para que la pestaña «Requisitos» de Centro 360 se recargue");
    }

    [Fact]
    public async Task Un_tipo_archivado_exigido_no_se_guarda_marcado_aunque_se_edite_otro_campo()
    {
        var cut = Renderizar(
            Fila(ArchivadoExigidoId, "Recibo de pago RLC/TC1", aplica: true, incluido: true, archivado: true, sustituto: "RLC (TC1)"));

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Configurar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Tipo archivado: solo se puede desmarcar; el sustituto es «RLC (TC1)»"));
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        _mediador.Enviadas.OfType<EstablecerDocumentacionRequeridaCentroCommand>().Should().BeEmpty(
            "el Command rechaza Incluido = true de un archivado: se avisa sin enviarlo");
        cut.Markup.Should().Contain("solo se puede desmarcar. Exige su sustituto, «RLC (TC1)»");
    }

    [Fact]
    public async Task Quitar_el_ajuste_de_un_requisito_avisa_a_la_ficha_del_Centro()
    {
        var cut = Renderizar(Fila(ArchivadoExigidoId, "Recibo de pago RLC/TC1", aplica: true, incluido: true, archivado: true, sustituto: "RLC (TC1)"));
        var avisos = new List<(EntidadWorkspace Tipo, Guid Id)>();
        Services.GetRequiredService<ContextWorkspaceService>().OnEntidadGuardada += (tipo, id) => avisos.Add((tipo, id));

        await cut.FindAll(".workspace-tarjeta-requisito-acciones button").Single(b => b.TextContent.Trim() == "Quitar ajuste")
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Quitar ajuste").ClickAsync(new MouseEventArgs());

        var envio = _mediador.Enviadas.OfType<EliminarDocumentacionRequeridaCentroCommand>().Should().ContainSingle().Subject;
        avisos.Should().ContainSingle().Which.Should().Be((EntidadWorkspace.Centro, envio.CentroId));
    }
}