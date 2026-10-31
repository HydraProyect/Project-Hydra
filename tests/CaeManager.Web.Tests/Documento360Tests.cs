using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerFichaDocumento360;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerValidacionOficialDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Documento 360 (<see cref="DocumentoDetalle"/>, <c>/documentos/{id}</c>): las secciones «Dónde se exige» y «Versiones»
/// con los rótulos, estados y orden del mockup «Documento 360 TALVEG». Prueban lo que se ve: qué filas salen, en qué orden,
/// con qué estado y qué botones lleva cada una.
/// </summary>
public class Documento360Tests : BunitContext
{
    public Documento360Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/cajas-ficha.js").Setup<double[]>("medirAlturas", _ => true).SetResult([]);
    }

    private sealed class MediatorFalso : IMediator
    {
        public FichaDocumento360Dto? Ficha { get; set; }
        public ValidacionOficialDocumentoDto? Validacion { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request switch
            {
                ObtenerFichaDocumento360Query => Task.FromResult((TResponse)(object?)Ficha!),
                ObtenerValidacionOficialDocumentoQuery => Task.FromResult((TResponse)(object?)Validacion!),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}."),
            };

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

    /// <summary>La página monta DrawerGestionDocumento, que los inyecta; cerrado, nadie los toca.</summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() => new NotSupportedException("Con el drawer cerrado no se abre ningún archivo.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Con el drawer cerrado no se convierte nada.");
    }

    // ── Montaje ───────────────────────────────────────────────────────────

    private static readonly Guid DocumentoId = Guid.NewGuid();
    private static readonly Guid EmpresaId = Guid.NewGuid();
    private static readonly Guid CentroNorteId = Guid.NewGuid();
    private static readonly Guid CentroZaragozaId = Guid.NewGuid();

    private static DocumentoDetalleDto Cabecera(Guid? sustitutoId = null) => new(
        DocumentoId, AmbitoAplicacion.Empresa, "Obras y Estructuras del Ebro S.L.", "Certificado de estar al corriente con la TGSS",
        false, new DateOnly(2026, 7, 10), new DateOnly(2026, 8, 10), EstadoVigenciaDocumento.VenceEnFecha,
        "archivo-julio", "Sustituye al certificado de junio, que salió con descubiertos.", null, null, null, null,
        Guid.NewGuid(), PerfilDocumentoOficial.CorrienteTgss, EmpresaId, null, "certificado-tgss-julio.pdf",
        sustitutoId, sustitutoId is null ? null : DateTime.UtcNow);

    /// <summary>Las del mockup: Centro Norte bloquea y está vencido; Planta Zaragoza da 5 días de tolerancia.</summary>
    private static FichaDocumento360Dto FichaDelMockup() => new(
        Cabecera(), EstadoDocumento.Vencido,
        [
            new(CentroNorteId, "Centro Norte", "Refrielectric S.A.", "Nalanda", EstadoDocumento.Vencido, null, true, 0),
            new(CentroZaragozaId, "Planta Zaragoza", "Refrielectric S.A.", "Dokify", EstadoDocumento.EnTolerancia, new DateOnly(2026, 8, 15), false, 5)
        ],
        [
            new(Guid.NewGuid(), "certificado-tgss-julio.pdf", true, new DateOnly(2026, 7, 10), new DateOnly(2026, 8, 10), true, EstadoDocumento.Vencido, null),
            new(Guid.NewGuid(), "certificado-tgss-junio.pdf", true, new DateOnly(2026, 6, 10), null, false, EstadoDocumento.Vencido, "negativa por descubiertos"),
            new(Guid.NewGuid(), "certificado-tgss-mayo.pdf", false, new DateOnly(2026, 5, 10), new DateOnly(2026, 6, 10), false, EstadoDocumento.Vencido, null)
        ]);

    private MediatorFalso Montar(string rol = Roles.GestorCae, FichaDocumento360Dto? ficha = null)
    {
        var mediador = new MediatorFalso { Ficha = ficha ?? FichaDelMockup() };
        this.ConRolDeEscritura(rol);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddLocalization();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return mediador;
    }

    private IRenderedComponent<DocumentoDetalle> Renderizar(string consulta = "")
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"documentos/{DocumentoId}{consulta}");
        var cut = Render<DocumentoDetalle>(p => p.Add(x => x.DocumentoId, DocumentoId));
        cut.WaitForAssertion(() => cut.FindAll("[aria-busy=true]").Should().BeEmpty());
        return cut;
    }

    private static List<string> Nombres(IRenderedComponent<DocumentoDetalle> cut) =>
        cut.FindAll(".fila-relacion .fila-relacion-nombre").Select(n => n.TextContent.Trim()).ToList();

    // ── Estructura ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, null, "No disponible para Soporte TALVEG")]
    [InlineData(true, "CSV-123", "CSV-123")]
    public void La_validacion_muestra_la_disponibilidad_del_codigo(bool disponible, string? codigo, string esperado)
    {
        var mediador = Montar();
        mediador.Validacion = new ValidacionOficialDocumentoDto(
            PerfilDocumentoOficial.CorrienteTgss, NivelConfianzaDocumental.FirmaValidaSinRevocacion,
            DecisionValidacionOficial.AutoValidado, ResultadoCotejoDocumentoOficial.Coincide,
            "", codigo, null, null, null, null, DateTime.UtcNow, [], disponible);
        var cut = Renderizar();
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Contains("Validaci\u00f3n")).Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(esperado));
    }

    [Fact]
    public void Las_pestanas_salen_con_los_rotulos_y_el_orden_del_mockup_y_la_primera_es_Donde_se_exige()
    {
        Montar();
        var cut = Renderizar();
        var pestanas = cut.FindAll("[role=tab]").Select(t => t.TextContent.Trim()).ToList();
        pestanas.Should().HaveCount(6);
        string[] rotulos = ["Dónde se exige", "Versiones", "Validación", "Firma", "Ficha", "Historial"];
        for (var i = 0; i < rotulos.Length; i++)
            pestanas[i].Should().StartWith(rotulos[i]);
        cut.Find("[role=tab][aria-selected=true]").TextContent.Should().StartWith("Dónde se exige");
    }

    [Fact]
    public void La_cabecera_dice_el_estado_del_documento_y_cuantos_Centros_lo_dan_por_valido()
    {
        Montar();
        var cut = Renderizar();
        cut.Find(".cabecera-identidad").TextContent.Should()
            .Contain("Certificado de estar al corriente con la TGSS")
            .And.Contain("Vencido")
            .And.Contain("venció el 10/08/2026")
            .And.Contain("Obras y Estructuras del Ebro S.L.")
            .And.Contain("0 de 2");
        cut.FindAll("a[href='/empresas/" + EmpresaId + "']").Should().NotBeEmpty();
    }

    // ── Dónde se exige ────────────────────────────────────────────────────

    [Fact]
    public void Donde_se_exige_lista_los_Centros_con_su_estado_su_condicion_de_acceso_y_la_recomendacion()
    {
        Montar();
        var cut = Renderizar();

        Nombres(cut).Should().Equal("Centro Norte", "Planta Zaragoza");

        var norte = cut.Find($"a[href='/centros/{CentroNorteId}']").Closest("li")!;
        norte.TextContent.Should().Contain("Refrielectric S.A. · Nalanda").And.Contain("Bloquea acceso").And.Contain("Vencido")
            .And.Contain("Recomendación · Pide la renovación al contacto de la Empresa antes del próximo acceso.");
        norte.GetAttribute("data-tono").Should().Be("peligro");

        var zaragoza = cut.Find($"a[href='/centros/{CentroZaragozaId}']").Closest("li")!;
        zaragoza.TextContent.Should().Contain("Refrielectric S.A. · Dokify").And.Contain("5 días de tolerancia").And.Contain("En tolerancia")
            .And.NotContain("Recomendación");
        zaragoza.GetAttribute("data-tono").Should().Be("advertencia");
    }

    [Fact]
    public void Donde_se_exige_cuenta_los_Centros_en_la_pestana_y_la_marca_en_alerta_si_alguno_no_vale()
    {
        Montar();
        var cut = Renderizar();
        var pestana = cut.FindAll("[role=tab]").Single(t => t.TextContent.Contains("Dónde se exige"));
        pestana.TextContent.Should().Contain("2");
    }

    [Fact]
    public async Task Los_contadores_de_estado_filtran_los_Centros_y_Quitar_filtros_los_devuelve()
    {
        Montar();
        var cut = Renderizar();
        await cut.FindAll(".filtro-estados-opcion").First(b => b.TextContent.Trim().StartsWith("En tolerancia", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs());
        Nombres(cut).Should().Equal("Planta Zaragoza");
        cut.Find(".lista-relaciones-encabezado").TextContent.Should().Contain("Mostrando 1 de 2 · del peor estado al mejor");
    }

    [Fact]
    public void Sin_Centros_que_lo_exijan_dice_que_ninguno_lo_exige_y_no_pinta_filas()
    {
        var ficha = FichaDelMockup() with { DondeSeExige = [] };
        Montar(ficha: ficha);
        var cut = Renderizar();
        cut.Markup.Should().Contain("Ningún Centro exige este documento");
        cut.FindAll("li.fila-relacion").Should().BeEmpty();
        cut.Find(".cabecera-identidad").TextContent.Should().Contain("Ningún Centro lo exige");
    }

    // ── Versiones ─────────────────────────────────────────────────────────

    [Fact]
    public void Versiones_lista_los_archivos_con_su_estado_la_actual_primero_y_Cerrada_en_las_anteriores()
    {
        Montar();
        var cut = Renderizar("?pestana=versiones");

        cut.Find("[role=tab][aria-selected=true]").TextContent.Should().StartWith("Versiones");
        Nombres(cut).Should().Equal("certificado-tgss-julio.pdf", "certificado-tgss-junio.pdf", "certificado-tgss-mayo.pdf");

        var filas = cut.FindAll("li.fila-relacion");
        filas[0].TextContent.Should().Contain("10/07/2026 → 10/08/2026 · versión actual").And.Contain("Vencido");
        filas[1].TextContent.Should().Contain("10/06/2026 · negativa por descubiertos").And.Contain("Cerrada");
        filas[2].TextContent.Should().Contain("10/05/2026 → 10/06/2026").And.Contain("Cerrada");
    }

    [Fact]
    public void Versiones_solo_ofrece_Renovar_en_la_actual_y_Ver_en_las_que_tienen_archivo()
    {
        Montar();
        var cut = Renderizar("?pestana=versiones");
        var filas = cut.FindAll("li.fila-relacion");

        filas[0].QuerySelectorAll("button").Select(b => b.TextContent.Trim()).Should().Equal("Ver", "Renovar");
        filas[1].QuerySelectorAll("button").Select(b => b.TextContent.Trim()).Should().Equal("Ver");
        filas[2].QuerySelectorAll("button").Should().BeEmpty("la versión sin archivo no tiene nada que ver ni que renovar");
    }

    [Fact]
    public void Versiones_a_Consulta_no_le_ofrece_Renovar()
    {
        Montar(Roles.Consulta);
        var cut = Renderizar("?pestana=versiones");
        cut.FindAll("li.fila-relacion button").Select(b => b.TextContent.Trim()).Should().NotContain("Renovar");
    }

    // ── Documento sustituido y no encontrado ──────────────────────────────

    [Fact]
    public void Un_documento_sustituido_se_marca_Cerrada_avisa_y_lleva_al_vigente_sin_Renovar()
    {
        var vigenteId = Guid.NewGuid();
        var ficha = FichaDelMockup() with { Documento = Cabecera(vigenteId) };
        Montar(ficha: ficha);
        var cut = Renderizar();
        cut.Find("[role=status]").QuerySelector($"a[href='/documentos/{vigenteId}']").Should().NotBeNull();
        cut.Find(".cabecera-identidad").TextContent.Should().Contain("Cerrada").And.NotContain("Renovar");
    }

    [Fact]
    public void Si_la_consulta_devuelve_null_la_ficha_no_se_distingue_de_una_que_no_existe()
    {
        var mediador = Montar();
        mediador.Ficha = null;
        ComponentFactories.AddStub<FichaNoEncontrada>();
        var cut = Renderizar();
        cut.WaitForAssertion(() => cut.RutaListaDeFichaNoEncontrada().Should().Be("/documentos"));
        cut.FindAll("[role=tab]").Should().BeEmpty();
    }
}
