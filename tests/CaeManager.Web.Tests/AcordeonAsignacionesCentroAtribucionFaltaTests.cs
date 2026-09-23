using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Common;
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
/// «Falta» en el tercer nivel de Centro 360 sale de
/// ResolucionTipoDocumentoCentro.Aplica (ObtenerAsignacionesDocumentacionPorCentroQuery,
/// mismo criterio que Alertas.razor): la fila de este centro si existe y, si
/// no, el valor general del tipo. Atribuirlo solo al centro ("el centro lo
/// exige") mentía en los centros que no han configurado nada — hallazgo real
/// del 2026-09-11 al corregir Alertas.
/// </summary>
public class AcordeonAsignacionesCentroAtribucionFaltaTests : BunitContext
{
    public AcordeonAsignacionesCentroAtribucionFaltaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        // Los disparadores de escritura van en SoloConEscritura (AuthorizeView): por
        // defecto un rol que escribe; los tests de Consulta lo sustituyen.
        this.ConRolDeEscritura();
    }

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

    /// <summary>
    /// Asignar y dar de baja (CrearAsignaciones/DarDeBajaAsignacionesCommand) y «Gestionar»
    /// —que abre DrawerGestionDocumento, que solo crea o renueva— acaban en ICommand que
    /// AutorizacionEscrituraBehavior deniega a Consulta. Los nombres de los documentos abren
    /// el mismo drawer: a Consulta se le pintan como texto. Con un rol que escribe todo se
    /// pinta, que es el control de que el render llegó a ellos.
    /// </summary>
    [Theory]
    [InlineData(Roles.Consulta, false)]
    [InlineData(Roles.GestorCae, true)]
    public async Task Asignar_dar_de_baja_y_gestionar_se_ofrecen_solo_a_quien_escribe(string rol, bool seOfrecen)
    {
        this.ConRolDeEscritura(rol);
        var trabajador = new TrabajadorAsignacionDocumentacionDto(
            Guid.NewGuid(), Guid.NewGuid(), "Ruiz Peña, Ana", new DateOnly(2026, 1, 15), EstadoDocumento.Faltante,
            [new DocumentoRequeridoDto(null, Guid.NewGuid(), "Reconocimiento médico", EstadoDocumento.Faltante, null)]);
        var incidenciaEmpresa = new IncidenciaCentroDto(
            "Seguro de responsabilidad civil", AmbitoCausa.Empresa, EstadoDocumento.Vencido,
            Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1));
        RegistrarServicios(new MediatorFalso { Trabajadores = [trabajador] });

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, Guid.NewGuid())
            .Add(a => a.CentroNombre, "Centro Logístico Norte")
            .Add(a => a.EmpresaId, Guid.NewGuid())
            .Add(a => a.EmpresaNombre, "Montajes Ebro S.L.")
            .Add(a => a.IncidenciasEmpresa, [incidenciaEmpresa]));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));
        await cut.Find("button.boton-expandir-fila").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".tabla-documentos-requeridos").Should().HaveCount(2));

        var tablas = cut.FindAll(".tabla-documentos-requeridos").Select(t => t.TextContent).ToList();
        tablas.Should().Contain(t => t.Contains("Seguro de responsabilidad civil"), "la incidencia de la Empresa es lectura: se ve");
        tablas.Should().Contain(t => t.Contains("Reconocimiento médico"), "el documento requerido del trabajador es lectura: se ve");

        var rotulos = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();
        var enlacesDeNombre = cut.FindAll(".tabla-documentos-requeridos button.enlace-nombre-fila");
        if (seOfrecen)
        {
            rotulos.Should().Contain(["+ Asignar trabajador", "Dar de baja seleccionados", "Gestionar"]);
            rotulos.Count(r => r == "Gestionar").Should().Be(2, "uno por la Empresa y otro por el trabajador");
            enlacesDeNombre.Should().HaveCount(2);
        }
        else
        {
            rotulos.Should().NotContain(["+ Asignar trabajador", "Dar de baja seleccionados", "Gestionar"]);
            enlacesDeNombre.Should().BeEmpty("el nombre abre el mismo drawer de gestión: a Consulta se le pinta como texto");
            cut.FindAll(".acordeon-asignaciones-cabecera").Should().BeEmpty("sin acciones no queda una cabecera vacía");
        }
    }
}
