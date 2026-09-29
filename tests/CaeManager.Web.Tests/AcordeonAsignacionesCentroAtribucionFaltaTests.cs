using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
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
/// «Falta» en el tercer nivel de Centro 360 sale de
/// ResolucionTipoDocumentoCentro.Aplica (ObtenerAsignacionesDocumentacionPorCentroQuery,
/// mismo criterio que Alertas.razor): la fila de este centro si existe y, si
/// no, el valor general del tipo. Atribuirlo solo al centro ("el centro lo
/// exige") mentía en los centros que no han configurado nada — hallazgo real
/// del 2026-09-11 al corregir Alertas.
/// </summary>
public class AcordeonAsignacionesCentroAtribucionFaltaTests : BunitContext
{
    public AcordeonAsignacionesCentroAtribucionFaltaTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public required IReadOnlyList<TrabajadorAsignacionDocumentacionDto> Trabajadores { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerAsignacionesDocumentacionPorCentroQuery => Trabajadores,
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

    /// <summary>El acordeón monta DrawerGestionDocumento, que los inyecta; cerrado, nadie los toca.</summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Con el drawer cerrado no se abre ningún archivo; si esto salta, el acordeón cambió de camino.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Con el drawer cerrado no se convierte nada; si esto salta, el acordeón cambió de camino.");
    }

    private void RegistrarServicios(MediatorFalso mediator)
    {
        // El acordeón inyecta IStringLocalizer<TextosCentros> (badge "Rechazado"
        // y "No aplica" de la fila de Empresa sin Estado, Codex oleada 3).
        Services.AddLocalization();
        // «Gestionar» va tras SoloConEscritura (como en Subcontratas): hace falta un rol que escriba.
        this.ConRolDeEscritura();
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    }

