using Bunit;
using CaeManager.Application.Comunicaciones.Commands.AsignarAdjuntosLote;
using CaeManager.Application.Comunicaciones.Commands.FijarDestinoAdjunto;
using CaeManager.Application.Comunicaciones.Commands.ResolverAdjunto;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones.Components;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// El panel de revisión del visor de documento a pantalla completa (tablero «Visor-Documento» del lienzo de Comunicaciones, estados
/// L-66 y L-67). Fija que cada acción envía el Command que ya existe con los datos que se ven en pantalla, el estado tras el éxito
/// y tras el error, que los textos y su orden son los del lienzo (un solo primario) y que no ofrece lo que el rol no puede hacer.
/// Que el servidor deniegue lo que sale de la cartera lo prueban los tests de los Commands, no este mediador simulado.
/// </summary>
public class PanelRevisionAdjuntoTests : BunitContext
{
    private static readonly Guid Conversacion = Guid.NewGuid();
    private static readonly Guid Marta = Guid.NewGuid();
    private static readonly Guid Pedro = Guid.NewGuid();
    private static readonly Guid ObrasNorte = Guid.NewGuid();
    private static readonly Guid TipoDni = Guid.NewGuid();
    private static readonly Guid TipoSeguro = Guid.NewGuid();
    private static readonly DateOnly Emision = new(2026, 1, 10);
    private static readonly DateOnly Vencimiento = new(2027, 1, 10);

    private sealed class Mediador : IMediator
    {
        public List<object> Enviados { get; } = [];
        public Func<FijarDestinoAdjuntoCommand, Result<Guid>>? Fijar { get; set; }
        public Func<AsignarAdjuntosLoteCommand, Result<IReadOnlyList<ResultadoAsignacionAdjunto>>>? Lote { get; set; }
        public Func<ResolverAdjuntoCommand, Result<Guid>>? Resolver { get; set; }
        public Exception? Lanza { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            if (Lanza is not null) throw Lanza;
            object respuesta = request switch
            {
                FijarDestinoAdjuntoCommand c => Fijar?.Invoke(c) ?? Result.Exito(c.AdjuntoId),
                AsignarAdjuntosLoteCommand c => Lote?.Invoke(c) ?? Asignados(c),
                ResolverAdjuntoCommand c => Resolver?.Invoke(c) ?? Result.Exito(c.AdjuntoId),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult((TResponse)respuesta);
        }

        private static Result<IReadOnlyList<ResultadoAsignacionAdjunto>> Asignados(AsignarAdjuntosLoteCommand c)
        {
            IReadOnlyList<ResultadoAsignacionAdjunto> resultados =
                c.Adjuntos.Select(a => new ResultadoAsignacionAdjunto(a.AdjuntoId, EstadoAsignacionAdjunto.Asignado, Guid.NewGuid(), null)).ToList();
            return Result.Exito(resultados);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private Mediador Preparar(string rol = Roles.GestorCae)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura(rol);
        Services.AddLocalization();
        var mediador = new Mediador();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton<ILogger<PanelRevisionAdjunto>>(_ => NullLogger<PanelRevisionAdjunto>.Instance);
        return mediador;
    }

    private static AdjuntoVisor Adjunto(EstadoAdjuntoVisor estado = EstadoAdjuntoVisor.Pendiente) =>
        new(Guid.NewGuid(), "dni-marta.pdf", "/comunicaciones/adjuntos/x/archivo", 1, "312 KB", estado);

    private static RevisionAdjuntos Revision(AdjuntoVisor adjunto, PropuestaRevision? propuesta = null) => new(
        Conversacion,
        new Dictionary<Guid, PropuestaRevision> { [adjunto.Id] = propuesta ?? PropuestaGuardada() },
        [new(Marta, false, "Marta Ruiz", "Trabajadora de Obras Norte"), new(Pedro, false, "Pedro Gil"), new(ObrasNorte, true, "Obras Norte")],
        [new(TipoDni, "DNI", false), new(TipoSeguro, "Seguro de responsabilidad civil", true)]);

    // La propuesta que el servidor ya tiene guardada: Marta y el DNI, con la fecha de emisión que una persona revisó.
    private static PropuestaRevision PropuestaGuardada() => new(Marta, null, TipoDni, Emision, Vencimiento);

    private sealed record Resultado(List<ResolucionAdjuntoVisor> Resueltos, List<int> Dejados);

    private (IRenderedComponent<PanelRevisionAdjunto> Cut, Resultado Avisos) Pintar(
        AdjuntoVisor adjunto, RevisionAdjuntos revision, int pendientes = 3, int total = 3, List<string>? teclas = null)
    {
        var avisos = new Resultado([], []);
        var cut = Render<PanelRevisionAdjunto>(p => p
            .Add(x => x.Contexto, new ContextoPanelVisor(adjunto, pendientes, total,
                EventCallback.Factory.Create<ResolucionAdjuntoVisor>(this, r => avisos.Resueltos.Add(r)),
                EventCallback.Factory.Create(this, () => avisos.Dejados.Add(1)),
                EventCallback.Factory.Create<KeyboardEventArgs>(this, e => teclas?.Add(e.Key))))
            .Add(x => x.Revision, revision));
        return (cut, avisos);
    }

    private static AngleSharp.Dom.IElement BotonDe(IRenderedComponent<PanelRevisionAdjunto> cut, string texto) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == texto);

