using Bunit;
using CaeManager.Application.Contactos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresaEmpleadora;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Bandeja.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Defectos del flujo «Pedir» / «Reclamar en lote» (piloto Outbound, 2026-10-16). El correo a un tercero no se deshace:
/// «Enviar» ya no manda nada, abre una confirmación que dice a quién y cuántos documentos, y solo ella envía. Y un envío que
/// falla no se pinta como enviado: el aviso dice que no salió y el lote sigue ahí; si el fallo es parcial, la parte entregada ya
/// está registrada y el lote sale de la lista para no reclamar dos veces a quien ya lo recibió.
/// </summary>
public class DrawerReclamacionLoteConfirmacionEnvioTests : BunitContext
{
    private static readonly Guid TrabajadorId = Guid.NewGuid();
    private static readonly Guid ClienteTitularId = Guid.NewGuid();
    private static readonly Guid DocumentoUnoId = Guid.NewGuid();
    private static readonly Guid DocumentoDosId = Guid.NewGuid();
    private static readonly Guid ContactoMartaId = Guid.NewGuid();
    private static readonly Guid ContactoPrlId = Guid.NewGuid();

    private readonly MediatorFalso _mediator = new();
    private int _reclamacionesEnviadas;

    public DrawerReclamacionLoteConfirmacionEnvioTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediator);
        Services.AddScoped<ToastService>();
    }

    private sealed class MediatorFalso : IMediator
    {
        public List<object> Recibidas { get; } = [];
        /// <summary>Si se informa, sustituye a los lotes de siempre (un único lote del Cliente titular).</summary>
        public IReadOnlyList<LoteReclamacionAgrupadoDto>? LotesForzados { get; set; }

        public Result<EnvioReclamacionResultado> ResultadoDelEnvio { get; set; } =
            Result.Exito(new EnvioReclamacionResultado([], ["marta@example.invalid"], 0));

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Recibidas.Add(request);
            object respuesta = request switch
            {
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)[],
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[new(TrabajadorId, "Ana Ruiz", null, null)],
                ObtenerLoteReclamacionPorFiltroQuery => LotesForzados ?? (IReadOnlyList<LoteReclamacionAgrupadoDto>)
                [
                    new(ClienteTitularId, "Refrielectric S.A.", AmbitoAplicacion.Trabajador, null,
                        [
                            new(DocumentoUnoId, TrabajadorId, "Ana Ruiz", Guid.NewGuid(), "Curso de altura", new DateOnly(2026, 11, 2), EstadoDocumento.Urgente),
                            new(DocumentoDosId, TrabajadorId, "Ana Ruiz", Guid.NewGuid(), "Aptitud médica", new DateOnly(2026, 11, 9), EstadoDocumento.Urgente),
                        ],
                        null,
                        [
                            new DestinatarioAgendaDto(ContactoMartaId, "Marta Gil", "marta@example.invalid", ["Formación PRL"]),
                            new DestinatarioAgendaDto(ContactoPrlId, "Pablo Ríos", "prl@example.invalid", ["Aptitud médica"]),
                        ],
                        []),
                ],
                EnviarReclamacionCommand => ResultadoDelEnvio,
                EnviarReclamacionEmpresaCommand => ResultadoDelEnvio,
                EnviarReclamacionEmpresaEmpleadoraCommand => ResultadoDelEnvio,
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private async Task<IRenderedComponent<DrawerReclamacionLote>> AbrirYContinuarAsync()
    {
        var cut = Render<DrawerReclamacionLote>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.AmbitosDisponibles, [AmbitoAplicacion.Trabajador])
            .Add(x => x.AmbitoInicial, AmbitoAplicacion.Trabajador)
            .Add(x => x.EntidadIdInicial, TrabajadorId)
            .Add(x => x.OnReclamacionEnviada, EventCallback.Factory.Create(this, () => _reclamacionesEnviadas++)));
        cut.WaitForAssertion(() => cut.FindAll(".selector-lote-documental input[type=checkbox]").Should().NotBeEmpty());
        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Continuar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".tabla-datos").Should().NotBeEmpty());
        return cut;
    }

    private static AngleSharp.Dom.IElement BotonEnviar(IRenderedComponent<DrawerReclamacionLote> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Enviar ("));

    private static AngleSharp.Dom.IElement BotonDelDialogo(IRenderedComponent<DrawerReclamacionLote> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == texto);

    private int EnviosRecibidos => _mediator.Recibidas.OfType<EnviarReclamacionCommand>().Count();

    [Fact]
    public async Task Enviar_no_envia_nada_y_pide_confirmacion_diciendo_a_quien_y_cuantos_documentos()
    {
        var cut = await AbrirYContinuarAsync();

        await BotonEnviar(cut).ClickAsync(new MouseEventArgs());

        EnviosRecibidos.Should().Be(0, "pulsar «Enviar» solo abre la confirmación");
        var dialogo = cut.Find(".modal-contenido");
        dialogo.TextContent.Should().Contain("¿Enviar la reclamación a Refrielectric S.A.?")
            .And.Contain("2 documento(s)")
            .And.Contain("2 contacto(s)")
            .And.Contain("Marta Gil (marta@example.invalid)")
            .And.Contain("Pablo Ríos (prl@example.invalid)");
    }

    [Fact]
    public async Task La_confirmacion_cuenta_solo_lo_marcado()
    {
        var cut = await AbrirYContinuarAsync();
        await cut.Find($"input[aria-label^='Incluir Aptitud médica']").ChangeAsync(new ChangeEventArgs { Value = false });
        await cut.FindAll(".reclamacion-destinatarios input[type=checkbox]")[1].ChangeAsync(new ChangeEventArgs { Value = false });

        await BotonEnviar(cut).ClickAsync(new MouseEventArgs());

        var texto = cut.Find(".modal-contenido").TextContent;
        texto.Should().Contain("1 documento(s)").And.Contain("1 contacto(s)").And.Contain("Marta Gil").And.NotContain("Pablo Ríos");
    }

    [Fact]
    public async Task Cancelar_la_confirmacion_no_envia_y_deja_el_lote_como_estaba()
    {
        var cut = await AbrirYContinuarAsync();
        await BotonEnviar(cut).ClickAsync(new MouseEventArgs());

        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        EnviosRecibidos.Should().Be(0);
        cut.FindAll(".modal-pie button").Should().BeEmpty("el diálogo se cerró");
        BotonEnviar(cut).HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Confirmar_envia_una_sola_vez_exactamente_lo_anunciado()
    {
        var cut = await AbrirYContinuarAsync();
        await BotonEnviar(cut).ClickAsync(new MouseEventArgs());

        await BotonDelDialogo(cut, "Enviar reclamación").ClickAsync(new MouseEventArgs());

        var envio = _mediator.Recibidas.OfType<EnviarReclamacionCommand>().Should().ContainSingle().Which;
        envio.ClienteId.Should().Be(ClienteTitularId);
        envio.DocumentoIds.Should().BeEquivalentTo([DocumentoUnoId, DocumentoDosId]);
        envio.ContactoIdsSeleccionados.Should().BeEquivalentTo([ContactoMartaId, ContactoPrlId]);
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Exito);
        _reclamacionesEnviadas.Should().Be(1);
    }

    /// <summary>
    /// «Gestionar» por Delegación (PR #1454): el lote que se reclama a la Empresa que emplea al Trabajador se envía con
    /// <see cref="EnviarReclamacionEmpresaEmpleadoraCommand"/> y la Delegación del lote, nunca por el comando de Cliente; y la
    /// tarjeta nombra la Delegación para distinguir dos lotes de la misma Empresa.
    /// </summary>
    [Fact]
    public async Task Un_lote_de_la_Empresa_empleadora_se_envia_por_su_comando_con_la_Delegacion_del_lote()
    {
        var empresaId = Guid.NewGuid();
        var delegacionEmpresaId = Guid.NewGuid();
        _mediator.LotesForzados =
        [
            new(empresaId, "Instalaciones Norte S.L.", AmbitoAplicacion.Trabajador, null,
                [new(DocumentoUnoId, TrabajadorId, "Ana Ruiz", Guid.NewGuid(), "Curso de altura", new DateOnly(2026, 11, 2), EstadoDocumento.Urgente)],
                null,
                [new DestinatarioAgendaDto(ContactoMartaId, "Marta Gil", "sevilla@norte.example", ["Delegación"])],
                [],
                new DestinoEmpresaEmpleadoraDto(delegacionEmpresaId, "Sevilla")),
        ];
        var cut = await AbrirYContinuarAsync();

        cut.Markup.Should().Contain("Instalaciones Norte S.L. (Sevilla)", "la tarjeta distingue la Delegación");
        _mediator.Recibidas.OfType<ObtenerLoteReclamacionPorFiltroQuery>().Should().Contain(q => q.PorEmpresaEmpleadora);

        await BotonEnviar(cut).ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Enviar reclamación").ClickAsync(new MouseEventArgs());

        var envio = _mediator.Recibidas.OfType<EnviarReclamacionEmpresaEmpleadoraCommand>().Should().ContainSingle().Which;
        envio.EmpresaId.Should().Be(empresaId);
        envio.DelegacionEmpresaId.Should().Be(delegacionEmpresaId);
        envio.DocumentoIds.Should().Equal(DocumentoUnoId);
        envio.ContactoIdsSeleccionados.Should().Equal(ContactoMartaId);
        EnviosRecibidos.Should().Be(0, "no se reclama al Cliente");
        _mediator.Recibidas.OfType<EnviarReclamacionEmpresaCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Lo_anunciado_en_la_confirmacion_es_lo_que_sale_aunque_cambien_las_casillas_con_el_dialogo_abierto()
    {
        var cut = await AbrirYContinuarAsync();
        await BotonEnviar(cut).ClickAsync(new MouseEventArgs());

        await cut.Find("input[aria-label^='Incluir Aptitud médica']").ChangeAsync(new ChangeEventArgs { Value = false });
        await BotonDelDialogo(cut, "Enviar reclamación").ClickAsync(new MouseEventArgs());

        _mediator.Recibidas.OfType<EnviarReclamacionCommand>().Should().ContainSingle()
            .Which.DocumentoIds.Should().BeEquivalentTo([DocumentoUnoId, DocumentoDosId], "el diálogo dijo 2 documentos");
    }

    [Fact]
    public async Task Cerrar_el_cajon_con_la_confirmacion_pendiente_no_deja_el_dialogo_flotando()
    {
        var cut = await AbrirYContinuarAsync();
        await BotonEnviar(cut).ClickAsync(new MouseEventArgs());
        cut.FindAll(".modal-pie button").Should().NotBeEmpty();

        cut.Render(p => p.Add(x => x.Visible, false));

        cut.FindAll(".modal-pie button").Should().BeEmpty();
        cut.Render(p => p.Add(x => x.Visible, true));
        cut.FindAll(".modal-pie button").Should().BeEmpty("al reabrir no reaparece una confirmación vieja");
        EnviosRecibidos.Should().Be(0);
    }

    [Fact]
    public async Task Si_el_envio_falla_la_pantalla_lo_dice_no_lo_da_por_enviado_y_el_lote_sigue_para_reintentar()
    {
        _mediator.ResultadoDelEnvio = Result.Fallo<EnvioReclamacionResultado>(Error.Crear(
            "Reclamacion.EnvioFallido",
            "No pudimos enviar el correo de la reclamación, así que no queda registrada. Inténtalo de nuevo en unos minutos."));
        var cut = await AbrirYContinuarAsync();
        await BotonEnviar(cut).ClickAsync(new MouseEventArgs());

        await BotonDelDialogo(cut, "Enviar reclamación").ClickAsync(new MouseEventArgs());

        var mensajes = Services.GetRequiredService<ToastService>().Mensajes;
        mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Error);
        mensajes.Should().NotContain(m => m.Tono == TonoToast.Exito, "no salió ningún correo");
        mensajes.Single().Mensaje.Should().Contain("no queda registrada");
        _reclamacionesEnviadas.Should().Be(0, "no hay nada nuevo que refrescar");
        cut.FindAll(".modal-pie button").Should().BeEmpty("el diálogo se cierra para que el aviso se vea");
        BotonEnviar(cut).HasAttribute("disabled").Should().BeFalse("el lote sigue ahí para reintentar");
    }

    [Fact]
    public async Task Si_el_envio_llega_solo_a_una_parte_se_avisa_de_la_que_no_y_el_lote_sale_porque_ya_esta_registrado()
    {
        _mediator.ResultadoDelEnvio = Result.Fallo<EnvioReclamacionResultado>(Error.Crear(
            RegistroEnvioReclamacionService.CodigoEnvioParcial,
            "La reclamación solo llegó a marta@example.invalid, y así queda registrada. No pudimos enviarla a prl@example.invalid."));
        var cut = await AbrirYContinuarAsync();
        await BotonEnviar(cut).ClickAsync(new MouseEventArgs());

        await BotonDelDialogo(cut, "Enviar reclamación").ClickAsync(new MouseEventArgs());

        var mensajes = Services.GetRequiredService<ToastService>().Mensajes;
        mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Error).Which.Mensaje.Should().Contain("prl@example.invalid");
        mensajes.Should().NotContain(m => m.Tono == TonoToast.Exito);
        _reclamacionesEnviadas.Should().Be(1, "la parte entregada ya está en el historial y la cola se refresca");
        cut.FindAll(".tabla-datos").Should().BeEmpty("reenviar el lote entero reclamaría otra vez a quien ya lo recibió");
    }

    // ---- Destinatarios añadidos a mano y copia (hueco 5 del piloto) ----

    private static async Task EscribirYAnadirAsync(IRenderedComponent<DrawerReclamacionLote> cut, string prefijoId, string correo)
    {
        await cut.Find($"input[id^='{prefijoId}']").InputAsync(new ChangeEventArgs { Value = correo });
        await cut.FindAll(".reclamacion-extra-campo")
            .Single(c => c.QuerySelector($"input[id^='{prefijoId}']") is not null)
            .QuerySelector("button")!.ClickAsync(new MouseEventArgs());
    }

    [Fact]
    public async Task Un_destinatario_manual_y_uno_en_copia_salen_en_la_confirmacion_y_en_el_comando()
    {
        var cut = await AbrirYContinuarAsync();
        await EscribirYAnadirAsync(cut, "extra-para-", "jefe.obra@subcontrata.example");
        await EscribirYAnadirAsync(cut, "extra-cc-", "coordinacion@arcosspa.example");

        await BotonEnviar(cut).ClickAsync(new MouseEventArgs());

        cut.Find(".modal-contenido").TextContent
            .Should().Contain("También a: jefe.obra@subcontrata.example")
            .And.Contain("En copia (CC): coordinacion@arcosspa.example")
            .And.Contain("Marta Gil (marta@example.invalid)");

        await BotonDelDialogo(cut, "Enviar reclamación").ClickAsync(new MouseEventArgs());

        var envio = _mediator.Recibidas.OfType<EnviarReclamacionCommand>().Should().ContainSingle().Which;
        envio.CorreosAdicionales.Should().Equal("jefe.obra@subcontrata.example");
        envio.CorreosEnCopia.Should().Equal("coordinacion@arcosspa.example");
    }

    [Fact]
    public async Task Sin_extras_la_confirmacion_y_el_comando_quedan_como_antes()
    {
        var cut = await AbrirYContinuarAsync();

        await BotonEnviar(cut).ClickAsync(new MouseEventArgs());
        cut.Find(".modal-contenido").TextContent.Should().NotContain("También a").And.NotContain("En copia");
        await BotonDelDialogo(cut, "Enviar reclamación").ClickAsync(new MouseEventArgs());

        var envio = _mediator.Recibidas.OfType<EnviarReclamacionCommand>().Should().ContainSingle().Which;
        envio.CorreosAdicionales.Should().BeEmpty();
        envio.CorreosEnCopia.Should().BeEmpty();
    }

    [Theory]
    [InlineData("no-es-un-correo", "no es un correo válido")]
    [InlineData("MARTA@example.invalid", "ya es un contacto de la agenda")]
    public async Task Un_correo_invalido_o_ya_presente_no_se_añade_y_dice_por_que(string correo, string motivo)
    {
        var cut = await AbrirYContinuarAsync();

        await EscribirYAnadirAsync(cut, "extra-para-", correo);

        cut.Find(".reclamacion-extra .campo-mensaje-error").TextContent.Should().Contain(motivo);
        cut.FindAll(".reclamacion-extra-lista li").Should().BeEmpty();
    }

    [Fact]
    public async Task El_mismo_correo_no_se_puede_poner_dos_veces_ni_en_destinatarios_y_en_copia()
    {
        var cut = await AbrirYContinuarAsync();
        await EscribirYAnadirAsync(cut, "extra-para-", "jefe.obra@subcontrata.example");

        await EscribirYAnadirAsync(cut, "extra-para-", "jefe.obra@subcontrata.example");
        await EscribirYAnadirAsync(cut, "extra-cc-", "JEFE.obra@subcontrata.example");

        cut.FindAll(".reclamacion-extra-lista li").Should().ContainSingle();
        cut.FindAll(".reclamacion-extra .campo-mensaje-error").Should().HaveCount(2);
    }

    [Fact]
    public async Task Quitar_un_manual_lo_saca_del_envio()
    {
        var cut = await AbrirYContinuarAsync();
        await EscribirYAnadirAsync(cut, "extra-para-", "jefe.obra@subcontrata.example");

        await cut.Find(".reclamacion-extra-quitar").ClickAsync(new MouseEventArgs());
        await BotonEnviar(cut).ClickAsync(new MouseEventArgs());

        cut.Find(".modal-contenido").TextContent.Should().NotContain("jefe.obra@subcontrata.example");
    }

    [Fact]
    public async Task Un_correo_tecleado_y_sin_anadir_bloquea_el_envio_en_vez_de_descartarse_en_silencio()
    {
        var cut = await AbrirYContinuarAsync();
        await cut.Find("input[id^='extra-cc-']").InputAsync(new ChangeEventArgs { Value = "coordinacion@arcosspa.example" });

        await BotonEnviar(cut).ClickAsync(new MouseEventArgs());

        cut.FindAll(".modal-pie button").Should().BeEmpty("no se abre la confirmación");
        cut.Find(".reclamacion-extra .campo-mensaje-error").TextContent.Should().Contain("Pulsa «Añadir»");
        EnviosRecibidos.Should().Be(0);
    }
}
