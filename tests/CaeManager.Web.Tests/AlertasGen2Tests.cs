using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Contactos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// /alertas Gen 2: la lista agrupada (por severidad o por motivo), la
/// confirmación antes de reclamar y el descarte de respuestas de lote que
/// llegan tarde.
///
/// <para>
/// <b>Lo que SÍ observa:</b> el marcado que pinta la pantalla y lo que llega
/// al mediador — cada petición queda registrada con sus parámetros, así que
/// un comando enviado sin confirmar, con otros ids o por el ámbito
/// equivocado, se ve aquí.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> que el envío llegue a nadie ni qué ids acepta
/// el servidor (lo revalidan los handlers, probados en Application), el
/// aspecto visual, ni la carrera de <c>ObtenerAlertasQuery</c>: su guarda de
/// versión existe, pero desde la interfaz no hay forma de lanzar dos cargas
/// a la vez (el único disparador, «Reintentar», desaparece al pulsarlo).
/// </para>
/// </summary>
public class AlertasGen2Tests : BunitContext
{
    /// <summary>TextoFechaCopiable importa clipboard.js y Modal dialogo-foco.js; el JavaScript queda fuera.</summary>
    public AlertasGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid ClienteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EmpresaId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DocumentoA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid DocumentoB = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid ContactoCarmen = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid ContactoIgnacio = Guid.Parse("cccccccc-0000-0000-0000-000000000002");

    /// <summary>
    /// Registra TODO lo que se le envía. La respuesta puede ser un valor o una
    /// <c>Task</c> ya tipada (p. ej. la de un <see cref="TaskCompletionSource{T}"/>),
    /// para resolver respuestas fuera de orden.
    /// </summary>
    private sealed class MediatorRegistrador(Func<object, object> responder) : IMediator
    {
        public List<object> Enviados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            var respuesta = responder(request);
            return respuesta is Task<TResponse> tarea ? tarea : Task.FromResult((TResponse)respuesta);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Enviados.Add(request!);
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return Task.FromResult<object?>(null);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    /// <summary>Respuestas de las consultas de arranque del selector de lote: vacías, no son lo que se observa.</summary>
    private static object? RespuestaDeSelector(object peticion) => peticion switch
    {
        ObtenerTiposDocumentoQuery => (object)Array.Empty<TipoDocumentoListaDto>(),
        ObtenerTrabajadoresParaSelectorQuery => Array.Empty<TrabajadorSelectorDto>(),
        ObtenerEmpresasParaSelectorQuery => Array.Empty<EmpresaSelectorDto>(),
        _ => null
    };

    private MediatorRegistrador Registrar(Func<object, object> responder)
    {
        var mediator = new MediatorRegistrador(responder);
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        return mediator;
    }

    private static AlertaDto Alerta(EstadoDocumento estado, string tipo = "Reconocimiento médico", Guid? tipoId = null, string trabajador = "Juan Pérez") => new(
        Guid.NewGuid(), Guid.NewGuid(), trabajador, tipoId ?? Guid.NewGuid(), tipo,
        estado == EstadoDocumento.Faltante ? null : new DateOnly(2026, 10, 1), estado,
        ArchivoUrl: null, CentroNombre: "Centro Zorrotzaurre");

    private IRenderedComponent<Features.Alertas.Pages.Alertas> RenderizarLista(params AlertaDto[] alertas)
    {
        Registrar(peticion => peticion switch
        {
            ObtenerAlertasQuery => (IReadOnlyList<AlertaDto>)alertas,
            _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
        });
        Services.GetRequiredService<NavigationManager>().NavigateTo("alertas");
        return Render<Features.Alertas.Pages.Alertas>();
    }

    private static List<string> TitulosDeGrupo(IRenderedComponent<Features.Alertas.Pages.Alertas> cut) =>
        cut.FindAll(".alertas-grupo-titulo").Select(e => e.TextContent.Trim()).ToList();

    private static List<string> InsigniasDeGrupo(IRenderedComponent<Features.Alertas.Pages.Alertas> cut) =>
        cut.FindAll(".alertas-grupo-cabecera .badge").Select(e => e.TextContent.Trim()).ToList();

    private static IElement CabeceraDeGrupo(IRenderedComponent<Features.Alertas.Pages.Alertas> cut, string titulo) =>
        cut.FindAll(".alertas-grupo-cabecera").Single(b => b.QuerySelector(".alertas-grupo-titulo")!.TextContent.Trim() == titulo);

    // ---------------------------------------------------------------- Procedencia de «Falta»

