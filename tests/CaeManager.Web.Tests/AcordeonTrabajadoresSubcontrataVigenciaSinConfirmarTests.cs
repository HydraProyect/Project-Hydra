using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas.Queries.ObtenerTrabajadoresDocumentacionPorSubcontrata;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Subcontratas.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Sin confirmar» sin fecha de vencimiento no es «—» (decisión del propietario, 2026-10-03): «—» oculta un estado
/// conocido. La celda de vigencia del tercer nivel de Subcontrata 360 dice «Sin confirmar» con el mismo aviso ámbar
/// que Centro 360 (misma función, <c>EstadoDocumentoUi.TextoSinFechaDeVencimiento</c>); un estado sin fecha que no es
/// «Sin caducidad» ni «Sin confirmar» (un hueco) sigue en «—», sin aviso.
/// </summary>
public class AcordeonTrabajadoresSubcontrataVigenciaSinConfirmarTests : BunitContext
{
    public AcordeonTrabajadoresSubcontrataVigenciaSinConfirmarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private sealed class MediatorFalso : IMediator
    {
        public required IReadOnlyList<TrabajadorDocumentacionSubcontrataDto> Trabajadores { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerTrabajadoresDocumentacionPorSubcontrataQuery => Trabajadores,
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

    /// <summary>Texto y clase de la celda de vigencia del único documento del tercer nivel de un Trabajador.</summary>
    private async Task<(string Texto, string Clase)> CeldaAsync(EstadoDocumento estado, DateOnly? fecha)
    {
        var trabajador = new TrabajadorDocumentacionSubcontrataDto(
            Guid.NewGuid(), "Ruiz Peña, Ana", "12345678A", estado,
            [new DocumentoRequeridoDto(estado == EstadoDocumento.Faltante ? null : Guid.NewGuid(), Guid.NewGuid(), "Reconocimiento médico", estado, fecha)],
            CumplimientoDocumental.Evaluar([estado]));

        RegistrarServicios(new MediatorFalso { Trabajadores = [trabajador] });

        var cut = Render<AcordeonTrabajadoresSubcontrata>(p => p
            .Add(a => a.SubcontrataId, Guid.NewGuid()));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));
        await cut.Find("button.boton-expandir-fila").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("tabla-documentos-requeridos"));

        var celda = cut.Find(".tabla-documentos-requeridos .celda-documento-vigencia");
        return (celda.TextContent.Trim(), celda.ClassName ?? "");
    }

    [Fact]
    public async Task Un_documento_sin_confirmar_dice_Sin_confirmar_con_aviso_y_no_raya_ni_Sin_caducidad()
    {
        var (texto, clase) = await CeldaAsync(EstadoDocumento.SinConfirmar, fecha: null);

        texto.Should().Be("Sin confirmar");
        clase.Should().Contain("celda-documento-vigencia-sin-confirmar", "lleva el mismo aviso ámbar que Centro 360");
    }

    [Fact]
    public async Task Un_documento_confirmado_sin_caducidad_dice_Sin_caducidad_sin_aviso()
    {
        var (texto, clase) = await CeldaAsync(EstadoDocumento.SinCaducidad, fecha: null);

        texto.Should().Be("Sin caducidad");
        clase.Should().NotContain("celda-documento-vigencia-sin-confirmar");
    }

    [Fact]
    public async Task Un_hueco_sin_fecha_sigue_en_raya_sin_aviso()
    {
        var (texto, clase) = await CeldaAsync(EstadoDocumento.Faltante, fecha: null);

        texto.Should().Be("—", "un estado sin fecha que no es «Sin caducidad» ni «Sin confirmar» no tiene vigencia que rotular");
        clase.Should().NotContain("celda-documento-vigencia-sin-confirmar");
    }

    [Fact]
    public async Task Un_documento_con_fecha_pinta_la_fecha_sin_aviso()
    {
        var (texto, clase) = await CeldaAsync(EstadoDocumento.Vigente, new DateOnly(2027, 3, 1));

        texto.Should().Be("01/03/2027");
        clase.Should().NotContain("celda-documento-vigencia-sin-confirmar");
    }
}