    private static Task Pulsar(IRenderedComponent<PanelRevisionAdjunto> cut, string texto) =>
        BotonDe(cut, texto).ClickAsync(new MouseEventArgs());

    private static string Aviso(IRenderedComponent<PanelRevisionAdjunto> cut) => cut.Find(".vz-aviso").TextContent.Trim();

    private IEnumerable<string> Toasts() => Services.GetRequiredService<ToastService>().Mensajes.Select(t => t.Mensaje);

    private static async Task EscribirAsync(AngleSharp.Dom.IElement campo, string texto) =>
        await campo.InputAsync(new ChangeEventArgs { Value = texto });

    private static Task ElegirMartaAsync(IRenderedComponent<PanelRevisionAdjunto> cut) =>
        cut.Find($"button[data-cand='{Marta}']").ClickAsync(new MouseEventArgs());

    // ---------- Textos, orden y jerarquía del lienzo ----------

    [Fact]
    public void Pinta_las_secciones_y_las_tres_acciones_con_los_rotulos_y_el_orden_del_lienzo_y_un_solo_primario()
    {
        Preparar();
        var adjunto = Adjunto();

        var (cut, _) = Pintar(adjunto, Revision(adjunto));

        cut.Find("aside").GetAttribute("aria-label").Should().Be("Revisión del documento");
        cut.FindAll(".vz-etq").Select(e => e.TextContent.Trim()).Should().Equal("¿Es el documento correcto?", "Asignar a");
        cut.Markup.Should().Contain("Falta elegir a quién corresponde").And.Contain("Sin lectura de Vega").And.Contain("Único posible");
        cut.Find("label[for]").TextContent.Should().Be("Tipo de documento");
        cut.FindAll(".vz-acciones button").Select(b => b.TextContent.Trim())
            .Should().Equal("Asignar el documento", "Rechazar documento", "Dejar el documento para después");
        cut.FindAll(".boton-primario").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Asignar el documento");
        cut.FindAll(".boton-destructivo").Should().BeEmpty();
    }

    [Fact]
    public void Con_la_propuesta_guardada_pinta_su_tarjeta_sin_dejarla_elegida()
    {
        Preparar();
        var adjunto = Adjunto();

        var (cut, _) = Pintar(adjunto, Revision(adjunto));

        var tarjeta = cut.Find($"button[data-cand='{Marta}']");
        tarjeta.TextContent.Should().Contain("Marta Ruiz").And.Contain("Trabajadora de Obras Norte");
        tarjeta.GetAttribute("role").Should().Be("radio");
        // Sin lectura de Vega nadie viene preseleccionado: lo elige la persona.
        tarjeta.GetAttribute("aria-checked").Should().Be("false");
        cut.FindAll("button[role=radio]").Should().ContainSingle();
    }

    [Fact]
    public void Sin_propuesta_el_panel_pide_buscar_y_no_ofrece_tarjetas()
    {
        Preparar();
        var adjunto = Adjunto();

        var (cut, _) = Pintar(adjunto, Revision(adjunto, new PropuestaRevision(null, null, null)));

        cut.FindAll("button[role=radio]").Should().BeEmpty();
        cut.Markup.Should().Contain("Sin propuesta: busca a la persona o Empresa");
        cut.Markup.Should().Contain("Buscar a otra persona o Empresa");
    }

    // ---------- Asignar ----------

    [Fact]
    public async Task Asignar_sin_elegir_a_quien_corresponde_no_envia_nada_y_dice_por_que()
    {
        var mediador = Preparar();
        var adjunto = Adjunto();
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto));

        var asignar = BotonDe(cut, "Asignar el documento");
        asignar.GetAttribute("aria-disabled").Should().Be("true");
        cut.Markup.Should().Contain("Elige a quién corresponde el documento para asignarlo.");

        await asignar.ClickAsync(new MouseEventArgs());