    /// <summary>
    /// «Falta» sale de ResolucionTipoDocumentoCentro.Aplica: la fila del centro
    /// si existe y, si no, el valor general del tipo (Requerido == Si). Un
    /// texto que lo atribuye solo al centro miente en los centros que no han
    /// configurado nada. Y es configuración, no norma: nada de «obligatorio».
    /// </summary>
    [Fact]
    public void La_procedencia_nombra_la_configuracion_del_centro_y_el_valor_general_del_tipo()
    {
        var cut = RenderizarLista();

        var procedencia = cut.Find(".alertas-procedencia").TextContent;
        procedencia.Should().Contain("los que ese centro tiene configurados")
            .And.Contain("si el centro no dice nada de un tipo, los tipos de documento que se piden siempre",
                "sin fila del centro, manda el valor general del tipo de documento");
        procedencia.Should().NotContain("lo que los centros tienen configurado como obligatorio",
            "esa frase atribuía «Falta» solo al centro");
        procedencia.Should().NotContainEquivalentOf("obligatori", "es configuración, no una obligación legal");
    }

    [Fact]
    public void La_descripcion_del_bloque_Falta_nombra_las_dos_procedencias()
    {
        var cut = RenderizarLista(Alerta(EstadoDocumento.Faltante));

        var meta = CabeceraDeGrupo(cut, "Falta").QuerySelector(".alertas-grupo-meta")!.TextContent;
        meta.Should().Contain("porque ese centro lo tiene configurado")
            .And.Contain("si el centro no dice nada, porque el tipo de documento se pide siempre");
        meta.Should().NotContainEquivalentOf("obligatori", "es configuración, no una obligación legal");
    }

    // ---------------------------------------------------------------- Agrupación

    [Fact]
    public void Por_defecto_agrupa_por_severidad_del_mas_grave_al_menos_grave_con_su_recuento()
    {
        var cut = RenderizarLista(
            Alerta(EstadoDocumento.Proximo), Alerta(EstadoDocumento.Faltante), Alerta(EstadoDocumento.Vencido),
            Alerta(EstadoDocumento.Urgente), Alerta(EstadoDocumento.Vencido));

        TitulosDeGrupo(cut).Should().Equal(["Vencido", "Falta", "Urgente", "Próximo"],
            "el orden es de gravedad, no el de la consulta (que pone Falta primero)");
        InsigniasDeGrupo(cut).Should().Equal(["2", "1", "1", "1"]);
    }

    [Fact]
    public async Task Agrupar_por_motivo_hace_un_bloque_por_tipo_de_documento_y_lleva_la_severidad_a_la_fila()
    {
        var tipoFormacion = Guid.NewGuid();
        var tipoReconocimiento = Guid.NewGuid();
        var cut = RenderizarLista(
            Alerta(EstadoDocumento.Proximo, "Reconocimiento médico", tipoReconocimiento, "Ana García"),
            Alerta(EstadoDocumento.Vencido, "Formación PRL", tipoFormacion, "Diego Manrique"),
            Alerta(EstadoDocumento.Proximo, "Formación PRL", tipoFormacion, "Lucía Bengoa"));

        await cut.FindAll(".alertas-agrupar button").Single(b => b.TextContent.Trim() == "Motivo").ClickAsync(new MouseEventArgs());

        TitulosDeGrupo(cut).Should().Equal(["Formación PRL", "Reconocimiento médico"],
            "un bloque por tipo, el que tiene algo vencido delante");
        InsigniasDeGrupo(cut).Should().Equal(["Vencido", "Próximo"], "la insignia es la peor severidad del bloque");

        var formacion = cut.FindAll(".alertas-grupo")[0];
        formacion.QuerySelectorAll("thead th").Select(t => t.TextContent.Trim()).Should().Contain("Severidad");
        formacion.QuerySelectorAll("tbody tr").Select(f => f.TextContent).Should().SatisfyRespectively(
            f => f.Should().Contain("Diego Manrique").And.Contain("Vencido"),
            f => f.Should().Contain("Lucía Bengoa").And.Contain("Próximo"));
    }

    [Fact]
    public async Task Plegar_un_bloque_oculta_sus_filas_y_no_las_de_los_demas()
    {
        var cut = RenderizarLista(
            Alerta(EstadoDocumento.Vencido, trabajador: "Juan Pérez"),
            Alerta(EstadoDocumento.Proximo, trabajador: "Lucía Bengoa"));

        await CabeceraDeGrupo(cut, "Vencido").ClickAsync(new MouseEventArgs());

        CabeceraDeGrupo(cut, "Vencido").GetAttribute("aria-expanded").Should().Be("false");
        cut.Markup.Should().NotContain("Juan Pérez", "su bloque está plegado");
        cut.Markup.Should().Contain("Lucía Bengoa", "plegar un bloque no toca los demás");
    }