    [Fact]
    public async Task El_tooltip_de_Falta_atribuye_la_exigencia_a_las_dos_procedencias_posibles()
    {
        var trabajador = new TrabajadorAsignacionDocumentacionDto(
            Guid.NewGuid(), Guid.NewGuid(), "Ruiz Peña, Ana", new DateOnly(2026, 1, 15), EstadoDocumento.Faltante,
            [new DocumentoRequeridoDto(null, Guid.NewGuid(), "Reconocimiento médico", EstadoDocumento.Faltante, null)]);

        RegistrarServicios(new MediatorFalso { Trabajadores = [trabajador] });

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, Guid.NewGuid())
            .Add(a => a.CentroNombre, "Centro Logístico Norte"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));
        await cut.Find("button.boton-expandir-fila").ClickAsync(new MouseEventArgs());

        // DocumentosVencidos cuenta Faltante como vencido (misma severidad),
        // así que el mismo trabajador pinta OTRA VentanaContexto en el
        // recuento de la fila — acotar al tercer nivel evita leer esa.
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("tabla-documentos-requeridos"));
        var panel = cut.Find(".tabla-documentos-requeridos .ventana-contexto-panel");
        var texto = panel.TextContent;

        texto.Should().Contain("porque este centro lo tiene configurado")
            .And.Contain("si no dice nada, porque el tipo de documento se pide siempre",
                "sin fila del centro, manda el valor general del tipo (ResolucionTipoDocumentoCentro.Aplica)");
        texto.Should().NotContain("Este centro exige el documento",
            "esa frase atribuía la exigencia solo al centro, aunque no hubiera configurado nada");
        texto.Should().NotContainEquivalentOf("obligatori", "es configuración, no una obligación legal");

        var disparador = cut.Find(".tabla-documentos-requeridos .ventana-contexto");
        disparador.GetAttribute("aria-label")!.Should().Contain("porque lo tiene configurado")
            .And.Contain("si no dice nada, porque el tipo se pide siempre");
    }

    // ---- «Gestionar» unificado con AcordeonTrabajadoresSubcontrata (lote 3, patrón de lista pieza 5) ----

    private static TrabajadorAsignacionDocumentacionDto TrabajadorConDocumento(EstadoDocumento estado) => new(
        Guid.NewGuid(), Guid.NewGuid(), "Ruiz Peña, Ana", new DateOnly(2026, 1, 15), estado,
        [new DocumentoRequeridoDto(estado == EstadoDocumento.Faltante ? null : Guid.NewGuid(), Guid.NewGuid(), "Reconocimiento médico", estado,
            estado == EstadoDocumento.Faltante ? null : new DateOnly(2027, 1, 15))]);

    private async Task<IRenderedComponent<AcordeonAsignacionesCentro>> RenderizarExpandidoAsync(EstadoDocumento estado, string? rol = null)
    {
        RegistrarServicios(new MediatorFalso { Trabajadores = [TrabajadorConDocumento(estado)] });
        if (rol is not null)
            this.ConRolDeEscritura(rol);

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, Guid.NewGuid())
            .Add(a => a.CentroNombre, "Centro Logístico Norte"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));
        await cut.Find("button.boton-expandir-fila").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("tabla-documentos-requeridos"));
        return cut;
    }

    [Fact]
    public async Task Gestionar_se_ofrece_a_quien_escribe_y_no_a_Consulta()
    {
        var escribe = await RenderizarExpandidoAsync(EstadoDocumento.Faltante);
        escribe.FindAll(".tabla-documentos-requeridos button").Select(b => b.TextContent.Trim()).Should().Contain("Gestionar",
            "control positivo: con un rol que escribe el botón está");

        // Otro contexto: mismo montaje con Consulta, que no puede ejecutar CrearDocumento/RenovarDocumento.
        using var consulta = new ConsultaSinEscritura();
        var cut = await consulta.RenderizarAsync();
        cut.FindAll(".tabla-documentos-requeridos button").Select(b => b.TextContent.Trim()).Should().NotContain("Gestionar");
    }

    [Fact]
    public async Task Gestionar_atenuado_de_un_documento_al_dia_explica_por_que()
    {
        var cut = await RenderizarExpandidoAsync(EstadoDocumento.Vigente);

        var boton = cut.FindAll(".tabla-documentos-requeridos button").Single(b => b.TextContent.Trim() == "Gestionar");
        boton.ClassList.Should().Contain("accion-atenuada");
        boton.GetAttribute("title").Should().Contain("Al día", "una acción atenuada sin motivo visible es un botón roto");
    }

    [Fact]
    public async Task Gestionar_de_un_documento_que_pide_intervencion_no_esta_atenuado_ni_lleva_titulo()
    {
        var cut = await RenderizarExpandidoAsync(EstadoDocumento.Faltante);

        var boton = cut.FindAll(".tabla-documentos-requeridos button").Single(b => b.TextContent.Trim() == "Gestionar");
        boton.ClassList.Should().NotContain("accion-atenuada");
        boton.GetAttribute("title").Should().BeNull();
    }

    /// <summary>Segundo contexto de bUnit con rol Consulta: el rol se fija al construir los servicios.</summary>
    private sealed class ConsultaSinEscritura : BunitContext
    {
        public ConsultaSinEscritura() => JSInterop.Mode = JSRuntimeMode.Loose;

        public async Task<IRenderedComponent<AcordeonAsignacionesCentro>> RenderizarAsync()
        {
            Services.AddLocalization();
            this.ConRolDeEscritura(CaeManager.Infrastructure.Identity.Roles.Consulta);
            Services.AddScoped<IMediator>(_ => new MediatorFalso { Trabajadores = [TrabajadorConDocumento(EstadoDocumento.Faltante)] });
            Services.AddScoped<ToastService>();
            Services.AddScoped<ContextWorkspaceService>();
            Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
            Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
            Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

            var cut = Render<AcordeonAsignacionesCentro>(p => p
                .Add(a => a.CentroId, Guid.NewGuid())
                .Add(a => a.CentroNombre, "Centro Logístico Norte"));
            cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));
            await cut.Find("button.boton-expandir-fila").ClickAsync(new MouseEventArgs());
            cut.WaitForAssertion(() => cut.Markup.Should().Contain("tabla-documentos-requeridos"));
            return cut;
        }
    }
}
