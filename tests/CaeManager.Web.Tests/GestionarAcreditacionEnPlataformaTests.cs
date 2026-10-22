using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands;
using CaeManager.Application.Documentos.Commands.CambiarEstadoAcreditacionEnCentro;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionAceptada;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionPendienteDeSubir;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionRechazada;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Regla de Chris del 2026-10-19: el estado del documento en TALVEG (vigencia) y el de su acreditación en cada plataforma
/// son dos estados distintos. En Centro 360 cada fila los enseña en dos líneas; «Gestionar» abre el panel que cambia solo el
/// estado en la plataforma; si el documento falta o está vencido en TALVEG, la acción lleva a la ficha 360 del Trabajador; y
/// va en gris cuando los dos estados están al día.
/// </summary>
public class GestionarAcreditacionEnPlataformaTests : BunitContext
{
    public GestionarAcreditacionEnPlataformaTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public IReadOnlyList<TrabajadorAsignacionDocumentacionDto> Trabajadores { get; init; } = [];
        public List<object> Enviados { get; } = [];
        public static readonly Guid CambioId = Guid.NewGuid();

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            object respuesta = request switch
            {
                ObtenerAsignacionesDocumentacionPorCentroQuery => Trabajadores,
                MarcarAcreditacionAceptadaCommand c => Result.Exito(new ResultadoAnotacionAcreditacionDto(
                    c.AcreditacionId, EstadoAcreditacion.Subida, VigenciaEnPlataforma.SinConfirmar, Guid.NewGuid())),
                CambiarEstadoAcreditacionEnCentroCommand => Result.Exito(CambioId),
                _ when typeof(TResponse) == typeof(Result) => Result.Exito(),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private MediatorFalso Registrar(params TrabajadorAsignacionDocumentacionDto[] trabajadores)
    {
        var mediator = new MediatorFalso { Trabajadores = trabajadores };
        Services.AddLocalization();
        this.ConRolDeEscritura();
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddScoped<IFileStorageService, AlmacenQueNadieToca>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieToca>();
        return mediator;
    }

    private sealed class AlmacenQueNadieToca : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ConversorQueNadieToca : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static TrabajadorAsignacionDocumentacionDto Trabajador(EstadoDocumento estado, Guid? documentoId, params AcreditacionResumenDto[] acreditaciones) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Ruiz Peña, Ana", new DateOnly(2026, 1, 15), estado,
            [new DocumentoRequeridoDto(documentoId, Guid.NewGuid(), "Reconocimiento médico", estado, new DateOnly(2027, 3, 14),
                AcreditacionesEnElCentro: acreditaciones)],
            CumplimientoDocumental.Evaluar([estado]));

    private static AcreditacionResumenDto Acreditacion(EstadoAcreditacion estado) => new(Guid.NewGuid(), "Nalanda", estado);

    private async Task<IRenderedComponent<AcordeonAsignacionesCentro>> DesplegadoAsync()
    {
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(a => a.CentroId, Guid.NewGuid()).Add(a => a.CentroNombre, "Centro Norte"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));
        await cut.Find("button.fila-relacion-desplegar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("tabla-documentos-requeridos"));
        return cut;
    }

    [Fact]
    public async Task Cada_fila_ensena_En_TALVEG_y_En_la_plataforma_en_lineas_separadas()
    {
        Registrar(Trabajador(EstadoDocumento.Vigente, Guid.NewGuid(), Acreditacion(EstadoAcreditacion.PendienteDeSubir)));
        var cut = await DesplegadoAsync();

        var talveg = cut.Find(".tabla-documentos-requeridos [data-linea=talveg]");
        var plataforma = cut.Find(".tabla-documentos-requeridos [data-linea=plataforma]");
        talveg.TextContent.Should().Contain("En TALVEG").And.Contain("Vigente").And.NotContain("Nalanda");
        plataforma.TextContent.Should().Contain("En la plataforma").And.Contain("Nalanda: pendiente de envío").And.NotContain("Vigente");
        cut.Find(".celda-documento-nombre").TextContent.Should().NotContain("Nalanda", "el estado en la plataforma no se repite bajo el nombre");
    }

