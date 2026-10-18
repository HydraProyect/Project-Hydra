using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Domain.Common;
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
                ObtenerDocumentoPorIdQuery consulta => new DocumentoDetalleDto(
                    consulta.Id, AmbitoAplicacion.Trabajador, "Ruiz Peña, Ana", "Reconocimiento médico", true,
                    new DateOnly(2026, 1, 15), new DateOnly(2027, 1, 15), default, null, null, null, null, null, null,
                    Guid.NewGuid(), PerfilDocumentoOficial.Ninguno, null),
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
            [new DocumentoRequeridoDto(null, Guid.NewGuid(), "Reconocimiento médico", EstadoDocumento.Faltante, null)],
            new FraccionCumplimiento(0, 1));

        RegistrarServicios(new MediatorFalso { Trabajadores = [trabajador] });

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, Guid.NewGuid())
            .Add(a => a.CentroNombre, "Centro Logístico Norte"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));
        await cut.Find("button.fila-relacion-desplegar").ClickAsync(new MouseEventArgs());

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

    // ---- Acciones por estado; documentos al día conservan Gestionar sin atenuación ----

    private static TrabajadorAsignacionDocumentacionDto TrabajadorConDocumento(EstadoDocumento estado) => new(
        Guid.NewGuid(), Guid.NewGuid(), "Ruiz Peña, Ana", new DateOnly(2026, 1, 15), estado,
        [new DocumentoRequeridoDto(estado == EstadoDocumento.Faltante ? null : Guid.NewGuid(), Guid.NewGuid(), "Reconocimiento médico", estado,
            estado == EstadoDocumento.Faltante ? null : new DateOnly(2027, 1, 15))],
        CumplimientoDocumental.Evaluar([estado]));

    private async Task<IRenderedComponent<AcordeonAsignacionesCentro>> RenderizarExpandidoAsync(EstadoDocumento estado, string? rol = null, bool riesgoEnVisita = false)
    {
        var trabajador = TrabajadorConDocumento(estado);
        if (riesgoEnVisita) trabajador = trabajador with { Documentos = [trabajador.Documentos[0] with { CaducaEnVentanaVisita = true }] };
        RegistrarServicios(new MediatorFalso { Trabajadores = [trabajador] });
        if (rol is not null)
            this.ConRolDeEscritura(rol);

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, Guid.NewGuid())
            .Add(a => a.CentroNombre, "Centro Logístico Norte"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));
        await cut.Find("button.fila-relacion-desplegar").ClickAsync(new MouseEventArgs());
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

    /// <summary>
    /// Un documento al día (Vigente o Sin caducidad) lleva «Gestionar» en tono secundario (regla de Chris, 2026-10-18, que
    /// sustituye a «sin botón» de la regla 8 y la maqueta del 17-10), también con la Visita en riesgo.
    /// </summary>
    [Theory]
    [InlineData(EstadoDocumento.Vigente, false)]
    [InlineData(EstadoDocumento.Vigente, true)]
    [InlineData(EstadoDocumento.SinCaducidad, false)]
    public async Task Documento_al_dia_lleva_Gestionar_en_tono_secundario_si_se_puede_escribir(EstadoDocumento estado, bool riesgoEnVisita)
    {
        var cut = await RenderizarExpandidoAsync(estado, riesgoEnVisita: riesgoEnVisita);
        var boton = cut.Find(".tabla-documentos-requeridos .columna-accion button");
        boton.TextContent.Trim().Should().Be("Gestionar");
        boton.ClassList.Should().Contain("accion-fila-enlace").And.Contain("gestionar-discreto").And.NotContain("accion-atenuada");
        cut.Find(".tabla-documentos-requeridos").TextContent.Should().Contain("Reconocimiento médico");
    }

    /// <summary>
    /// El nombre del documento sigue siendo una vía más para quien escribe: con la Visita en riesgo (Vigente que caduca
    /// dentro de ella) abre la misma gestión que «Gestionar».
    /// </summary>
    [Fact]
    public async Task El_nombre_del_documento_Vigente_con_riesgo_en_Visita_abre_la_gestion()
    {
        var cut = await RenderizarExpandidoAsync(EstadoDocumento.Vigente, riesgoEnVisita: true);

        cut.FindAll(".tabla-documentos-requeridos .columna-accion button").Should().ContainSingle();
        var enlace = cut.Find(".tabla-documentos-requeridos .celda-documento-nombre button.enlace-nombre-fila");
        enlace.TextContent.Trim().Should().Be("Reconocimiento médico");
        await enlace.ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find(".drawer-panel").TextContent.Should().Contain("Renovar documento")
            .And.Contain("Reconocimiento médico").And.Contain("Ruiz Peña, Ana"));
    }

    [Fact]
    public async Task El_clic_en_Gestionar_abre_la_gestion_del_documento()
    {
        var cut = await RenderizarExpandidoAsync(EstadoDocumento.Vencido);
        var boton = cut.Find(".tabla-documentos-requeridos .columna-accion button");
        boton.TextContent.Trim().Should().Be("Gestionar");
        boton.ClassList.Should().Contain("accion-fila-enlace").And.NotContain("accion-atenuada");
        boton.HasAttribute("title").Should().BeFalse();
        await boton.ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find(".drawer-panel").TextContent.Should().Contain("Renovar documento")
            .And.Contain("Reconocimiento médico").And.Contain("Ruiz Peña, Ana"));
    }

    [Theory]
    [InlineData(EstadoDocumento.Vencido, "Gestionar")]
    [InlineData(EstadoDocumento.EnTolerancia, "Gestionar")]
    [InlineData(EstadoDocumento.Urgente, "Gestionar")]
    [InlineData(EstadoDocumento.Proximo, "Gestionar")]
    [InlineData(EstadoDocumento.SinConfirmar, "Gestionar")]
    [InlineData(EstadoDocumento.Faltante, "Gestionar")]
    public async Task El_documento_ofrece_una_sola_accion_Gestionar_sea_cual_sea_su_estado_y_no_esta_atenuada(EstadoDocumento estado, string accion)
    {
        var cut = await RenderizarExpandidoAsync(estado);
        var boton = cut.Find(".tabla-documentos-requeridos .columna-accion button");
        boton.TextContent.Trim().Should().Be(accion);
        boton.ClassList.Should().NotContain("accion-atenuada")
            .And.NotContain("gestionar-discreto", "solo el documento al día lleva el tono secundario; el resto, el normal");
        boton.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Gestionar_de_un_documento_que_pide_intervencion_no_esta_atenuado_ni_lleva_titulo()
    {
        var cut = await RenderizarExpandidoAsync(EstadoDocumento.Faltante);

        var boton = cut.FindAll(".tabla-documentos-requeridos button").Single(b => b.TextContent.Trim() == "Gestionar");
        boton.ClassList.Should().NotContain("accion-atenuada");
        boton.GetAttribute("title").Should().BeNull();
    }

    [Theory]
    [InlineData(EstadoDocumento.Vigente)]
    [InlineData(EstadoDocumento.SinCaducidad)]
    [InlineData(EstadoDocumento.Vencido)]
    public async Task Consulta_no_recibe_accion_de_fila_para_un_documento(EstadoDocumento estado)
    {
        using var consulta = new ConsultaSinEscritura();
        var cut = await consulta.RenderizarAsync(estado);
        cut.FindAll(".tabla-documentos-requeridos .columna-accion button").Should().BeEmpty();
    }

    /// <summary>Segundo contexto de bUnit con rol Consulta: el rol se fija al construir los servicios.</summary>
    private sealed class ConsultaSinEscritura : BunitContext
    {
        public ConsultaSinEscritura() => JSInterop.Mode = JSRuntimeMode.Loose;

        public async Task<IRenderedComponent<AcordeonAsignacionesCentro>> RenderizarAsync(EstadoDocumento estado = EstadoDocumento.Faltante)
        {
            Services.AddLocalization();
            this.ConRolDeEscritura(CaeManager.Infrastructure.Identity.Roles.Consulta);
            Services.AddScoped<IMediator>(_ => new MediatorFalso { Trabajadores = [TrabajadorConDocumento(estado)] });
            Services.AddScoped<ToastService>();
            Services.AddScoped<ContextWorkspaceService>();
            Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
            Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
            Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

            var cut = Render<AcordeonAsignacionesCentro>(p => p
                .Add(a => a.CentroId, Guid.NewGuid())
                .Add(a => a.CentroNombre, "Centro Logístico Norte"));
            cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));
            await cut.Find("button.fila-relacion-desplegar").ClickAsync(new MouseEventArgs());
            cut.WaitForAssertion(() => cut.Markup.Should().Contain("tabla-documentos-requeridos"));
            return cut;
        }
    }
}