        mediador.Enviados.Should().BeEmpty();
        avisos.Resueltos.Should().BeEmpty();
    }

    [Fact]
    public async Task Asignar_con_la_propuesta_guardada_llama_solo_al_lote_con_los_datos_de_la_pantalla_y_avisa_al_visor()
    {
        var mediador = Preparar();
        var adjunto = Adjunto();
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto), pendientes: 3, total: 3);
        await ElegirMartaAsync(cut);
        cut.Find($"button[data-cand='{Marta}']").GetAttribute("aria-checked").Should().Be("true");
        BotonDe(cut, "Asignar el documento").HasAttribute("aria-disabled").Should().BeFalse();

        await Pulsar(cut, "Asignar el documento");

        var lote = mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<AsignarAdjuntosLoteCommand>().Subject;
        lote.ConversacionId.Should().Be(Conversacion);
        lote.Adjuntos.Should().ContainSingle().Which.Should().Be(new AdjuntoAAsignar(adjunto.Id, TipoDni, Marta, null, Emision, Vencimiento, false));
        avisos.Resueltos.Should().Equal(new ResolucionAdjuntoVisor(adjunto.Id, EstadoAdjuntoVisor.Asignado));
        Toasts().Should().ContainSingle().Which.Should().Be("Asignado a Marta Ruiz como «DNI» · quedan 2 por revisar");
    }

    [Fact]
    public async Task Asignar_el_ultimo_pendiente_dice_que_ya_estan_todos_revisados()
    {
        Preparar();
        var adjunto = Adjunto();
        var (cut, _) = Pintar(adjunto, Revision(adjunto), pendientes: 1, total: 3);
        await ElegirMartaAsync(cut);

        await Pulsar(cut, "Asignar el documento");

        Toasts().Should().ContainSingle().Which.Should().Be("Asignado a Marta Ruiz como «DNI» · has revisado los 3 adjuntos");
    }

    [Fact]
    public async Task Si_la_persona_elige_a_otro_destino_se_guarda_antes_de_asignar_y_el_lote_lleva_ese_destino()
    {
        var mediador = Preparar();
        var adjunto = Adjunto();
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto));

        await Pulsar(cut, "Buscar a otra persona o Empresa");
        await EscribirAsync(cut.Find("input[type=search]"), "Ped");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("1 resultado"));
        await Pulsar(cut, "Elegir a Pedro Gil");
        cut.Find($"button[data-cand='{Pedro}']").GetAttribute("aria-checked").Should().Be("true");
        await Pulsar(cut, "Asignar el documento");

        mediador.Enviados.Should().HaveCount(2);
        mediador.Enviados[0].Should().Be(new FijarDestinoAdjuntoCommand(adjunto.Id, Pedro, null, TipoDni));
        var lote = mediador.Enviados[1].Should().BeOfType<AsignarAdjuntosLoteCommand>().Subject;
        lote.Adjuntos.Should().ContainSingle().Which.Should().Be(new AdjuntoAAsignar(adjunto.Id, TipoDni, Pedro, null, Emision, Vencimiento, false));
        avisos.Resueltos.Should().ContainSingle().Which.Estado.Should().Be(EstadoAdjuntoVisor.Asignado);
    }

    [Fact]
    public async Task Un_tipo_distinto_del_propuesto_tambien_se_guarda_antes_de_asignar_y_a_una_Empresa_solo_se_le_ofrecen_tipos_de_Empresa()
    {
        var mediador = Preparar();
        var adjunto = Adjunto();
        var (cut, _) = Pintar(adjunto, Revision(adjunto, new PropuestaRevision(null, ObrasNorte, TipoSeguro, Emision)));

        await cut.Find($"button[data-cand='{ObrasNorte}']").ClickAsync(new MouseEventArgs());
        cut.FindAll("select option").Select(o => o.TextContent.Trim()).Should().Equal("Elige un tipo", "Seguro de responsabilidad civil");
        await Pulsar(cut, "Asignar el documento");

        mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<AsignarAdjuntosLoteCommand>()
            .Which.Adjuntos.Single().Should().Be(new AdjuntoAAsignar(adjunto.Id, TipoSeguro, null, ObrasNorte, Emision, null, false));
    }

    [Fact]
    public async Task Sin_tipo_no_se_asigna_hasta_elegirlo_y_entonces_se_guarda_el_tipo_antes()
    {
        var mediador = Preparar();
        var adjunto = Adjunto();
        var (cut, _) = Pintar(adjunto, Revision(adjunto, new PropuestaRevision(Marta, null, null, Emision)));
        await ElegirMartaAsync(cut);

        BotonDe(cut, "Asignar el documento").GetAttribute("aria-disabled").Should().Be("true");
        cut.Markup.Should().Contain("Elige el tipo de documento para asignarlo.");
        await Pulsar(cut, "Asignar el documento");
        mediador.Enviados.Should().BeEmpty();

        cut.Find("select").Change(TipoDni.ToString());
        await Pulsar(cut, "Asignar el documento");

        mediador.Enviados.Should().HaveCount(2);
        mediador.Enviados[0].Should().Be(new FijarDestinoAdjuntoCommand(adjunto.Id, Marta, null, TipoDni));
        mediador.Enviados[1].Should().BeOfType<AsignarAdjuntosLoteCommand>();
    }

    [Fact]
    public async Task Sin_fecha_de_emision_no_se_asigna_ni_se_inventa_una()
    {
        var mediador = Preparar();
        var adjunto = Adjunto();
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto, new PropuestaRevision(Marta, null, TipoDni)));
        await ElegirMartaAsync(cut);

        BotonDe(cut, "Asignar el documento").GetAttribute("aria-disabled").Should().Be("true");
        cut.Markup.Should().Contain("Falta la fecha de emisión del documento para asignarlo.");
        await Pulsar(cut, "Asignar el documento");

        mediador.Enviados.Should().BeEmpty();
        avisos.Resueltos.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_no_se_puede_guardar_el_destino_se_avisa_sin_llamar_al_lote_ni_cambiar_el_adjunto()
    {
        var mediador = Preparar();
        mediador.Fijar = _ => Result.Fallo<Guid>(Error.Crear("fuera_de_alcance", "Ese Trabajador no está en tu cartera."));
        var adjunto = Adjunto();
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto));
        await Pulsar(cut, "Buscar a otra persona o Empresa");
        await EscribirAsync(cut.Find("input[type=search]"), "Ped");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Elegir a Pedro Gil"));
        await Pulsar(cut, "Elegir a Pedro Gil");

        await Pulsar(cut, "Asignar el documento");

        mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<FijarDestinoAdjuntoCommand>();
        Aviso(cut).Should().Be("Ese Trabajador no está en tu cartera.");
        avisos.Resueltos.Should().BeEmpty();
        Toasts().Should().BeEmpty();
        // Sigue pendiente, con lo que la persona había elegido.
        BotonDe(cut, "Asignar el documento").Should().NotBeNull();
        cut.Find($"button[data-cand='{Pedro}']").GetAttribute("aria-checked").Should().Be("true");
    }

    [Fact]
    public async Task Si_el_lote_falla_no_se_da_por_asignado_y_se_dice_por_que()
    {
        var mediador = Preparar();
        mediador.Lote = _ => Result.Fallo<IReadOnlyList<ResultadoAsignacionAdjunto>>(Error.Crear("sin_permiso", "No tienes permiso para asignar este adjunto."));
        var adjunto = Adjunto();
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto));
        await ElegirMartaAsync(cut);

        await Pulsar(cut, "Asignar el documento");

        Aviso(cut).Should().Be("No tienes permiso para asignar este adjunto.");
        avisos.Resueltos.Should().BeEmpty();
        Toasts().Should().BeEmpty();
    }

    [Theory]
    [InlineData(EstadoAsignacionAdjunto.NoAsignado)]
    [InlineData(EstadoAsignacionAdjunto.Conflicto)]
    [InlineData(EstadoAsignacionAdjunto.Error)]
    [InlineData(EstadoAsignacionAdjunto.Indeterminado)]
    public async Task Si_el_adjunto_no_sale_asignado_del_lote_se_muestra_su_detalle_y_nada_cambia(EstadoAsignacionAdjunto estado)
    {
        var mediador = Preparar();
        mediador.Lote = c => Result.Exito<IReadOnlyList<ResultadoAsignacionAdjunto>>(
            [new ResultadoAsignacionAdjunto(c.Adjuntos[0].AdjuntoId, estado, null, "Otra sesión cambió el adjunto.")]);
        var adjunto = Adjunto();
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto));
        await ElegirMartaAsync(cut);

        await Pulsar(cut, "Asignar el documento");

        Aviso(cut).Should().Be("Otra sesión cambió el adjunto.");
        avisos.Resueltos.Should().BeEmpty();
        Toasts().Should().BeEmpty();
    }

    [Fact]
    public async Task Si_el_lote_no_responde_por_el_adjunto_se_da_un_aviso_generico_y_nada_cambia()
    {
        var mediador = Preparar();
        mediador.Lote = _ => Result.Exito<IReadOnlyList<ResultadoAsignacionAdjunto>>([]);
        var adjunto = Adjunto();
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto));
        await ElegirMartaAsync(cut);

        await Pulsar(cut, "Asignar el documento");

        Aviso(cut).Should().Be("No se pudo completar. Comprueba el estado del adjunto.");
        avisos.Resueltos.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_excepcion_del_Command_no_tumba_el_panel_y_se_avisa_sin_revelar_su_texto()
    {
        var mediador = Preparar();
        mediador.Lanza = new InvalidOperationException("detalle interno con datos");
        var adjunto = Adjunto();
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto));
        await ElegirMartaAsync(cut);

        await Pulsar(cut, "Asignar el documento");

        Aviso(cut).Should().Be("No se pudo completar. Comprueba el estado del adjunto.");
        cut.Markup.Should().NotContain("detalle interno");
        avisos.Resueltos.Should().BeEmpty();
        BotonDe(cut, "Asignar el documento").Should().NotBeNull();
    }

    // ---------- Rechazar ----------

    [Fact]
    public async Task Rechazar_pide_el_motivo_y_no_envia_nada_hasta_tenerlo()
    {
        var mediador = Preparar();
        var adjunto = Adjunto();
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto));

        await Pulsar(cut, "Rechazar documento");

        cut.Find("textarea").Should().NotBeNull();
        cut.Markup.Should().Contain("Motivo del rechazo").And.Contain("Escribe el motivo para rechazar el documento.");
        cut.FindAll(".vz-acciones button").Select(b => b.TextContent.Trim()).Should().Equal("Rechazar documento", "Cancelar el rechazo");
        cut.FindAll(".boton-primario").Should().BeEmpty();
        var rechazar = cut.Find(".boton-destructivo");
        rechazar.GetAttribute("aria-disabled").Should().Be("true");

        await rechazar.ClickAsync(new MouseEventArgs());

        mediador.Enviados.Should().BeEmpty();
        avisos.Resueltos.Should().BeEmpty();
    }

    [Fact]
    public async Task Rechazar_con_motivo_envia_el_Command_con_el_motivo_recortado_y_avisa_al_visor()
    {
        var mediador = Preparar();
        var adjunto = Adjunto();
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto), pendientes: 3, total: 3);
        await Pulsar(cut, "Rechazar documento");
        await EscribirAsync(cut.Find("textarea"), "  Falta la firma  ");
        cut.WaitForAssertion(() => cut.Find(".boton-destructivo").HasAttribute("aria-disabled").Should().BeFalse());

        await cut.Find(".boton-destructivo").ClickAsync(new MouseEventArgs());

        mediador.Enviados.Should().ContainSingle().Which.Should().Be(new ResolverAdjuntoCommand(adjunto.Id, ResolucionAdjunto.Rechazar, "Falta la firma"));
        avisos.Resueltos.Should().Equal(new ResolucionAdjuntoVisor(adjunto.Id, EstadoAdjuntoVisor.Rechazado));
        // El Command no envía nada al remitente: el aviso dice solo lo que pasó.
        Toasts().Should().ContainSingle().Which.Should().Be("Rechazado · el documento queda sin asignar · quedan 2 por revisar");
    }

    [Fact]
    public async Task Si_rechazar_falla_se_avisa_y_se_conserva_el_motivo_escrito()
    {
        var mediador = Preparar();
        mediador.Resolver = _ => Result.Fallo<Guid>(Error.Crear("estado", "El adjunto ya no está pendiente."));
        var adjunto = Adjunto();
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto));
        await Pulsar(cut, "Rechazar documento");
        await EscribirAsync(cut.Find("textarea"), "Falta la firma");
        cut.WaitForAssertion(() => cut.Find(".boton-destructivo").HasAttribute("aria-disabled").Should().BeFalse());

        await cut.Find(".boton-destructivo").ClickAsync(new MouseEventArgs());

        Aviso(cut).Should().Be("El adjunto ya no está pendiente.");
        cut.Find("textarea").TextContent.Should().Be("Falta la firma");
        avisos.Resueltos.Should().BeEmpty();
        Toasts().Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelar_el_rechazo_vuelve_a_las_tres_acciones_sin_enviar_nada_y_descarta_el_motivo()
    {
        var mediador = Preparar();
        var adjunto = Adjunto();
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto));
        await Pulsar(cut, "Rechazar documento");
        await EscribirAsync(cut.Find("textarea"), "Falta la firma");

        await Pulsar(cut, "Cancelar el rechazo");

        cut.FindAll("textarea").Should().BeEmpty();
        cut.FindAll(".vz-acciones button").Select(b => b.TextContent.Trim())
            .Should().Equal("Asignar el documento", "Rechazar documento", "Dejar el documento para después");
        await Pulsar(cut, "Rechazar documento");
        cut.Find("textarea").TextContent.Should().BeEmpty();
        mediador.Enviados.Should().BeEmpty();
        avisos.Resueltos.Should().BeEmpty();
    }

    // ---------- Revisar de nuevo ----------

    [Fact]
    public async Task Un_adjunto_rechazado_muestra_su_motivo_y_se_puede_revisar_de_nuevo_con_Reabrir()
    {
        var mediador = Preparar();
        var adjunto = Adjunto(EstadoAdjuntoVisor.Rechazado);
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto, new PropuestaRevision(Marta, null, TipoDni, MotivoRechazo: "Falta la firma")), pendientes: 0, total: 3);

        cut.Find(".vz-resuelto").TextContent.Should().Contain("Rechazado").And.Contain("Falta la firma");
        cut.Find($"button[data-cand='{Marta}']").GetAttribute("aria-disabled").Should().Be("true");
        cut.FindAll("select").Should().BeEmpty();
        cut.FindAll(".boton-primario").Should().BeEmpty();
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain(["Asignar el documento", "Dejar el documento para después"])
            .And.Contain("Revisar de nuevo el documento");

        await Pulsar(cut, "Revisar de nuevo el documento");

        mediador.Enviados.Should().ContainSingle().Which.Should().Be(new ResolverAdjuntoCommand(adjunto.Id, ResolucionAdjunto.Reabrir));
        avisos.Resueltos.Should().Equal(new ResolucionAdjuntoVisor(adjunto.Id, EstadoAdjuntoVisor.Pendiente));
        Toasts().Should().ContainSingle().Which.Should().Be("El documento vuelve a estar pendiente");
    }

    [Fact]
    public async Task Si_revisar_de_nuevo_falla_el_adjunto_sigue_rechazado_y_se_dice_por_que()
    {
        var mediador = Preparar();
        mediador.Resolver = _ => Result.Fallo<Guid>(Error.Crear("estado", "Solo se puede reabrir un adjunto rechazado o descartado."));
        var adjunto = Adjunto(EstadoAdjuntoVisor.Rechazado);
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto), pendientes: 0, total: 3);

        await Pulsar(cut, "Revisar de nuevo el documento");

        Aviso(cut).Should().Be("Solo se puede reabrir un adjunto rechazado o descartado.");
        avisos.Resueltos.Should().BeEmpty();
        cut.Find(".vz-resuelto").TextContent.Should().Contain("Rechazado");
    }

    [Fact]
    public void Un_adjunto_asignado_dice_a_quien_y_como_y_no_ofrece_acciones_ni_deshacer()
    {
        var mediador = Preparar();
        var adjunto = Adjunto(EstadoAdjuntoVisor.Asignado);

        var (cut, _) = Pintar(adjunto, Revision(adjunto), pendientes: 0, total: 3);

        cut.Find(".vz-resuelto").TextContent.Should().Contain("Asignado").And.Contain("A Marta Ruiz como «DNI».");
        cut.Markup.Should().Contain("Ya está asignado.");
        cut.FindAll(".vz-pie button").Should().BeEmpty();
        cut.FindAll("select").Should().BeEmpty();
        mediador.Enviados.Should().BeEmpty();
    }

    // ---------- Dejar para después ----------

    [Fact]
    public async Task Dejar_para_despues_no_llama_a_ningun_Command_y_pasa_al_siguiente()
    {
        var mediador = Preparar();
        var adjunto = Adjunto();
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto), pendientes: 3);

        await Pulsar(cut, "Dejar el documento para después");

        mediador.Enviados.Should().BeEmpty();
        avisos.Dejados.Should().HaveCount(1);
        avisos.Resueltos.Should().BeEmpty();
        Toasts().Should().ContainSingle().Which.Should().Be("Lo dejas para después");
    }

    [Fact]
    public async Task Dejar_para_despues_sin_otro_pendiente_lo_dice_y_no_se_mueve()
    {
        var mediador = Preparar();
        var adjunto = Adjunto();
        var (cut, avisos) = Pintar(adjunto, Revision(adjunto), pendientes: 1);

        await Pulsar(cut, "Dejar el documento para después");

        mediador.Enviados.Should().BeEmpty();
        avisos.Dejados.Should().BeEmpty();
        Toasts().Should().ContainSingle().Which.Should().Be("No hay otro adjunto pendiente");
    }

    // ---------- Rol ----------

    [Fact]
    public async Task Con_el_rol_Consulta_se_ve_la_revision_pero_no_hay_ni_se_ejecuta_ninguna_accion()
    {
        var mediador = Preparar(Roles.Consulta);
        var adjunto = Adjunto();

        var (cut, avisos) = Pintar(adjunto, Revision(adjunto));

        cut.Markup.Should().Contain("Con el rol Consulta puedes ver la revisión, pero no cambiarla.");
        cut.FindAll(".vz-pie button").Should().BeEmpty();
        cut.FindAll("select").Should().BeEmpty();
        cut.Find($"button[data-cand='{Marta}']").GetAttribute("aria-disabled").Should().Be("true");
        await cut.Find($"button[data-cand='{Marta}']").ClickAsync(new MouseEventArgs());
        cut.Find($"button[data-cand='{Marta}']").GetAttribute("aria-checked").Should().Be("false");
        mediador.Enviados.Should().BeEmpty();
        avisos.Resueltos.Should().BeEmpty();
    }

    // ---------- Teclado ----------

    [Fact]
    public async Task Las_teclas_de_un_campo_son_del_campo_y_no_llegan_al_visor()
    {
        Preparar();
        var adjunto = Adjunto();
        var teclas = new List<string>();
        var (cut, _) = Pintar(adjunto, Revision(adjunto), teclas: teclas);

        await cut.Find("select").KeyDownAsync(new KeyboardEventArgs { Key = "+" });
        await cut.Find("select").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight" });
        await Pulsar(cut, "Buscar a otra persona o Empresa");
        await cut.Find("input[type=search]").KeyDownAsync(new KeyboardEventArgs { Key = "]" });
        await Pulsar(cut, "Rechazar documento");
        await cut.Find("textarea").KeyDownAsync(new KeyboardEventArgs { Key = "-" });

        teclas.Should().BeEmpty();
    }

    [Fact]
    public async Task Las_flechas_de_la_lista_de_candidatos_eligen_y_no_llegan_al_visor_y_las_demas_teclas_del_panel_si()
    {
        Preparar();
        var adjunto = Adjunto();
        var teclas = new List<string>();
        var (cut, _) = Pintar(adjunto, Revision(adjunto), teclas: teclas);

        await cut.Find("[role=radiogroup]").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        cut.Find($"button[data-cand='{Marta}']").GetAttribute("aria-checked").Should().Be("true");
        teclas.Should().BeEmpty();

        await cut.Find("aside").KeyDownAsync(new KeyboardEventArgs { Key = "=" });
        await cut.Find("aside").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        teclas.Should().Equal("=", "Escape");
    }

    // ---------- Dentro del visor a pantalla completa ----------

    private IRenderedComponent<VisorDocumentoPantallaCompleta> PintarVisor(
        IReadOnlyList<AdjuntoVisor> lista, RevisionAdjuntos? revision, List<ResolucionAdjuntoVisor>? resueltos = null, Action<bool>? alCambiar = null) =>
        Render<VisorDocumentoPantallaCompleta>(p =>
        {
            p.Add(x => x.Visible, true)
             .Add(x => x.Adjuntos, lista)
             .Add(x => x.AdjuntoResuelto, EventCallback.Factory.Create<ResolucionAdjuntoVisor>(this, r => resueltos?.Add(r)))
             .Add(x => x.VisibleChanged, EventCallback.Factory.Create<bool>(this, v => alCambiar?.Invoke(v)));

            // Como lo conectará quien abra el visor (A4): el panel real de Comunicaciones en el hueco Panel.
            if (revision is not null)
                p.Add(x => x.Panel, contexto => constructor =>
                {
                    constructor.OpenComponent<PanelRevisionAdjunto>(0);
                    constructor.AddComponentParameter(1, nameof(PanelRevisionAdjunto.Contexto), contexto);
                    constructor.AddComponentParameter(2, nameof(PanelRevisionAdjunto.Revision), revision);
                    constructor.CloseComponent();
                });
        });

    private static RevisionAdjuntos RevisionDe(params AdjuntoVisor[] adjuntos) => new(
        Conversacion,
        adjuntos.ToDictionary(a => a.Id, _ => PropuestaGuardada()),
        [new(Marta, false, "Marta Ruiz", "Trabajadora de Obras Norte"), new(Pedro, false, "Pedro Gil")],
        [new(TipoDni, "DNI", false)]);

    private static string Posicion(IRenderedComponent<VisorDocumentoPantallaCompleta> cut) => cut.Find(".vz-cola-n").TextContent.Trim();

    private static Task TeclaAsync(AngleSharp.Dom.IElement elemento, string tecla) => elemento.KeyDownAsync(new KeyboardEventArgs { Key = tecla });

    [Fact]
    public void Sin_datos_de_revision_el_visor_no_ofrece_el_panel_ni_su_boton()
    {
        Preparar();

        var cut = PintarVisor([Adjunto(), Adjunto()], revision: null);

        cut.FindAll("aside").Should().BeEmpty();
        cut.Markup.Should().NotContain("la revisión");
    }

    [Fact]
    public async Task Con_datos_de_revision_el_panel_va_abierto_y_el_boton_lo_oculta_y_lo_vuelve_a_mostrar()
    {
        Preparar();
        var a = Adjunto();
        var cut = PintarVisor([a], RevisionDe(a));

        cut.Find("#vz-revision aside").Should().NotBeNull();
        var boton = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Ocultar la revisión");
        boton.GetAttribute("aria-expanded").Should().Be("true");
        boton.GetAttribute("aria-controls").Should().Be("vz-revision");

        await boton.ClickAsync(new MouseEventArgs());

        // Oculto no es desmontado: el panel sigue montado (y con él los borradores) dentro del contenedor del visor, que queda hidden.
        cut.Find("#vz-revision").HasAttribute("hidden").Should().BeTrue();
        var ver = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Ver la revisión");
        ver.GetAttribute("aria-expanded").Should().Be("false");

        await ver.ClickAsync(new MouseEventArgs());

        cut.Find("#vz-revision").HasAttribute("hidden").Should().BeFalse();
        cut.Find("#vz-revision aside").Should().NotBeNull();
    }

    [Fact]
    public async Task Asignar_en_el_visor_marca_el_adjunto_pasa_al_siguiente_pendiente_y_avisa_a_quien_lo_abrio()
    {
        var mediador = Preparar();
        var (a, b, c) = (Adjunto(), Adjunto(), Adjunto());
        var resueltos = new List<ResolucionAdjuntoVisor>();
        var cut = PintarVisor([a, b, c], RevisionDe(a, b, c), resueltos);

        await cut.Find($"button[data-cand='{Marta}']").ClickAsync(new MouseEventArgs());
        await cut.FindAll("button").Single(x => x.TextContent.Trim() == "Asignar el documento").ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<AsignarAdjuntosLoteCommand>().Should().ContainSingle().Which.Adjuntos.Single().AdjuntoId.Should().Be(a.Id);
        resueltos.Should().Equal(new ResolucionAdjuntoVisor(a.Id, EstadoAdjuntoVisor.Asignado));
        Posicion(cut).Should().Be("Documento 2 de 3");
        cut.FindAll(".vz-marca")[0].ClassList.Should().Contain("vz-marca-asignado");
        cut.Find(".vz-cola-resto").TextContent.Should().Be("Quedan 2 por revisar");
        Toasts().Should().ContainSingle().Which.Should().Be("Asignado a Marta Ruiz como «DNI» · quedan 2 por revisar");
        // El panel pasa a mostrar el adjunto siguiente, todavía pendiente y sin nada elegido.
        cut.Find($"button[data-cand='{Marta}']").GetAttribute("aria-checked").Should().Be("false");
    }

    [Fact]
    public async Task Rechazar_el_ultimo_pendiente_en_el_visor_lo_marca_se_queda_en_el_y_dice_que_estan_todos_revisados()
    {
        var mediador = Preparar();
        var (a, b) = (Adjunto(EstadoAdjuntoVisor.Asignado), Adjunto());
        var resueltos = new List<ResolucionAdjuntoVisor>();
        var cut = PintarVisor([a, b], RevisionDe(a, b), resueltos);
        await cut.FindAll("button").Single(x => x.TextContent.Trim() == "Ir al adjunto siguiente").ClickAsync(new MouseEventArgs());
        Posicion(cut).Should().Be("Documento 2 de 2");

        await cut.FindAll("button").Single(x => x.TextContent.Trim() == "Rechazar documento").ClickAsync(new MouseEventArgs());
        await EscribirAsync(cut.Find("textarea"), "Falta la firma");
        cut.WaitForAssertion(() => cut.Find(".boton-destructivo").HasAttribute("aria-disabled").Should().BeFalse());
        await cut.Find(".boton-destructivo").ClickAsync(new MouseEventArgs());

        mediador.Enviados.Should().ContainSingle().Which.Should().Be(new ResolverAdjuntoCommand(b.Id, ResolucionAdjunto.Rechazar, "Falta la firma"));
        resueltos.Should().Equal(new ResolucionAdjuntoVisor(b.Id, EstadoAdjuntoVisor.Rechazado));
        Posicion(cut).Should().Be("Documento 2 de 2");
        cut.FindAll(".vz-marca")[1].ClassList.Should().Contain("vz-marca-rechazado");
        cut.Markup.Should().Contain("Todos los adjuntos revisados");
        Toasts().Should().ContainSingle().Which.Should().Be("Rechazado · el documento queda sin asignar · has revisado los 2 adjuntos");
    }

    [Fact]
    public async Task Revisar_de_nuevo_en_el_visor_devuelve_el_adjunto_a_pendiente_y_se_queda_en_el()
    {
        Preparar();
        var (a, b) = (Adjunto(EstadoAdjuntoVisor.Rechazado), Adjunto());
        var resueltos = new List<ResolucionAdjuntoVisor>();
        var cut = PintarVisor([a, b], RevisionDe(a, b), resueltos);

        await cut.FindAll("button").Single(x => x.TextContent.Trim() == "Revisar de nuevo el documento").ClickAsync(new MouseEventArgs());

        resueltos.Should().Equal(new ResolucionAdjuntoVisor(a.Id, EstadoAdjuntoVisor.Pendiente));
        Posicion(cut).Should().Be("Documento 1 de 2");
        cut.FindAll(".vz-marca")[0].ClassList.Should().NotContain("vz-marca-rechazado");
        cut.Find(".vz-cola-resto").TextContent.Should().Be("Quedan 2 por revisar");
        cut.FindAll("button").Select(x => x.TextContent.Trim()).Should().Contain("Asignar el documento");
    }

    [Fact]
    public async Task Dejar_para_despues_en_el_visor_salta_al_siguiente_pendiente_sin_resolver_nada_ni_llamar_a_Commands()
    {
        var mediador = Preparar();
        var (a, b, c) = (Adjunto(), Adjunto(EstadoAdjuntoVisor.Asignado), Adjunto());
        var resueltos = new List<ResolucionAdjuntoVisor>();
        var cut = PintarVisor([a, b, c], RevisionDe(a, b, c), resueltos);

        await cut.FindAll("button").Single(x => x.TextContent.Trim() == "Dejar el documento para después").ClickAsync(new MouseEventArgs());

        Posicion(cut).Should().Be("Documento 3 de 3");
        mediador.Enviados.Should().BeEmpty();
        resueltos.Should().BeEmpty();
        cut.Find(".vz-cola-resto").TextContent.Should().Be("Quedan 2 por revisar");
    }

    [Fact]
    public async Task En_el_panel_las_flechas_no_cambian_de_adjunto_y_las_otras_teclas_siguen_valiendo_y_las_de_un_campo_no_llegan_al_visor()
    {
        Preparar();
        var (a, b) = (Adjunto(), Adjunto());
        var cerrados = new List<bool>();
        var cut = PintarVisor([a, b], RevisionDe(a, b), alCambiar: cerrados.Add);

        // En el panel (desplazable) las flechas desplazan: no cambian de adjunto.
        await TeclaAsync(cut.Find("aside"), "ArrowRight");
        Posicion(cut).Should().Be("Documento 1 de 2");

        // « = » amplía como « + » y Escape cierra, igual que desde el resto del visor.
        await TeclaAsync(cut.Find("aside"), "=");
        cut.Find(".vz-pct").TextContent.Trim().Should().NotBe("100 %");

        // Una tecla escrita en un campo es del campo: ni zoom, ni hoja, ni adjunto, ni cierre.
        var zoom = cut.Find(".vz-pct").TextContent.Trim();
        await TeclaAsync(cut.Find("select"), "+");
        await TeclaAsync(cut.Find("select"), "ArrowRight");
        await TeclaAsync(cut.Find("select"), "Escape");
        cut.Find(".vz-pct").TextContent.Trim().Should().Be(zoom);
        Posicion(cut).Should().Be("Documento 1 de 2");
        cerrados.Should().BeEmpty();

        await TeclaAsync(cut.Find("aside"), "Escape");
        cerrados.Should().Equal(false);
    }
}
