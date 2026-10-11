using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
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
/// S4: un documento «Sin confirmar» no tiene fecha de vencimiento, pero eso NO es «no caduca». La celda de
/// vigencia del tercer nivel de Centro 360 decía «Sin caducidad» junto al badge ámbar «Sin confirmar» (el modelo
/// eliminó el «nulo = no caduca»; decisión del propietario del 2026-10-01: «Sin confirmar» es un estado propio,
/// al día con aviso). Ahora dice «Sin confirmar» y lleva el mismo aviso ámbar que el panel Documentación base;
/// «Sin caducidad» queda solo para lo confirmado como tal, y un estado sin fecha que no es ninguno de los dos
/// (un hueco) se rotula «—».
/// </summary>
public class AcordeonAsignacionesCentroVigenciaSinConfirmarTests : BunitContext
{
    public AcordeonAsignacionesCentroVigenciaSinConfirmarTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public IReadOnlyList<TrabajadorAsignacionDocumentacionDto> Trabajadores { get; init; } = [];

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
        Services.AddLocalization();
        this.ConRolDeEscritura();
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    }

    /// <summary>Texto y clase de la celda de vigencia del único documento del tercer nivel de un Trabajador.</summary>
    private async Task<(string Texto, string Clase)> CeldaDeTrabajadorAsync(EstadoDocumento estado, DateOnly? fecha)
    {
        var trabajador = new TrabajadorAsignacionDocumentacionDto(
            Guid.NewGuid(), Guid.NewGuid(), "Ruiz Peña, Ana", new DateOnly(2026, 1, 15), estado,
            [new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Reconocimiento médico", estado, fecha)],
            CumplimientoDocumental.Evaluar([estado]));

        RegistrarServicios(new MediatorFalso { Trabajadores = [trabajador] });

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, Guid.NewGuid())
            .Add(a => a.CentroNombre, "Centro Logístico Norte"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));
        await cut.Find("button.fila-relacion-desplegar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("tabla-documentos-requeridos"));

        var celda = cut.Find(".tabla-documentos-requeridos .celda-documento-vigencia");
        return (celda.TextContent.Trim(), celda.ClassName ?? "");
    }

    private (string Texto, string Clase) CeldaDeEmpresa(EstadoDocumento estado, DateOnly? fecha)
    {
        RegistrarServicios(new MediatorFalso());

        var incidencia = new IncidenciaCentroDto(
            "Plan de prevención — Empresa", AmbitoCausa.Empresa, estado, Guid.NewGuid(), Guid.NewGuid(), fecha);

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, Guid.NewGuid())
            .Add(a => a.CentroNombre, "Centro Logístico Norte")
            .Add(a => a.EmpresaId, Guid.NewGuid())
            .Add(a => a.EmpresaNombre, "Empresa Recuentos S.L.")
            .Add(a => a.IncidenciasEmpresa, [incidencia]));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("tabla-documentos-requeridos"));
        var celda = cut.Find(".tabla-documentos-requeridos .celda-documento-vigencia");
        return (celda.TextContent.Trim(), celda.ClassName ?? "");
    }

    [Fact]
    public async Task Un_documento_de_Trabajador_sin_confirmar_dice_Sin_confirmar_con_aviso_y_nunca_Sin_caducidad()
    {
        var (texto, clase) = await CeldaDeTrabajadorAsync(EstadoDocumento.SinConfirmar, fecha: null);

        texto.Should().Be("Sin confirmar");
        texto.Should().NotContain("Sin caducidad", "sin fecha no es «no caduca»: nadie ha anotado hasta cuándo vale");
        clase.Should().Contain("celda-documento-vigencia-sin-confirmar", "lleva el mismo aviso ámbar que el panel Documentación base");
    }

    [Fact]
    public async Task Un_documento_de_Trabajador_confirmado_como_sin_caducidad_sigue_diciendo_Sin_caducidad_sin_aviso()
    {
        var (texto, clase) = await CeldaDeTrabajadorAsync(EstadoDocumento.SinCaducidad, fecha: null);

        texto.Should().Be("Sin caducidad");
        clase.Should().NotContain("celda-documento-vigencia-sin-confirmar", "lo confirmado como no caducable no es un aviso");
    }

    [Fact]
    public async Task Un_documento_con_fecha_conserva_su_texto_y_no_lleva_el_aviso()
    {
        var (texto, clase) = await CeldaDeTrabajadorAsync(EstadoDocumento.Vigente, new DateOnly(2027, 3, 14));

        texto.Should().Be("Caduca 14/03/2027");
        clase.Should().NotContain("celda-documento-vigencia-sin-confirmar");
    }

    [Fact]
    public void Una_incidencia_de_Empresa_sin_confirmar_dice_Sin_confirmar_con_aviso()
    {
        var (texto, clase) = CeldaDeEmpresa(EstadoDocumento.SinConfirmar, fecha: null);

        texto.Should().Be("Sin confirmar");
        clase.Should().Contain("celda-documento-vigencia-sin-confirmar");
    }

    [Fact]
    public void Una_incidencia_de_Empresa_sin_fecha_que_no_es_ni_sin_confirmar_ni_sin_caducidad_no_afirma_una_vigencia()
    {
        var (texto, clase) = CeldaDeEmpresa(EstadoDocumento.Faltante, fecha: null);

        texto.Should().Be("—", "un hueco no tiene vigencia que rotular; antes decía «Sin caducidad»");
        clase.Should().NotContain("celda-documento-vigencia-sin-confirmar");
    }
    private static TrabajadorAsignacionDocumentacionDto Asignado(string nombre, EstadoDocumento estado) =>
        new(Guid.NewGuid(), Guid.NewGuid(), nombre, new DateOnly(2026, 1, 15), estado,
            [new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Reconocimiento médico", estado, null)],
            CumplimientoDocumental.Evaluar([estado]));

    [Theory]
    [InlineData(EstadoDocumento.Vencido, "peligro")]
    [InlineData(EstadoDocumento.Faltante, "peligro")]
    [InlineData(EstadoDocumento.EnTolerancia, "advertencia")]
    [InlineData(EstadoDocumento.Urgente, "advertencia")]
    [InlineData(EstadoDocumento.Proximo, null)]
    [InlineData(EstadoDocumento.SinConfirmar, null)]
    [InlineData(EstadoDocumento.Vigente, null)]
    [InlineData(EstadoDocumento.SinCaducidad, null)]
    public void La_fila_de_Trabajador_usa_el_tinte_canonico(EstadoDocumento estado, string? tono)
    {
        RegistrarServicios(new MediatorFalso { Trabajadores = [Asignado("Ana Ruiz", estado)] });
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(x => x.CentroId, Guid.NewGuid()).Add(x => x.CentroNombre, "Centro Norte"));
        cut.FindAll(".lista-trabajadores-acordeon [data-pieza=fila]").Should().ContainSingle()
            .Which.GetAttribute("data-tono").Should().Be(tono);
    }

    [Fact]
    public void El_orden_es_gravedad_y_conserva_el_desempate_por_apellidos_y_nombre_del_lector()
    {
        // Orden que entrega el lector (apellidos/nombre), deliberadamente distinto del nombre completo.
        var filas = new[]
        {
            Asignado("Zoe Alba", EstadoDocumento.SinConfirmar),
            Asignado("Ana Zulu", EstadoDocumento.SinConfirmar),
            Asignado("Luis Barco", EstadoDocumento.Vencido),
            Asignado("Eva Cano", EstadoDocumento.Faltante),
            Asignado("Paz Diaz", EstadoDocumento.Urgente)
        };
        RegistrarServicios(new MediatorFalso { Trabajadores = filas });
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(x => x.CentroId, Guid.NewGuid()).Add(x => x.CentroNombre, "Centro Norte"));
        cut.FindAll(".fila-relacion-nombre").Select(x => x.TextContent.Trim()).Should()
            .Equal("Luis Barco", "Eva Cano", "Paz Diaz", "Zoe Alba", "Ana Zulu");
    }

    [Fact]
    public void Bloqueado_se_ordena_primero_con_tinte_rojo_y_motivo_visible_sin_desplegar()
    {
        var vencido = Asignado("Ana Alba", EstadoDocumento.Vencido);
        var bloqueado = Asignado("Zoe Zulu", EstadoDocumento.Vigente);
        var centroId = Guid.NewGuid();
        RegistrarServicios(new MediatorFalso { Trabajadores = [vencido, bloqueado] });
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(x => x.CentroId, centroId)
            .Add(x => x.CentroNombre, "Centro Norte").Add(x => x.Bloqueos,
                [new(centroId, "Centro Norte", bloqueado.TrabajadorId, bloqueado.TrabajadorNombre, Guid.NewGuid(), "Formación PRL")]));
        var fila = cut.Find(".lista-trabajadores-acordeon [data-pieza=fila]");
        fila.QuerySelector(".fila-relacion-nombre")!.TextContent.Should().Be("Zoe Zulu");
        fila.GetAttribute("data-tono").Should().Be("peligro");
        fila.QuerySelector(".fila-relacion-desplegar")!.GetAttribute("aria-expanded").Should().Be("false");
        cut.Find(".motivo-bloqueo").TextContent.Should().Contain("Formación PRL");
    }

    [Fact]
    public async Task Seleccion_y_expansion_distinguen_AsignacionId_aunque_el_Trabajador_se_repita()
    {
        var primero = Asignado("Ana Ruiz", EstadoDocumento.Vencido);
        var segundo = Asignado("Ana Ruiz", EstadoDocumento.Vencido) with { TrabajadorId = primero.TrabajadorId };
        RegistrarServicios(new MediatorFalso { Trabajadores = [primero, segundo] });
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(x => x.CentroId, Guid.NewGuid())
            .Add(x => x.CentroNombre, "Centro Norte").Add(x => x.SeleccionMultiple, true));
        var filas = cut.FindAll(".lista-trabajadores-acordeon [data-pieza=fila]");
        filas[1].QuerySelector("input")!.Change(true);
        filas = cut.FindAll(".lista-trabajadores-acordeon [data-pieza=fila]");
        filas[0].QuerySelector("input")!.HasAttribute("checked").Should().BeFalse();
        filas[1].QuerySelector("input")!.HasAttribute("checked").Should().BeTrue();
        await filas[1].QuerySelector(".fila-relacion-desplegar")!.ClickAsync(new MouseEventArgs());
        var botones = cut.FindAll(".fila-relacion-desplegar");
        botones[0].GetAttribute("aria-expanded").Should().Be("false");
        botones[1].GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll(".tabla-documentos-requeridos").Should().ContainSingle();
        await botones[1].ClickAsync(new MouseEventArgs());
        cut.FindAll(".tabla-documentos-requeridos").Should().BeEmpty();
    }

    [Fact]
    public async Task Nombre_y_Detalles_abren_el_panel_del_Trabajador_y_no_la_Asignacion()
    {
        var trabajador = Asignado("Ana Ruiz", EstadoDocumento.Vigente);
        RegistrarServicios(new MediatorFalso { Trabajadores = [trabajador] });
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(x => x.CentroId, Guid.NewGuid()).Add(x => x.CentroNombre, "Centro Norte"));
        await cut.Find(".fila-relacion-nombre").ClickAsync(new MouseEventArgs());
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual!.EntidadId.Should().Be(trabajador.TrabajadorId);
        await cut.Find(".fila-relacion-acciones .accion-fila-enlace").ClickAsync(new MouseEventArgs());
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual!.EntidadId.Should().Be(trabajador.TrabajadorId);
    }

}