    /// <summary>
    /// La paginación corta los bloques. La insignia no puede contar solo lo que
    /// cabe en la página: diría 20 donde hay 25 vencidos.
    /// </summary>
    [Fact]
    public void La_insignia_cuenta_todo_el_bloque_aunque_la_pagina_solo_muestre_una_parte()
    {
        var cut = RenderizarLista(Enumerable.Range(0, 25).Select(_ => Alerta(EstadoDocumento.Vencido)).ToArray());

        InsigniasDeGrupo(cut).Should().Equal(["25"]);
        cut.Find(".alertas-grupo-meta").TextContent.Should().Contain("20 en esta página");
        cut.FindAll(".alertas-grupo tbody tr").Should().HaveCount(20, "el tamaño de página por defecto sigue siendo 20");
    }

    // ---------------------------------------------------------------- Reclamación con confirmación

    private static LoteReclamacionAgrupadoDto LoteCliente(string nombre = "Refrielectric S.A.", params Guid[] documentos) => new(
        ClienteId, nombre, AmbitoAplicacion.Trabajador, UltimaReclamacionFechaUtc: null,
        (documentos.Length == 0 ? [DocumentoA, DocumentoB] : documentos)
            .Select((id, i) => new DocumentoReclamableDto(
                id, Guid.NewGuid(), $"Trabajador {i + 1}", Guid.NewGuid(), i == 0 ? "Formación PRL" : "Reconocimiento médico",
                new DateOnly(2026, 8, 30), EstadoDocumento.Vencido))
            .ToList(),
        UltimaReclamacionConversacionId: null,
        [
            new DestinatarioAgendaDto(ContactoCarmen, "Carmen Ruiz", "carmen.ruiz@refrielectric.es", ["Formación PRL"]),
            new DestinatarioAgendaDto(ContactoIgnacio, "Ignacio Vera", "rrhh@refrielectric.es", ["Reconocimiento médico"])
        ]);

    private static LoteReclamacionAgrupadoDto LoteEmpresa(string nombre) => new(
        EmpresaId, nombre, AmbitoAplicacion.Empresa, UltimaReclamacionFechaUtc: null,
        [new DocumentoReclamableDto(DocumentoA, null, null, Guid.NewGuid(), "Seguro de responsabilidad civil", new DateOnly(2026, 9, 1), EstadoDocumento.Vencido)],
        UltimaReclamacionConversacionId: null,
        [new DestinatarioAgendaDto(ContactoCarmen, "Carmen Ruiz", "carmen.ruiz@refrielectric.es", ["Seguro"])]);

    /// <param name="lotes">Respuesta de cada ObtenerLoteReclamacionPorFiltroQuery, por orden de llegada.</param>
    private (IRenderedComponent<Features.Alertas.Pages.Alertas> Cut, MediatorRegistrador Mediator) RenderizarConLote(
        Func<object, object>? alEnviar = null, params IReadOnlyList<LoteReclamacionAgrupadoDto>[] lotes)
    {
        var llamadasLote = 0;
        var mediator = Registrar(peticion => RespuestaDeSelector(peticion) ?? peticion switch
        {
            ObtenerAlertasQuery => (object)Array.Empty<AlertaDto>(),
            ObtenerLoteReclamacionPorFiltroQuery => lotes[Math.Min(llamadasLote++, lotes.Length - 1)],
            EnviarReclamacionCommand cmd => alEnviar?.Invoke(peticion) ?? Result.Exito(new EnvioReclamacionResultado(cmd.DocumentoIds, [])),
            EnviarReclamacionEmpresaCommand cmd => alEnviar?.Invoke(peticion) ?? Result.Exito(new EnvioReclamacionResultado(cmd.DocumentoIds, [])),
            _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
        });
        Services.GetRequiredService<NavigationManager>().NavigateTo("alertas");
        return (Render<Features.Alertas.Pages.Alertas>(), mediator);
    }

    private static async Task AbrirSeccionYElegirFiltro(IRenderedComponent<Features.Alertas.Pages.Alertas> cut, AmbitoAplicacion? ambito = null) =>
        await await PulsarContinuar(cut, ambito);