    [Theory]
    [InlineData(EstadoDocumento.Vencido)]
    [InlineData(EstadoDocumento.Faltante)]
    public async Task Si_falta_en_TALVEG_la_accion_lleva_a_la_ficha_del_Trabajador_y_no_abre_el_panel(EstadoDocumento estado)
    {
        var trabajador = Trabajador(estado, estado == EstadoDocumento.Faltante ? null : Guid.NewGuid(), Acreditacion(EstadoAcreditacion.Aceptada));
        Registrar(trabajador);
        var cut = await DesplegadoAsync();

        var accion = cut.Find("[data-accion=actualizar-en-ficha]");
        accion.TextContent.Trim().Should().Be("Actualizar en Trabajador 360");
        accion.GetAttribute("href").Should().Be($"/trabajadores/{trabajador.TrabajadorId}");
        cut.FindAll("[data-accion=gestionar-acreditacion]").Should().BeEmpty();
    }

    [Fact]
    public async Task Gestionar_va_en_gris_solo_cuando_los_dos_estados_estan_al_dia()
    {
        Registrar(Trabajador(EstadoDocumento.Vigente, Guid.NewGuid(), Acreditacion(EstadoAcreditacion.Aceptada)));
        var cut = await DesplegadoAsync();

        cut.Find("[data-accion=gestionar-acreditacion]").ClassList.Should().Contain("gestionar-discreto");
    }

    [Fact]
    public async Task Gestionar_no_va_en_gris_si_falta_subir_a_la_plataforma_aunque_este_vigente_en_TALVEG()
    {
        Registrar(Trabajador(EstadoDocumento.Vigente, Guid.NewGuid(), Acreditacion(EstadoAcreditacion.PendienteDeSubir)));
        var cut = await DesplegadoAsync();

        cut.Find("[data-accion=gestionar-acreditacion]").ClassList.Should().NotContain("gestionar-discreto");
    }

    [Fact]
    public async Task Gestionar_abre_el_panel_de_acreditacion_con_el_estado_en_TALVEG_de_solo_lectura()
    {
        Registrar(Trabajador(EstadoDocumento.Vigente, Guid.NewGuid(), Acreditacion(EstadoAcreditacion.PendienteDeSubir)));
        var cut = await DesplegadoAsync();

        await cut.Find("[data-accion=gestionar-acreditacion]").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Gestionar acreditación en plataforma"));
        cut.Find("[data-estado-talveg]").TextContent.Should().Contain("Vigente");
        cut.FindAll("[role=radio].ga-op").Should().HaveCount(4);
        cut.Find("[data-estado-plataforma]").TextContent.Should().Contain("Pendiente de subir");
    }

    private async Task<IRenderedComponent<AcordeonAsignacionesCentro>> DesplegadoConEstadoDePlataformaAsync()
    {
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(a => a.CentroId, Guid.NewGuid()).Add(a => a.CentroNombre, "Centro Norte")
            .Add(a => a.MostrarEstadoPlataforma, true).Add(a => a.NombrePlataforma, "Nalanda"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));
        await cut.Find("button.fila-relacion-desplegar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("tabla-plataforma-centro"));
        return cut;
    }

    [Fact]
    public async Task En_Centro_360_la_tabla_de_plataforma_rotula_su_columna_y_anade_la_columna_Accion_sin_Gestionar()
    {
        Registrar(Trabajador(EstadoDocumento.Vigente, Guid.NewGuid(), Acreditacion(EstadoAcreditacion.PendienteDeSubir)));
        var cut = await DesplegadoConEstadoDePlataformaAsync();

        var cabecera = cut.Find(".cabecera-plataforma-centro").TextContent;
        cabecera.Should().Contain("Estado en Nalanda").And.Contain("Acción");
        cut.Find(".fila-plataforma-centro [data-accion=marcar-rapido-plataforma]").TextContent.Trim().Should().Be("Marcar subido");
        cut.FindAll(".fila-plataforma-centro [data-accion=cambiar-estado-plataforma]").Should().ContainSingle();
        cut.FindAll("[data-accion=gestionar-acreditacion]").Should().BeEmpty("en Centro 360 la columna «Acción» sustituye a «Gestionar»");
    }

