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
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// La casilla "Incluido" del drawer de "Requisitos del Centro" parte de
/// <c>item.Aplica</c> (ObtenerDocumentacionRequeridaDeCentroQuery, mismo
/// criterio de ResolucionTipoDocumentoCentro.Aplica que Alertas.razor):
/// puede llegar premarcada sin que este centro tenga fila propia, solo
/// porque el tipo se pide siempre por defecto. La etiqueta "este centro lo
/// exige" atribuía esa marca inicial al centro incluso cuando venía del
/// valor general del tipo — la nota de la lista (arriba del botón
/// "Configurar") ya explica las dos procedencias; la casilla no debe
/// contradecirla.
/// </summary>
public class CentroWorkspacePanelDrawerRequisitoAtribucionTests : BunitContext
{
    public CentroWorkspacePanelDrawerRequisitoAtribucionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        // Los disparadores de escritura van en SoloConEscritura (AuthorizeView): por
        // defecto un rol que escribe; los tests de Consulta lo sustituyen.
        this.ConRolDeEscritura();
    }

    private sealed class MediatorFalso : IMediator
    {
        public required CentroDetalleDto Detalle { get; init; }
        public required IReadOnlyList<DocumentacionRequeridaCentroDto> Documentacion { get; init; }
        public IReadOnlyList<LoteReclamacionClienteDto> Lotes { get; init; } = [];
        public IReadOnlyList<CanalGestionResumenDto> Canales { get; init; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerCentroPorIdQuery => Detalle,
                ObtenerUltimaReclamacionClienteQuery => null,
                ObtenerLoteReclamacionQuery => Lotes,
                ObtenerEstadoCentroQuery => null,
                ObtenerDocumentacionRequeridaDeCentroQuery => Documentacion,
                ObtenerCanalesGestionDeCentroQuery => Canales,
                ObtenerProveedoresPlataformaCaeQuery => Array.Empty<ProveedorPlataformaCaeListaDto>(),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            })!);

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
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <summary>No se visita la pestaña "plataforma" en este test — nadie debe llamarlo.</summary>
    private sealed class ResolucionProveedorQueNadieDebeTocar : IResolucionProveedorPlataformaCaeService
    {
        private static Exception NoDeberia() => new NotSupportedException("Sin pasar por 'plataforma' no debería resolverse ningún proveedor.");
        public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorUrlAsync(string url, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorDominioCorreoAsync(string email, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() => new NotSupportedException("Con el drawer de documentación no se sube ningún archivo en este test.");
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private void RegistrarServicios(MediatorFalso mediator)
    {
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IResolucionProveedorPlataformaCaeService, ResolucionProveedorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    }

    private static CentroDetalleDto Centro(Guid centroId) => new(
        centroId, Guid.NewGuid(), "Refrielectric S.A.", Guid.NewGuid(), "Montajes Ebro S.L.",
        "Centro Logístico Norte", "C-001", null, null, null, Guid.NewGuid());

    [Fact]
    public async Task La_casilla_Incluido_no_atribuye_la_marca_inicial_solo_al_centro()
    {
        var centroId = Guid.NewGuid();

        // Aplica=true, Incluido=null: NINGUNA fila TipoDocumentoCentro para este
        // centro — la casilla llega premarcada solo porque el tipo se pide
        // siempre por defecto (ResolucionTipoDocumentoCentro.Aplica), no porque
        // este centro lo haya configurado.
        var item = new DocumentacionRequeridaCentroDto(
            Guid.NewGuid(), "Reconocimiento médico", AmbitoAplicacion.Trabajador,
            EsObligatorioGlobal: true, Aplica: true, Incluido: null,
            PeriodicidadEspecialMeses: null, BloqueaAcceso: false, ArchivoUrl: null, NombreArchivoOriginal: null);

        RegistrarServicios(new MediatorFalso { Detalle = Centro(centroId), Documentacion = [item] });

        var cut = Render<CentroWorkspacePanel>(p => p
            .Add(c => c.EntidadId, centroId)
            .Add(c => c.PestanaActiva, "requisitos"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Reconocimiento médico"));
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Configurar").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("campo-checkbox"));
        var etiqueta = cut.FindAll(".campo-checkbox").First(l => l.TextContent.Contains("Incluido")).TextContent.Trim();

        etiqueta.Should().Be("Incluido en este centro");
        etiqueta.Should().NotContain("este centro lo exige",
            "la casilla puede llegar premarcada por el valor general del tipo, no porque este centro lo haya configurado");
    }

    // ------------------------------------------------------------------ solo lectura (Consulta)

    private static IReadOnlyList<string> RotulosDeBotones(IRenderedComponent<CentroWorkspacePanel> cut) =>
        cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();

    /// <summary>
    /// Pedir prioridad (PedirPrioridadValidacionCommand), Reclamar documentación
    /// (EnviarReclamacionCommand) y el lápiz de Editar (EditarCentroCommand) son ICommand
    /// que AutorizacionEscrituraBehavior deniega a Consulta: no se le ofrecen. Con un rol
    /// que escribe sí se pintan, que es el control de que el render llegó a ellos.
    /// </summary>
    [Theory]
    [InlineData(Roles.Consulta, false)]
    [InlineData(Roles.GestorCae, true)]
    public void La_cabecera_ofrece_editar_pedir_prioridad_y_reclamar_solo_a_quien_escribe(string rol, bool seOfrecen)
    {
        this.ConRolDeEscritura(rol);
        var centroId = Guid.NewGuid();
        var lote = new LoteReclamacionClienteDto(Guid.NewGuid(), "Refrielectric S.A.", null,
            [new DocumentoReclamableDto(Guid.NewGuid(), Guid.NewGuid(), "Ruiz Peña, Ana", Guid.NewGuid(),
                "Reconocimiento médico", new DateOnly(2026, 1, 1), EstadoDocumento.Vencido)]);
        RegistrarServicios(new MediatorFalso { Detalle = Centro(centroId), Documentacion = [], Lotes = [lote] });

        var cut = Render<CentroWorkspacePanel>(p => p
            .Add(c => c.EntidadId, centroId)
            .Add(c => c.PestanaActiva, "informacion"));

        cut.WaitForAssertion(() => cut.Find(".workspace-titulo-entidad").TextContent.Should().Contain("Centro Logístico Norte",
            "la ficha del centro es lectura: se ve con cualquier rol"));
        var rotulos = RotulosDeBotones(cut);
        var lapiz = cut.FindAll("button").Where(b => b.GetAttribute("aria-label") == "Editar información del centro");
        if (seOfrecen)
        {
            rotulos.Should().Contain(["Pedir prioridad", "Reclamar documentación (1)"]);
            lapiz.Should().ContainSingle();
        }
        else
        {
            rotulos.Should().NotContain(r => r.StartsWith("Pedir prioridad") || r.StartsWith("Reclamar documentación"));
            lapiz.Should().BeEmpty();
        }
    }

    /// <summary>
    /// Configurar y Quitar ajuste (Establecer/EliminarDocumentacionRequeridaCentroCommand)
    /// son ICommand denegados a Consulta; el requisito en sí se sigue leyendo.
    /// </summary>
    [Theory]
    [InlineData(Roles.Consulta, false)]
    [InlineData(Roles.GestorCae, true)]
    public void Los_requisitos_se_configuran_solo_con_un_rol_que_escribe(string rol, bool seOfrecen)
    {
        this.ConRolDeEscritura(rol);
        var centroId = Guid.NewGuid();
        var item = new DocumentacionRequeridaCentroDto(
            Guid.NewGuid(), "Reconocimiento médico", AmbitoAplicacion.Trabajador,
            EsObligatorioGlobal: true, Aplica: true, Incluido: true,
            PeriodicidadEspecialMeses: null, BloqueaAcceso: false, ArchivoUrl: null, NombreArchivoOriginal: null);
        RegistrarServicios(new MediatorFalso { Detalle = Centro(centroId), Documentacion = [item] });

        var cut = Render<CentroWorkspacePanel>(p => p
            .Add(c => c.EntidadId, centroId)
            .Add(c => c.PestanaActiva, "requisitos"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Reconocimiento médico", "el requisito es lectura: se ve"));
        var rotulos = RotulosDeBotones(cut);
        if (seOfrecen)
            rotulos.Should().Contain(["Configurar", "Quitar ajuste"]);
        else
            rotulos.Should().NotContain(["Configurar", "Quitar ajuste"]);
    }

    /// <summary>
    /// Editar, Marcar principal y Eliminar un canal de gestión son ICommand denegados a
    /// Consulta, igual que «Añadir acceso»; los datos del canal se siguen leyendo.
    /// </summary>
    [Theory]
    [InlineData(Roles.Consulta, false)]
    [InlineData(Roles.GestorCae, true)]
    public void Los_canales_se_gestionan_solo_con_un_rol_que_escribe(string rol, bool seOfrecen)
    {
        this.ConRolDeEscritura(rol);
        var centroId = Guid.NewGuid();
        var canal = new CanalGestionResumenDto(
            Guid.NewGuid(), TipoCanalGestion.Email, "Documentación del centro", EsPrincipal: false,
            null, null, null, "documentacion@refrielectric.example", "Marta Ibáñez", null,
            TieneCredenciales: false, Guid.NewGuid());
        RegistrarServicios(new MediatorFalso { Detalle = Centro(centroId), Documentacion = [], Canales = [canal] });

        var cut = Render<CentroWorkspacePanel>(p => p
            .Add(c => c.EntidadId, centroId)
            .Add(c => c.PestanaActiva, "plataforma"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("documentacion@refrielectric.example",
            "el destinatario del canal es lectura: se ve"));
        var rotulos = RotulosDeBotones(cut);
        if (seOfrecen)
            rotulos.Should().Contain(["Editar", "Marcar principal", "Eliminar", "Añadir acceso"]);
        else
            rotulos.Should().NotContain(["Editar", "Marcar principal", "Eliminar", "Añadir acceso"]);
    }
}