    /// <summary>
    /// Devuelve el clic en «Continuar» SIN esperarlo: su manejador espera la
    /// carga del lote, y si esa carga es un TaskCompletionSource pendiente,
    /// esperar aquí bloquearía el test para siempre.
    /// </summary>
    private static async Task<Task> PulsarContinuar(IRenderedComponent<Features.Alertas.Pages.Alertas> cut, AmbitoAplicacion? ambito = null)
    {
        if (cut.FindAll(".selector-lote-documental").Count == 0)
            await cut.Find(".seccion-colapsable-cabecera").ClickAsync(new MouseEventArgs());

        if (ambito is { } a)
            await cut.Find(".selector-lote-documental select").ChangeAsync(new ChangeEventArgs { Value = a.ToString() });

        return cut.FindAll(".selector-lote-acciones button").Single(b => b.TextContent.Trim() == "Continuar").ClickAsync(new MouseEventArgs());
    }

    private static IElement BotonEnviarDeTarjeta(IRenderedComponent<Features.Alertas.Pages.Alertas> cut) =>
        cut.FindAll(".tarjeta-acciones button").Single(b => b.TextContent.Trim().StartsWith("Enviar reclamación"));

    private static IElement BotonDelDialogo(IRenderedComponent<Features.Alertas.Pages.Alertas> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == texto);

    [Fact]
    public async Task Enviar_reclamacion_no_envia_nada_y_pide_confirmacion_diciendo_a_quien()
    {
        var (cut, mediator) = RenderizarConLote(lotes: [[LoteCliente()]]);
        await AbrirSeccionYElegirFiltro(cut);

        await BotonEnviarDeTarjeta(cut).ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<EnviarReclamacionCommand>().Should().BeEmpty(
            "reclamar escribe a terceros: no puede salir de un solo clic");
        cut.Find("[role=dialog] h2").TextContent.Should().Contain("Refrielectric S.A.");
        cut.Find("[role=dialog]").TextContent.Should()
            .Contain("2 documento(s)").And.Contain("Carmen Ruiz").And.Contain("Ignacio Vera");
    }

    [Fact]
    public async Task Cancelar_la_confirmacion_no_envia_nada()
    {
        var (cut, mediator) = RenderizarConLote(lotes: [[LoteCliente()]]);
        await AbrirSeccionYElegirFiltro(cut);

        await BotonEnviarDeTarjeta(cut).ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<EnviarReclamacionCommand>().Should().BeEmpty();
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    /// <summary>
    /// El efecto, no el camino: se desmarca un documento y un contacto, y lo
    /// que llega al mediador es exactamente lo que quedó marcado, una sola vez.
    /// Después la tarjeta tiene que enseñar el lote nuevo, no el viejo.
    /// </summary>
    [Fact]
    public async Task Confirmar_envia_una_vez_lo_marcado_y_recarga_el_lote()
    {
        var (cut, mediator) = RenderizarConLote(lotes:
        [
            [LoteCliente()],
            [LoteCliente(nombre: "Refrielectric S.A. (recargado)", documentos: [DocumentoB])]
        ]);
        await AbrirSeccionYElegirFiltro(cut);

        await cut.Find("input[aria-label='Incluir Formación PRL en la reclamación']").ChangeAsync(new ChangeEventArgs { Value = false });
        await cut.FindAll(".reclamacion-destinatarios input[type=checkbox]")[1].ChangeAsync(new ChangeEventArgs { Value = false });
        await BotonEnviarDeTarjeta(cut).ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Enviar reclamación").ClickAsync(new MouseEventArgs());

        var enviado = mediator.Enviados.OfType<EnviarReclamacionCommand>().Should().ContainSingle().Subject;
        enviado.ClienteId.Should().Be(ClienteId);
        enviado.DocumentoIds.Should().Equal([DocumentoB], "Formación PRL se desmarcó");
        enviado.ContactoIdsSeleccionados.Should().Equal([ContactoCarmen], "Ignacio Vera se desmarcó");
        enviado.CentroId.Should().BeNull();

        mediator.Enviados.OfType<ObtenerLoteReclamacionPorFiltroQuery>().Should().HaveCount(2, "tras enviar se recarga el lote");
        cut.WaitForAssertion(() => cut.Find(".tarjeta-titulo").TextContent.Should().Be("Refrielectric S.A. (recargado)"));
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Exito);
    }