    [Theory]
    [InlineData(EstadoAcreditacion.Rechazada)]
    [InlineData(EstadoAcreditacion.PendienteDeSubir)]
    public async Task En_Centro_360_un_documento_que_bloquea_en_la_plataforma_cuenta_como_Trabajador_bloqueado(EstadoAcreditacion estado)
    {
        // Fila 8 del probador (ILL-01): el chip decía «0 Bloqueado» mientras la vista enseñaba los bloqueos de plataforma.
        Registrar(Trabajador(EstadoDocumento.Vigente, Guid.NewGuid(), Acreditacion(estado)));
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(a => a.CentroId, Guid.NewGuid()).Add(a => a.CentroNombre, "Centro Norte")
            .Add(a => a.MostrarEstadoPlataforma, true).Add(a => a.NombrePlataforma, "Nalanda")
            .Add(a => a.Bloqueos, Array.Empty<CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente.DocumentacionBloqueantePendienteDto>()));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));

        var chip = cut.FindAll("button").Single(b => b.TextContent.Contains("Bloquea acceso"));
        chip.TextContent.Should().Contain("1");
        cut.Find(".fila-relacion").TextContent.Should().Contain("Bloqueado");
    }

    [Fact]
    public async Task En_Centro_360_un_documento_validado_en_la_plataforma_no_bloquea_al_Trabajador()
    {
        Registrar(Trabajador(EstadoDocumento.Vigente, Guid.NewGuid(), Acreditacion(EstadoAcreditacion.Aceptada)));
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(a => a.CentroId, Guid.NewGuid()).Add(a => a.CentroNombre, "Centro Norte")
            .Add(a => a.MostrarEstadoPlataforma, true).Add(a => a.NombrePlataforma, "Nalanda")
            .Add(a => a.Bloqueos, Array.Empty<CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente.DocumentacionBloqueantePendienteDto>()));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Bloquea acceso")).TextContent.Should().Contain("0");
        await Task.CompletedTask;
    }

    private (IRenderedComponent<AccionEstadoPlataforma>, MediatorFalso, AcreditacionResumenDto, List<int>) Accion(EstadoAcreditacion estado)
    {
        var mediator = Registrar();
        var acreditacion = Acreditacion(estado);
        var recargas = new List<int>();
        var cut = Render<AccionEstadoPlataforma>(p => p
            .Add(x => x.CentroId, Guid.NewGuid())
            .Add(x => x.CentroNombre, "Centro Norte")
            .Add(x => x.Acreditacion, acreditacion)
            .Add(x => x.DocumentoNombre, "Reconocimiento médico")
            .Add(x => x.TitularNombre, "Ruiz Peña, Ana")
            .Add(x => x.OnCambio, () => recargas.Add(1)));
        return (cut, mediator, acreditacion, recargas);
    }

    [Theory]
    [InlineData(EstadoAcreditacion.PendienteDeSubir, "Marcar subido", EstadoAcreditacion.Subida)]
    [InlineData(EstadoAcreditacion.Rechazada, "Marcar subido", EstadoAcreditacion.Subida)]
    [InlineData(EstadoAcreditacion.Subida, "Marcar validado", EstadoAcreditacion.Aceptada)]
    public async Task El_boton_rapido_marca_el_siguiente_estado_avisa_con_Deshacer_y_recarga(
        EstadoAcreditacion estado, string texto, EstadoAcreditacion destino)
    {
        var (cut, mediator, acreditacion, recargas) = Accion(estado);
        var boton = cut.Find("[data-accion=marcar-rapido-plataforma]");
        boton.TextContent.Trim().Should().Be(texto);

        await boton.ClickAsync(new MouseEventArgs());

        var enviado = mediator.Enviados.OfType<CambiarEstadoAcreditacionEnCentroCommand>().Should().ContainSingle().Subject;
        enviado.AcreditacionId.Should().Be(acreditacion.Id);
        enviado.Estado.Should().Be(destino);
        recargas.Should().HaveCount(1);
        var toast = Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Subject;
        toast.TextoAccion.Should().Be("Deshacer");

        await Services.GetRequiredService<ToastService>().EjecutarAccionAsync(toast.Id);

        var deshacer = mediator.Enviados.OfType<DeshacerCambioEstadoAcreditacionCommand>().Should().ContainSingle().Subject;
        deshacer.AcreditacionId.Should().Be(acreditacion.Id);
        deshacer.CambioId.Should().Be(MediatorFalso.CambioId);
        recargas.Should().HaveCount(2);
    }

    [Fact]
    public void Validado_no_tiene_boton_rapido_pero_si_Cambiar()
    {
        var (cut, _, _, _) = Accion(EstadoAcreditacion.Aceptada);

        cut.FindAll("[data-accion=marcar-rapido-plataforma]").Should().BeEmpty();
        cut.FindAll("[data-accion=cambiar-estado-plataforma]").Should().ContainSingle();
    }

    [Fact]
    public async Task Cambiar_abre_el_modal_con_cuatro_tarjetas_y_el_estado_actual_marcado()
    {
        var (cut, _, _, _) = Accion(EstadoAcreditacion.Subida);

        await cut.Find("[data-accion=cambiar-estado-plataforma]").ClickAsync(new MouseEventArgs());

        cut.FindAll(".tarjeta-estado-plataforma").Should().HaveCount(4);
        cut.Find(".tarjeta-estado-plataforma-elegida").GetAttribute("data-estado-plataforma").Should().Be(nameof(EstadoAcreditacion.Subida));
        cut.FindAll("[data-campo=motivo-rechazo]").Should().BeEmpty("el motivo solo se pide para un rechazo");
    }

    [Fact]
    public async Task Rechazado_sin_motivo_no_se_envia_y_con_motivo_envia_fecha_motivo_y_nota()
    {
        var (cut, mediator, acreditacion, recargas) = Accion(EstadoAcreditacion.Subida);
        await cut.Find("[data-accion=cambiar-estado-plataforma]").ClickAsync(new MouseEventArgs());
        await cut.Find("[data-estado-plataforma=Rechazada] input").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "on" });

        await cut.Find("[data-modal-estado-plataforma]").Closest("form")!.SubmitAsync();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Escribe el motivo"));
        mediator.Enviados.OfType<CambiarEstadoAcreditacionEnCentroCommand>().Should().BeEmpty();

        await cut.Find("[data-campo=motivo-rechazo] input, input[data-campo=motivo-rechazo]").InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Falta la firma" });
        await cut.Find("[data-campo=nota-estado] input, input[data-campo=nota-estado]").InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Solicitud 4711" });
        await cut.Find("[data-modal-estado-plataforma]").Closest("form")!.SubmitAsync();

        cut.WaitForAssertion(() => mediator.Enviados.OfType<CambiarEstadoAcreditacionEnCentroCommand>().Should().ContainSingle());
        var enviado = mediator.Enviados.OfType<CambiarEstadoAcreditacionEnCentroCommand>().Single();
        enviado.Should().Match<CambiarEstadoAcreditacionEnCentroCommand>(c =>
            c.AcreditacionId == acreditacion.Id && c.Estado == EstadoAcreditacion.Rechazada && c.Motivo == "Falta la firma"
            && c.Nota == "Solicitud 4711" && c.Fecha == DiaDeNegocio.Hoy());
        recargas.Should().HaveCount(1);
        cut.FindAll("[data-modal-estado-plataforma]").Should().BeEmpty("el modal se cierra al guardar");
    }

    [Fact]
    public void La_anotacion_guardada_sale_bajo_la_pastilla()
    {
        Registrar();
        var acreditacion = new AcreditacionResumenDto(Guid.NewGuid(), "Nalanda", EstadoAcreditacion.Rechazada,
            new AnotacionEstadoPlataformaDto(Guid.NewGuid(), new DateOnly(2026, 10, 21), "Falta la firma", "Solicitud 4711", "Marta Gil"));

        var cut = Render<AnotacionEstadoPlataforma>(p => p.Add(x => x.Acreditacion, acreditacion));

        var texto = cut.Find("[data-anotacion-plataforma]").TextContent;
        texto.Should().StartWith("Falta la firma").And.Contain("21/10/2026").And.Contain("Marta Gil").And.Contain("Solicitud 4711");
    }

    [Fact]
    public async Task En_Centro_360_si_falta_en_TALVEG_la_fila_de_plataforma_lleva_a_la_ficha_y_no_abre_el_panel()
    {
        var trabajador = Trabajador(EstadoDocumento.Vencido, Guid.NewGuid(), Acreditacion(EstadoAcreditacion.Aceptada));
        Registrar(trabajador);
        var cut = await DesplegadoConEstadoDePlataformaAsync();

        cut.Find(".fila-plataforma-centro [data-accion=actualizar-en-ficha]").GetAttribute("href")
            .Should().Be($"/trabajadores/{trabajador.TrabajadorId}");
        cut.FindAll("[data-accion=gestionar-acreditacion]").Should().BeEmpty();
    }

    private (IRenderedComponent<PanelGestionarAcreditacion>, MediatorFalso, AcreditacionResumenDto) Panel(
        EstadoAcreditacion estado, EstadoDocumento enTalveg = EstadoDocumento.Vigente)
    {
        var mediator = Registrar();
        var acreditacion = Acreditacion(estado);
        var cut = Render<PanelGestionarAcreditacion>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.DocumentoNombre, "RNT")
            .Add(x => x.PropietarioNombre, "Montajes S.L.")
            .Add(x => x.PropietarioId, Guid.NewGuid())
            .Add(x => x.CentroNombre, "Almacén Sur")
            .Add(x => x.EsDeEmpresa, true)
            .Add(x => x.EstadoTalveg, enTalveg)
            .Add(x => x.Acreditaciones, [acreditacion]));
        return (cut, mediator, acreditacion);
    }

    private static AngleSharp.Dom.IElement Opcion(IRenderedComponent<PanelGestionarAcreditacion> cut, string destino) =>
        cut.Find($"[role=radio][data-destino={destino}]");

    private static AngleSharp.Dom.IElement BotonGuardar(IRenderedComponent<PanelGestionarAcreditacion> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains("Guardar estado"));

    private static async Task ElegirYGuardarAsync(IRenderedComponent<PanelGestionarAcreditacion> cut, string destino)
    {
        await Opcion(cut, destino).ClickAsync(new MouseEventArgs());
        await BotonGuardar(cut).ClickAsync(new MouseEventArgs());
    }

    [Fact]
    public void El_panel_calca_la_maqueta_ficha_estado_actual_y_pie_con_enlace_a_la_ficha()
    {
        var (cut, _, _) = Panel(EstadoAcreditacion.Subida);

        cut.Find(".ga-ficha").TextContent.Should().Contain("RNT").And.Contain("Montajes S.L.").And.Contain("Nalanda").And.Contain("Almacén Sur");
        cut.Find("legend").TextContent.Should().Be("Nuevo estado en Nalanda");
        cut.FindAll("[role=radio].ga-op").Select(o => o.GetAttribute("data-destino")).Should().Equal(
            PanelGestionarAcreditacion.DestinoAlDia, PanelGestionarAcreditacion.DestinoPendienteValidacion,
            PanelGestionarAcreditacion.DestinoRechazada, PanelGestionarAcreditacion.DestinoPendienteSubir);
        cut.Find("[data-accion=ir-a-ficha]").TextContent.Should().Be("Empresa 360");
    }

    [Fact]
    public void Abre_con_el_estado_actual_marcado_y_no_deja_guardar_sin_cambiarlo()
    {
        var (cut, _, _) = Panel(EstadoAcreditacion.Subida);

        Opcion(cut, PanelGestionarAcreditacion.DestinoPendienteValidacion).GetAttribute("aria-checked").Should().Be("true");
        BotonGuardar(cut).HasAttribute("disabled").Should().BeTrue();
    }

    [Theory]
    [InlineData(EstadoDocumento.Vencido)]
    [InlineData(EstadoDocumento.Faltante)]
    public void Sin_version_vigente_en_TALVEG_avisa_y_bloquea_subido_y_pendiente_de_validacion(EstadoDocumento enTalveg)
    {
        var (cut, _, _) = Panel(EstadoAcreditacion.PendienteDeSubir, enTalveg);

        cut.Find("[data-aviso=talveg-no-vigente]").TextContent.Should().Contain("Empresa 360");
        Opcion(cut, PanelGestionarAcreditacion.DestinoAlDia).HasAttribute("disabled").Should().BeTrue();
        Opcion(cut, PanelGestionarAcreditacion.DestinoPendienteValidacion).HasAttribute("disabled").Should().BeTrue();
        Opcion(cut, PanelGestionarAcreditacion.DestinoRechazada).HasAttribute("disabled").Should().BeFalse();
    }

    [Theory]
    [InlineData(PanelGestionarAcreditacion.DestinoPendienteSubir, typeof(MarcarAcreditacionPendienteDeSubirCommand))]
    [InlineData(PanelGestionarAcreditacion.DestinoPendienteValidacion, typeof(MarcarAcreditacionSubidaCommand))]
    public async Task Cada_estado_del_panel_envia_su_comando_de_acreditacion(string destino, Type comando)
    {
        var (cut, mediator, _) = Panel(EstadoAcreditacion.Aceptada);

        await ElegirYGuardarAsync(cut, destino);

        mediator.Enviados.Should().ContainSingle().Which.Should().BeOfType(comando);
    }

    [Fact]
    public async Task Subido_y_al_dia_envia_validada_con_la_vigencia_sin_confirmar()
    {
        var (cut, mediator, acreditacion) = Panel(EstadoAcreditacion.Subida);

        await ElegirYGuardarAsync(cut, PanelGestionarAcreditacion.DestinoAlDia);

        var enviado = mediator.Enviados.Should().ContainSingle().Which.Should().BeOfType<MarcarAcreditacionAceptadaCommand>().Subject;
        enviado.AcreditacionId.Should().Be(acreditacion.Id);
        enviado.Vigencia.Should().Be(VigenciaEnPlataforma.SinConfirmar);
    }

    [Fact]
    public async Task Rechazado_sin_motivo_no_deja_guardar()
    {
        var (cut, mediator, _) = Panel(EstadoAcreditacion.Subida);

        await Opcion(cut, PanelGestionarAcreditacion.DestinoRechazada).ClickAsync(new MouseEventArgs());

        BotonGuardar(cut).HasAttribute("disabled").Should().BeTrue();
        mediator.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Rechazado_con_motivo_envia_el_rechazo_con_su_motivo()
    {
        var (cut, mediator, _) = Panel(EstadoAcreditacion.Subida);
        await Opcion(cut, PanelGestionarAcreditacion.DestinoRechazada).ClickAsync(new MouseEventArgs());
        cut.Find("textarea").Input("Firma ilegible");

        await BotonGuardar(cut).ClickAsync(new MouseEventArgs());

        mediator.Enviados.Should().ContainSingle().Which.Should().BeOfType<MarcarAcreditacionRechazadaCommand>()
            .Which.MotivoLiteral.Should().Be("Firma ilegible");
    }
}