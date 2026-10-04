using System.Globalization;
using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// «En tolerancia» (aprobado 2026-10-03): un Documento vencido que en ESTE Centro aún vale para acceder se rotula «Vencido · en
/// tolerancia hasta dd/MM», con severidad entre Vencido y Urgente. Solo lo producen las vistas con contexto de Centro; sigue
/// siendo un vencido para los recuentos y los filtros de «vencidos» (esconderlo sería contar menos de lo que hay), y su color es
/// el ámbar de «lo que pide acción pero no es rojo».
/// </summary>
public class EstadoEnToleranciaDocumentoTests : BunitContext
{
    private static readonly DateOnly Venció = new(2026, 10, 3);
    private static readonly DateOnly Hasta = new(2026, 10, 15);

    public EstadoEnToleranciaDocumentoTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Fact]
    public void El_rotulo_con_fecha_dice_vencido_en_tolerancia_hasta_dd_MM_y_sin_fecha_el_rotulo_corto()
    {
        EstadoDocumentoUi.Texto(EstadoDocumento.EnTolerancia, Hasta).Should().Be("Vencido · en tolerancia hasta 15/10");
        EstadoDocumentoUi.Texto(EstadoDocumento.EnTolerancia, null).Should().Be("En tolerancia");
        EstadoDocumentoUi.Texto(EstadoDocumento.EnTolerancia).Should().Be("En tolerancia");
        EstadoDocumentoUi.Texto(EstadoDocumento.Vencido, Hasta).Should().Be("Vencido", "la fecha solo rotula a un vencido en tolerancia");
    }

    [Fact]
    public void El_rotulo_tiene_su_version_en_catalan()
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("ca-ES");

            EstadoDocumentoUi.Texto(EstadoDocumento.EnTolerancia, Hasta).Should().Be("Vençut · en tolerància fins al 15/10");
            EstadoDocumentoUi.Texto(EstadoDocumento.EnTolerancia).Should().Be("En tolerància");
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Fact]
    public void Es_ambar_no_rojo_y_cuenta_como_vencido_para_los_recuentos()
    {
        EstadoDocumentoUi.Tono(EstadoDocumento.EnTolerancia).Should().Be(TonoBadge.Advertencia);
        EstadoDocumentoUi.Tono(EstadoDocumento.Vencido).Should().Be(TonoBadge.Peligro, "control: el vencido sin tolerancia sigue en rojo");
        EstadoDocumentoUi.HaVencido(EstadoDocumento.EnTolerancia).Should().BeTrue();
        EstadoDocumentoUi.HaVencido(EstadoDocumento.Vencido).Should().BeTrue();
        EstadoDocumentoUi.HaVencido(EstadoDocumento.Urgente).Should().BeFalse();
        EstadoDocumentoUi.HaVencido(EstadoDocumento.Faltante).Should().BeFalse();
    }

    private sealed class MediatorFalso(IReadOnlyList<TrabajadorAsignacionDocumentacionDto> trabajadores) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerAsignacionesDocumentacionPorCentroQuery => trabajadores,
                ObtenerDocumentacionBloqueantePendienteQuery => (IReadOnlyList<DocumentacionBloqueantePendienteDto>)[],
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

    private sealed class SinArchivos : IFileStorageService
    {
        private static Exception NoDeberia() => new NotSupportedException("Con el drawer cerrado no se abre ningún archivo.");
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class SinConversor : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Con el drawer cerrado no se convierte nada.");
    }

    private static TrabajadorAsignacionDocumentacionDto Trabajador(string nombre, EstadoDocumento estadoDelDocumento, DateOnly? hasta = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), nombre, new DateOnly(2026, 1, 15), estadoDelDocumento,
            [new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Formación PRL", estadoDelDocumento, Venció, EnToleranciaHasta: hasta)],
            // El cumplimiento mide el estado de vigencia real: un vencido en tolerancia no está al día todavía.
            CumplimientoDocumental.Evaluar([estadoDelDocumento == EstadoDocumento.EnTolerancia ? EstadoDocumento.Vencido : estadoDelDocumento]));

    private IRenderedComponent<AcordeonAsignacionesCentro> Renderizar(params TrabajadorAsignacionDocumentacionDto[] trabajadores) =>
        Renderizar(mostrarTotales: false, trabajadores);

    private IRenderedComponent<AcordeonAsignacionesCentro> Renderizar(bool mostrarTotales, params TrabajadorAsignacionDocumentacionDto[] trabajadores)
    {
        Services.AddLocalization();
        this.ConRolDeEscritura();
        Services.AddScoped<IMediator>(_ => new MediatorFalso(trabajadores));
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IFileStorageService, SinArchivos>();
        Services.AddScoped<IConversorWordPdfService, SinConversor>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, Guid.NewGuid())
            .Add(a => a.CentroNombre, "Centro Logístico Norte")
            .Add(a => a.MostrarTotales, mostrarTotales));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(trabajadores[0].TrabajadorNombre));
        return cut;
    }

    [Fact]
    public async Task La_fila_del_documento_dice_vencido_en_tolerancia_hasta_y_la_vigencia_sigue_diciendo_cuando_vencio()
    {
        var cut = Renderizar(Trabajador("Ruiz Peña, Ana", EstadoDocumento.EnTolerancia, Hasta));

        await cut.Find("button.boton-expandir-fila").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("tabla-documentos-requeridos"));

        var fila = cut.Find(".tabla-documentos-requeridos .fila-documento-requerido");
        fila.TextContent.Should().Contain("Vencido · en tolerancia hasta 15/10");
        fila.TextContent.Should().Contain("Vencio 03/10/2026", "la fecha de vencimiento del Documento no cambia: la tolerancia no lo alarga");
    }

    [Fact]
    public void Un_vencido_en_tolerancia_sigue_contando_en_los_vencidos_del_Trabajador()
    {
        var cut = Renderizar(Trabajador("Ruiz Peña, Ana", EstadoDocumento.EnTolerancia, Hasta));

        cut.Markup.Should().Contain("1 documento vencido", "esconderlo de la ranura de vencidos contaría menos de lo que hay");
    }

    [Fact]
    public void La_ventana_de_vencidos_describe_al_documento_en_tolerancia_con_su_ultimo_dia()
    {
        var cut = Renderizar(Trabajador("Ruiz Peña, Ana", EstadoDocumento.EnTolerancia, Hasta));

        cut.FindAll(".ventana-linea").Select(l => l.TextContent.Trim())
            .Should().ContainSingle().Which.Should().Be("Formación PRL — Vencido · en tolerancia hasta 15/10");
    }

    [Fact]
    public void Los_totales_del_Centro_cuentan_al_vencido_en_tolerancia_como_vencido_y_no_como_al_dia()
    {
        var cut = Renderizar(true, Trabajador("Ruiz Peña, Ana", EstadoDocumento.EnTolerancia, Hasta));

        var totales = cut.Find("[data-totales-documentales]").TextContent;
        System.Text.RegularExpressions.Regex.Replace(totales, @"\s+", " ").Should()
            .Contain("1 exigidos · 0 al día · 1 vencidos · 0 faltantes");
    }

    [Theory]
    [InlineData("Vencido", true)]
    [InlineData("EnTolerancia", true)]
    [InlineData("Urgente", false)]
    [InlineData("Faltante", false)]
    public void El_filtro_de_vencidos_incluye_al_vencido_en_tolerancia_y_el_de_en_tolerancia_solo_a_el(string filtro, bool seVe)
    {
        var enTolerancia = Trabajador("Ruiz Peña, Ana", EstadoDocumento.EnTolerancia, Hasta);
        var otro = Trabajador("Gil Soto, Marta", EstadoDocumento.Vigente);
        var cut = Renderizar(enTolerancia, otro);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Gil Soto, Marta"));

        cut.Render(p => p.Add(a => a.FiltroEstado, filtro));

        if (seVe)
            cut.Markup.Should().Contain("Ruiz Peña, Ana");
        else
            cut.Markup.Should().NotContain("Ruiz Peña, Ana");
        cut.Markup.Should().NotContain("Gil Soto, Marta", "el Trabajador al día no tiene ningún documento en esos estados");
        (cut.Markup.Contains("Ruiz Peña, Ana")).Should().Be(seVe);
    }
}