    [Fact]
    public async Task Un_lote_de_empresa_se_envia_con_el_comando_de_empresa()
    {
        var (cut, mediator) = RenderizarConLote(lotes: [[LoteEmpresa("Montajes Ebro S.L.")]]);
        await AbrirSeccionYElegirFiltro(cut, AmbitoAplicacion.Empresa);

        await BotonEnviarDeTarjeta(cut).ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Enviar reclamación").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<ObtenerLoteReclamacionPorFiltroQuery>().Select(q => q.Filtro.Ambito)
            .Should().NotBeEmpty().And.OnlyContain(a => a == AmbitoAplicacion.Empresa,
                "la pantalla tiene que pedir el lote del ámbito elegido, también al recargar tras enviar");
        mediator.Enviados.OfType<EnviarReclamacionCommand>().Should().BeEmpty();
        var enviado = mediator.Enviados.OfType<EnviarReclamacionEmpresaCommand>().Should().ContainSingle().Subject;
        enviado.EmpresaId.Should().Be(EmpresaId);
        enviado.DocumentoIds.Should().Equal([DocumentoA]);
        enviado.ContactoIdsSeleccionados.Should().Equal([ContactoCarmen]);
    }

    [Fact]
    public async Task Si_el_envio_falla_avisa_deja_el_dialogo_abierto_y_no_recarga()
    {
        var (cut, mediator) = RenderizarConLote(
            alEnviar: _ => Result.Fallo<EnvioReclamacionResultado>(Error.Crear("Reclamacion.SinDestinatarios", "Ningún contacto válido en la agenda.")),
            lotes: [[LoteCliente()]]);
        await AbrirSeccionYElegirFiltro(cut);

        await BotonEnviarDeTarjeta(cut).ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Enviar reclamación").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<EnviarReclamacionCommand>().Should().ContainSingle();
        Services.GetRequiredService<ToastService>().Mensajes.Should()
            .ContainSingle(m => m.Tono == TonoToast.Error && m.Mensaje == "Ningún contacto válido en la agenda.");
        cut.FindAll("[role=dialog]").Should().ContainSingle("tras un fallo el diálogo sigue ahí para reintentar o cancelar");
        mediator.Enviados.OfType<ObtenerLoteReclamacionPorFiltroQuery>().Should().ContainSingle("nada cambió: no hay que recargar");
    }

    // ---------------------------------------------------------------- Carrera

    /// <summary>
    /// Se pide el lote de un filtro, el usuario lo abandona antes de que llegue
    /// y pide otro. Si la primera respuesta llega después de la segunda, no
    /// puede pintarse: pondría bajo el filtro nuevo las tarjetas del viejo, y
    /// «Enviar» reclamaría esos documentos.
    /// </summary>
    [Fact]
    public async Task Una_respuesta_de_lote_que_llega_tarde_no_pisa_la_del_filtro_vigente()
    {
        var pendientes = new List<TaskCompletionSource<IReadOnlyList<LoteReclamacionAgrupadoDto>>>();
        var mediator = Registrar(peticion => RespuestaDeSelector(peticion) ?? peticion switch
        {
            ObtenerAlertasQuery => (object)Array.Empty<AlertaDto>(),
            ObtenerLoteReclamacionPorFiltroQuery => Pendiente(),
            _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
        });
        Services.GetRequiredService<NavigationManager>().NavigateTo("alertas");
        var cut = Render<Features.Alertas.Pages.Alertas>();

        var primera = await PulsarContinuar(cut);                              // 1.ª: Trabajador, queda en vuelo
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "← Elegir otro filtro").ClickAsync(new MouseEventArgs());
        var segunda = await PulsarContinuar(cut, AmbitoAplicacion.Empresa);    // 2.ª: Empresa, queda en vuelo

        mediator.Enviados.OfType<ObtenerLoteReclamacionPorFiltroQuery>().Select(q => q.Filtro.Ambito)
            .Should().Equal([AmbitoAplicacion.Trabajador, AmbitoAplicacion.Empresa], "es el punto de partida del caso");

        await cut.InvokeAsync(() => pendientes[1].SetResult([LoteEmpresa("Montajes Ebro S.L.")]));
        await segunda;
        cut.Find(".tarjeta-titulo").TextContent.Should().Be("Montajes Ebro S.L.");

        // La tardía se resuelve y se ESPERA a que su manejador termine: sin
        // eso, una aserción de ausencia podría pasar solo porque la escritura
        // tardía aún no había ocurrido.
        await cut.InvokeAsync(() => pendientes[0].SetResult([LoteCliente("Refrielectric S.A.")]));
        await primera;

        cut.FindAll(".tarjeta-titulo").Select(t => t.TextContent).Should().Equal(["Montajes Ebro S.L."],
            "la respuesta del filtro abandonado llegó tarde y no puede sustituir a la vigente");

        Task<IReadOnlyList<LoteReclamacionAgrupadoDto>> Pendiente()
        {
            var tcs = new TaskCompletionSource<IReadOnlyList<LoteReclamacionAgrupadoDto>>();
            pendientes.Add(tcs);
            return tcs.Task;
        }
    }
}
