using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Hallazgo de la oleada 2 de Codex sobre la PR #833: una causa de rechazo en
/// plataforma con <c>Ambito.Empresa</c> llega con <c>Estado: null</c> a
/// propósito (CalculoEstadoCentroService — no es un vencimiento de fecha, es
/// un rechazo activo). Antes de este fix, el bloque Empresa de este
/// componente hacía <c>incidencia.Estado!.Value</c> sin comprobar null,
/// bajo el comentario "Estado siempre presente" — cierto antes del fix v2 de
/// ObtenerCentrosQuery.Desglosar, falso después. Este test demuestra que el
/// null real no lanza y que el componente pinta el badge dedicado
/// "Rechazado" en su lugar, en vez de solo probarlo indirectamente a través
/// del DTO de ObtenerCentrosQueryRecuentosTests (integración, no UI).
/// </summary>
public class AcordeonAsignacionesCentroIncidenciaEmpresaSinEstadoTests : BunitContext
{
    public AcordeonAsignacionesCentroIncidenciaEmpresaSinEstadoTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerAsignacionesDocumentacionPorCentroQuery =>
                    (IReadOnlyList<TrabajadorAsignacionDocumentacionDto>)[],
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

    [Fact]
    public void La_incidencia_de_Empresa_sin_Estado_no_lanza_y_pinta_el_badge_Rechazado()
    {
        // El badge "Rechazado" y el texto "No aplica" salen de
        // IStringLocalizer<TextosCentros> (Codex, oleada 3: ratchet de
        // textos sin localizar).
        Services.AddLocalization();
        // «Gestionar» va tras SoloConEscritura (como en Subcontratas): hace falta un rol que escriba.
        this.ConRolDeEscritura();
        Services.AddScoped<IMediator>(_ => new MediatorFalso());
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var incidencia = new IncidenciaCentroDto(
            "EPIs — rechazado por la plataforma", AmbitoCausa.Empresa, Estado: null,
            DocumentoId: Guid.NewGuid(), TipoDocumentoId: Guid.NewGuid(), FechaVencimiento: null);

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, Guid.NewGuid())
            .Add(a => a.CentroNombre, "Centro Logístico Norte")
            .Add(a => a.EmpresaId, Guid.NewGuid())
            .Add(a => a.EmpresaNombre, "Empresa Recuentos S.L.")
            .Add(a => a.IncidenciasEmpresa, [incidencia]));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("tabla-documentos-requeridos"));
        var fila = cut.Find(".tabla-documentos-requeridos .fila-documento-requerido");

        fila.TextContent.Should().Contain("Rechazado",
            "sin Estado, el componente ya no intenta indexar EstadoDocumentoUi con un valor inexistente: pinta un badge propio");
        fila.TextContent.Should().Contain("No aplica",
            "un rechazo no tiene vigencia documental: \"Sin caducidad\" sería contradictorio junto al badge \"Rechazado\" " +
            "(hallazgo de Codex, oleada 3 sobre esta misma PR)");
        fila.TextContent.Should().NotContain("Sin caducidad");
    }

    private IRenderedComponent<AcordeonAsignacionesCentro> RenderizarBloqueEmpresa(int incidencias, bool modoListado = false) =>
        RenderizarBloqueEmpresa(
            Enumerable.Range(1, incidencias)
                .Select(i => new IncidenciaCentroDto(
                    $"Seguro {i}", AmbitoCausa.Empresa, CaeManager.Domain.Documentos.EstadoDocumento.Vencido,
                    DocumentoId: Guid.NewGuid(), TipoDocumentoId: Guid.NewGuid(), FechaVencimiento: new DateOnly(2026, 1, 1)))
                .ToList(),
            modoListado);

    private IRenderedComponent<AcordeonAsignacionesCentro> RenderizarBloqueEmpresa(IReadOnlyList<IncidenciaCentroDto> lista, bool modoListado = false)
    {
        Services.AddLocalization();
        this.ConRolDeEscritura();
        Services.AddScoped<IMediator>(_ => new MediatorFalso());
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, Guid.NewGuid())
            .Add(a => a.CentroNombre, "Centro Logístico Norte")
            .Add(a => a.EmpresaId, Guid.NewGuid())
            .Add(a => a.EmpresaNombre, "Empresa Recuentos S.L.")
            .Add(a => a.ModoListado, modoListado)
            .Add(a => a.IncidenciasEmpresa, lista));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("bloque-empresa-ambito"));
        return cut;
    }

    /// <summary>
    /// El bloque Empresa comparte rejilla con los Trabajadores pero no la clase de su lista: los selectores de pruebas y de
    /// atajos de teclado que leen <c>.lista-trabajadores-acordeon</c> hablan de Trabajadores, no de la Empresa.
    /// </summary>
    [Fact]
    public void El_bloque_Empresa_no_lleva_la_clase_de_la_lista_de_Trabajadores()
    {
        var cut = RenderizarBloqueEmpresa(incidencias: 1);

        cut.FindAll(".lista-trabajadores-acordeon .fila-relacion").Should().BeEmpty();
        cut.Find(".bloque-empresa-ambito").ClassList.Should().NotContain("bloque-empresa-ambito-listado");
    }

    /// <summary>En la fila desplegada de /centros (ModoListado) la Empresa sigue el criterio compacto de sus Trabajadores.</summary>
    [Fact]
    public void En_el_listado_de_Centros_el_bloque_Empresa_lleva_la_variante_compacta()
    {
        var cut = RenderizarBloqueEmpresa(incidencias: 1, modoListado: true);

        cut.Find(".bloque-empresa-ambito").ClassList.Should().Contain("bloque-empresa-ambito-listado");
    }

    /// <summary>
    /// Pasada visual de staging (2026-10-17): el bloque Empresa de la pestaña Trabajadores de Centro 360 era una fila suelta con
    /// una insignia «2» flotando y una tabla con barra vertical propia. Ahora es una tarjeta con la gramática de la lista de
    /// Trabajadores: fila plegable de la Empresa con su cifra, su estado y «Detalles» en la rejilla, y los documentos con
    /// incidencia dentro de la fila desplegada.
    /// </summary>
    [Fact]
    public void El_bloque_Empresa_es_una_tarjeta_con_una_fila_plegable_que_lleva_cifra_y_estado()
    {
        var cut = RenderizarBloqueEmpresa(incidencias: 2);

        var tarjeta = cut.Find(".bloque-empresa-ambito .lista-relaciones-tarjeta");
        var fila = tarjeta.QuerySelector("li.fila-relacion.fila-relacion-rejilla");
        fila.Should().NotBeNull("la Empresa es una fila de la misma lista en tarjeta que los Trabajadores");

        fila!.QuerySelector(".fila-relacion-cifra")!.TextContent.Trim().Should().Be("2 doc.");
        fila.QuerySelector(".fila-relacion-estado")!.TextContent.Should().Contain("Con incidencia");
        fila.QuerySelector(".fila-relacion-acciones")!.TextContent.Should().Contain("Detalles");

        cut.FindAll(".fila-sujeto-ambito").Should().BeEmpty("la fila suelta con indicadores a la derecha ya no existe");
        cut.FindAll(".badge-solo-recuento").Should().BeEmpty("el contador va dentro de la fila, no como insignia flotante");

        var desplegado = fila.QuerySelector(".fila-relacion-desplegado");
        desplegado.Should().NotBeNull("las incidencias cuelgan de la fila de la Empresa");
        desplegado!.QuerySelectorAll(".fila-documento-requerido").Should().HaveCount(2);

        cut.FindAll(".separador-ambito").Select(e => e.TextContent.Trim()).Should().Equal("Empresa", "Trabajadores");
    }

    /// <summary>
    /// Decisión del 2026-10-21 (#1530): dentro de la fila desplegada de /centros la tarjeta de la Empresa nace contraída
    /// (sus documentos no se ven hasta pulsar su chevron); en Centro 360 sigue desplegada de entrada.
    /// </summary>
    [Fact]
    public void En_el_listado_de_Centros_la_fila_de_la_Empresa_nace_contraida()
    {
        var cut = RenderizarBloqueEmpresa(incidencias: 2, modoListado: true);
        var chevron = () => cut.Find(".bloque-empresa-ambito .fila-relacion-desplegar");

        chevron().GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll(".bloque-empresa-ambito .fila-documento-requerido").Should().BeEmpty();
        cut.Find(".bloque-empresa-ambito .fila-relacion-cifra").TextContent.Trim().Should().Be("2 doc.", "la cifra sí se ve contraída");

        chevron().Click();

        cut.FindAll(".bloque-empresa-ambito .fila-documento-requerido").Should().HaveCount(2);
    }

    private static IncidenciaCentroDto DocumentoEmpresa(string descripcion, CaeManager.Domain.Documentos.EstadoDocumento? estado) =>
        new(descripcion, AmbitoCausa.Empresa, estado,
            DocumentoId: Guid.NewGuid(), TipoDocumentoId: Guid.NewGuid(), FechaVencimiento: new DateOnly(2026, 12, 1));

    /// <summary>
    /// Demo con IT (2026-11-10): una Empresa con sus documentos vigentes que solo vencen el mes siguiente salía con la pastilla
    /// roja «Con incidencia» y la fila teñida de rojo. «Por vencer» no es una incidencia: ámbar, como la fila del Centro.
    /// </summary>
    [Fact]
    public void Con_solo_documentos_por_vencer_el_bloque_Empresa_es_Por_vencer_en_ambar_sin_rojo()
    {
        var cut = RenderizarBloqueEmpresa([
            DocumentoEmpresa("Certificado de estar al corriente con la Seguridad Social — Empresa", CaeManager.Domain.Documentos.EstadoDocumento.Proximo),
            DocumentoEmpresa("Seguro de responsabilidad civil — Empresa", CaeManager.Domain.Documentos.EstadoDocumento.Urgente)]);

        var fila = cut.Find(".bloque-empresa-ambito li.fila-relacion");
        fila.GetAttribute("data-tono").Should().Be("advertencia", "sin vencidos ni pendientes la fila no se tiñe de rojo");
        var estado = fila.QuerySelector(".fila-relacion-estado")!;
        estado.TextContent.Should().Contain("Por vencer").And.NotContain("Con incidencia");
        estado.InnerHtml.Should().Contain("advertencia").And.NotContain("peligro");

        // Nada de lo que se lee ni se anuncia habla de incidencia junto a «Por vencer».
        fila.QuerySelector(".fila-relacion-cifra span")!.GetAttribute("title").Should().Be("2 documentos de empresa por vencer");
        fila.TextContent.Should().Contain("Solo se listan los documentos por vencer").And.NotContain("incidencia");
        cut.Find(".bloque-empresa-ambito [role=table]").GetAttribute("aria-label").Should().Be("Documentación por vencer de Empresa Recuentos S.L.");
        cut.Find(".bloque-empresa-ambito .fila-relacion-desplegar").GetAttribute("aria-label").Should().Be("Documentos por vencer de Empresa Recuentos S.L.");
    }

    [Fact]
    public void Con_un_solo_documento_por_vencer_la_cifra_va_en_singular()
    {
        var cut = RenderizarBloqueEmpresa([DocumentoEmpresa("Seguro — Empresa", CaeManager.Domain.Documentos.EstadoDocumento.Proximo)]);

        cut.Find(".bloque-empresa-ambito .fila-relacion-cifra span").GetAttribute("title").Should().Be("1 documento de empresa por vencer");
    }

    [Fact]
    public void Con_un_documento_vencido_entre_los_por_vencer_el_bloque_Empresa_sigue_Con_incidencia_en_rojo()
    {
        var cut = RenderizarBloqueEmpresa([
            DocumentoEmpresa("Seguro — Empresa", CaeManager.Domain.Documentos.EstadoDocumento.Proximo),
            DocumentoEmpresa("Contrato de mantenimiento — Empresa", CaeManager.Domain.Documentos.EstadoDocumento.Vencido)]);

        var fila = cut.Find(".bloque-empresa-ambito li.fila-relacion");
        fila.GetAttribute("data-tono").Should().Be("peligro");
        fila.QuerySelector(".fila-relacion-estado")!.TextContent.Should().Contain("Con incidencia");
    }

    /// <summary>El sufijo « — Empresa» del título repite el encabezado EMPRESA bajo el que cuelga el documento.</summary>
    [Fact]
    public void El_titulo_del_documento_en_el_bloque_Empresa_no_repite_el_sufijo_Empresa()
    {
        var cut = RenderizarBloqueEmpresa([
            DocumentoEmpresa("Certificado de estar al corriente con la Seguridad Social — Empresa", CaeManager.Domain.Documentos.EstadoDocumento.Proximo),
            DocumentoEmpresa("EPIs — Empresa — rechazado por la plataforma", null)]);

        var titulos = cut.FindAll(".bloque-empresa-ambito .celda-documento-nombre").Select(c => c.TextContent.Trim()).ToList();

        titulos.Should().Equal("Certificado de estar al corriente con la Seguridad Social", "EPIs — rechazado por la plataforma");
    }

    [Theory]
    [InlineData("Seguro — Empresa", "Seguro")]
    [InlineData("Seguro — Empresa — rechazado por la plataforma", "Seguro — rechazado por la plataforma")]
    [InlineData("Seguro — rechazado por la plataforma", "Seguro — rechazado por la plataforma")]
    [InlineData("Seguro de Empresa", "Seguro de Empresa")]
    [InlineData("Póliza — Empresa — rechazado por la plataforma", "Póliza — rechazado por la plataforma")]
    [InlineData("Alta — Empresa — extra — Empresa — rechazado por la plataforma", "Alta — Empresa — extra — rechazado por la plataforma")]
    public void TituloDocumentoEmpresa_solo_quita_el_segmento_Empresa(string descripcion, string esperado)
    {
        AcordeonAsignacionesCentro.TituloDocumentoEmpresa(DocumentoEmpresa(descripcion, null)).Should().Be(esperado);
    }

    [Fact]
    public void La_fila_de_la_Empresa_se_pliega_y_se_despliega_con_su_chevron()
    {
        var cut = RenderizarBloqueEmpresa(incidencias: 1);
        var chevron = () => cut.Find(".bloque-empresa-ambito .fila-relacion-desplegar");

        cut.FindAll(".bloque-empresa-ambito .fila-documento-requerido").Should().HaveCount(1, "va desplegada de entrada: solo existe si hay algo que resolver");
        chevron().GetAttribute("aria-expanded").Should().Be("true");
        cut.Find(".bloque-empresa-ambito .fila-relacion-cifra").TextContent.Trim().Should().Be("1 doc.");

        chevron().Click();

        cut.FindAll(".bloque-empresa-ambito .fila-documento-requerido").Should().BeEmpty();
        chevron().GetAttribute("aria-expanded").Should().Be("false");

        chevron().Click();

        cut.FindAll(".bloque-empresa-ambito .fila-documento-requerido").Should().HaveCount(1);
    }
}
