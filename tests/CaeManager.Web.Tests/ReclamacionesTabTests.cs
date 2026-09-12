using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos;
using CaeManager.Application.Contactos.Commands.GuardarContactoAgenda;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionesEnviadas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// <c>ReclamacionesTab</c> quedó fuera a propósito del incremento que llevó
/// <c>/documentos</c> a su mockup Gen 2 (ver <c>DocumentosGen2Tests</c>) y
/// arrastraba los mismos tres defectos de concurrencia que allí se cerraron:
/// sin generación de carga vigente, sin <see cref="CancellationTokenSource"/>
/// de ciclo de vida, y guardas de reentrada comprobadas al salir en vez de al
/// entrar. Esta clase prueba solo eso — el contrato de concurrencia de la
/// pestaña, no su fidelidad a ningún mockup.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> que la respuesta de una página del
/// historial ya abandonada no pisa a la vigente; que las consultas viajan con
/// el token del ciclo y que retirar la pestaña cancela la que sigue en vuelo;
/// que <c>Dispose</c> es idempotente; y que dos clics en "Reclamar de nuevo" o
/// en "Enviar reclamación" mandan un solo comando.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> la autorización real de los handlers, el
/// contenido exacto de los correos, ni el aspecto (CSS).
/// </para>
/// </summary>
public class ReclamacionesTabTests : BunitContext
{
    /// <summary>La pestaña monta ModalContactoAgenda (Modal con JS interop para el foco), aunque Visible empiece en false.</summary>
    public ReclamacionesTabTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    // ---------------------------------------------------------------- dobles

    /// <summary>
    /// Mediador que responde por tipo, apunta lo enviado <b>y el token con el
    /// que llegó</b>, y permite retener una respuesta para provocar una
    /// carrera de verdad en vez de simularla. Mismo patrón que
    /// <c>DocumentosGen2Tests.MediadorControlado</c>.
    /// </summary>
    private sealed class MediadorControlado : IMediator
    {
        public List<object> Enviadas { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        /// <summary>Lo que devuelve <see cref="ObtenerReclamacionesEnviadasQuery"/> — puede depender de la página/tamaño pedidos.</summary>
        public Func<ObtenerReclamacionesEnviadasQuery, ResultadoPaginado<ReclamacionEnviadaDto>> Historial { get; set; } =
            q => new ResultadoPaginado<ReclamacionEnviadaDto>([], 0, q.Pagina, q.TamanoPagina);

        /// <summary>Lo que devuelve <see cref="ObtenerLoteReclamacionQuery"/> (vista de componer/enviar).</summary>
        public Func<ObtenerLoteReclamacionQuery, IReadOnlyList<LoteReclamacionClienteDto>> Lotes { get; set; } = _ => [];

        public Func<EnviarReclamacionCommand, Result<EnvioReclamacionResultado>>? AlEnviar { get; set; }

        public Func<EnviarReclamacionEmpresaCommand, Result<EnvioReclamacionResultado>>? AlEnviarEmpresa { get; set; }

        /// <summary>Si devuelve una tarea, esa consulta se queda esperando a que el test la suelte.</summary>
        public Func<object, Task<object?>?>? Interceptar { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            Tokens.Add(cancellationToken);

            if (Interceptar?.Invoke(request) is { } retenida)
                return (TResponse)(await retenida)!;

            return (TResponse)Responder(request)!;
        }

        private object? Responder(object request) => request switch
        {
            ObtenerReclamacionesEnviadasQuery q => Historial(q),
            ObtenerLoteReclamacionQuery q => Lotes(q),
            EnviarReclamacionCommand c => AlEnviar?.Invoke(c) ?? Result.Exito(new EnvioReclamacionResultado(c.DocumentoIds, [])),
            EnviarReclamacionEmpresaCommand c => AlEnviarEmpresa?.Invoke(c) ?? Result.Exito(new EnvioReclamacionResultado(c.DocumentoIds, [])),
            // La usa ModalContactoAgenda ("Añadir contacto"), que ReclamacionesTab monta siempre.
            GuardarContactoAgendaCommand => Result.Exito(Guid.NewGuid()),
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
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

    // ---------------------------------------------------------------- datos

    private static ReclamacionEnviadaDto ReclamacionEnviada(string razonSocial = "Refrielectric SL") => new(
        Guid.NewGuid(), Guid.NewGuid(), razonSocial, AmbitoAplicacion.Cliente, "contacto@refrielectric.example",
        DateTime.UtcNow.AddDays(-1), 1, null, null, [Guid.NewGuid()]);

    private static LoteReclamacionClienteDto LoteConTodoPreseleccionado(string razonSocial = "Arcos SPA") => new(
        Guid.NewGuid(), razonSocial, UltimaReclamacionFechaUtc: null,
        Documentos:
        [
            new DocumentoReclamableDto(
                Guid.NewGuid(), Guid.NewGuid(), "Salas Moreno, Javier", Guid.NewGuid(), "Reconocimiento médico",
                new DateOnly(2026, 1, 15), EstadoDocumento.Vigente)
        ],
        Destinatarios: [new DestinatarioAgendaDto(Guid.NewGuid(), "Marta Ruiz", "marta@arcos.example", ["RLC/TC1"])]);

    private static MediadorControlado ConHistorial(params ReclamacionEnviadaDto[] items) =>
        new() { Historial = q => new ResultadoPaginado<ReclamacionEnviadaDto>(items, items.Length, q.Pagina, q.TamanoPagina) };

    // ---------------------------------------------------------------- arnés

    private (IRenderedComponent<ReclamacionesTab> Cut, MediadorControlado Mediador) Renderizar(MediadorControlado? mediador = null)
    {
        mediador ??= new MediadorControlado();

        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();

        return (Render<ReclamacionesTab>(), mediador);
    }

    private static IElement BotonPorTexto(IRenderedComponent<ReclamacionesTab> cut, string texto) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == texto);

    private static async Task<IRenderedComponent<ReclamacionesTab>> AbrirComponerAsync(IRenderedComponent<ReclamacionesTab> cut)
    {
        await BotonPorTexto(cut, "+ Nueva reclamación").ClickAsync(new MouseEventArgs());
        return cut;
    }

    // ------------------------------------------------- contrato 1: concurrencia (historial)

    /// <summary>
    /// El historial oculta su propio paginador mientras carga (pasa a la
    /// rejilla de esqueleto), así que la única forma real de que dos cargas
    /// del historial se superpongan es que una la dispare el paginador y la
    /// otra la dispare <c>ReclamarDeNuevoAsync</c> al terminar — dos disparadores
    /// distintos, no el mismo control pulsado dos veces.
    ///
    /// <para>
    /// Orden real: se pide la página 2 (queda retenida), luego se reenvía una
    /// reclamación de la página 1 (también retenida). El reenvío termina
    /// PRIMERO y dispara su propia recarga de la página 2 (más reciente); la
    /// carga de página 2 que pidió el paginador —más antigua— responde
    /// DESPUÉS. Antes de esto, <c>ReclamacionesTab</c> no tenía ninguna
    /// generación de carga: la que respondiera último ganaba, llegara o no en
    /// orden, y la respuesta vieja podía pisar a la que sí vale.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_historial_de_una_recarga_ya_superada_no_pisa_a_la_que_la_supero()
    {
        var reclamacion = ReclamacionEnviada("Refrielectric SL");
        var mediador = new MediadorControlado
        {
            // Historial de 25 (dos páginas de 20) para que "Siguiente" esté activo.
            Historial = q => new ResultadoPaginado<ReclamacionEnviadaDto>([reclamacion], 25, q.Pagina, q.TamanoPagina)
        };

        var (cut, _) = Renderizar(mediador);
        cut.Markup.Should().Contain("Refrielectric SL", "es el punto de partida de este caso: la carga inicial ya respondió");

        // Las dos recargas del historial (página 2, y la que dispara el
        // reenvío al terminar) se retienen EN EL ORDEN en que se piden, sin
        // que su contenido dependa de la Página/TamañoPágina —da igual quién
        // llegue tarde, lo que se comprueba es la generación, no el dato—.
        var colaHistorial = new Queue<TaskCompletionSource<object?>>();
        var tcsPagina2 = new TaskCompletionSource<object?>();
        var tcsPostReenvio = new TaskCompletionSource<object?>();
        colaHistorial.Enqueue(tcsPagina2);
        colaHistorial.Enqueue(tcsPostReenvio);

        var tcsEnvio = new TaskCompletionSource<object?>();
        mediador.Interceptar = p => p switch
        {
            EnviarReclamacionCommand => tcsEnvio.Task,
            ObtenerReclamacionesEnviadasQuery when colaHistorial.Count > 0 => colaHistorial.Dequeue().Task,
            _ => null
        };

        var reenvio = BotonPorTexto(cut, "Reclamar de nuevo").ClickAsync(new MouseEventArgs());
        var siguiente = cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Siguiente"))
            .ClickAsync(new MouseEventArgs());

        // El reenvío termina primero y dispara su propia recarga (más
        // reciente); se resuelve con datos distintos de los de "página 2".
        await cut.InvokeAsync(() => tcsEnvio.SetResult(Result.Exito(new EnvioReclamacionResultado(reclamacion.DocumentoIds, []))));
        await cut.InvokeAsync(() => tcsPostReenvio.SetResult(
            new ResultadoPaginado<ReclamacionEnviadaDto>([ReclamacionEnviada("Post-reenvío SL")], 99, 2, 20)));
        await reenvio;

        cut.Markup.Should().Contain("Post-reenvío SL", "es la recarga vigente: la disparó lo último que terminó");

        // La recarga de "página 2" que pidió el paginador —más antigua—
        // responde AHORA, tarde: no puede pisar a la que ya se pintó.
        await cut.InvokeAsync(() => tcsPagina2.SetResult(
            new ResultadoPaginado<ReclamacionEnviadaDto>([ReclamacionEnviada("Página 2 SL")], 2, 2, 20)));
        await siguiente;

        cut.Markup.Should().Contain("Post-reenvío SL", "sigue siendo la vigente tras la respuesta tardía");
        cut.Markup.Should().NotContain("Página 2 SL", "su respuesta llegó tarde y ya no es la pregunta vigente");
    }

    /// <summary>
    /// Mismo contrato que el anterior, pero en la vista de componer/enviar:
    /// la vista también oculta sus tarjetas mientras carga, así que los dos
    /// disparadores que pueden solaparse son, de nuevo, dos distintos —el
    /// que dispara <c>EnviarAsync</c> al terminar y el que dispara
    /// "Añadir contacto" (<c>ModalContactoAgenda.OnGuardado</c>) al guardar—,
    /// no el mismo botón pulsado dos veces.
    /// </summary>
    [Fact]
    public async Task Los_lotes_de_una_recarga_ya_superada_no_pisan_a_la_que_la_supero()
    {
        var lote = LoteConTodoPreseleccionado("Arcos SPA");
        var mediador = new MediadorControlado { Lotes = _ => [lote] };

        var (cut, _) = Renderizar(mediador);
        await AbrirComponerAsync(cut);
        cut.Markup.Should().Contain("Arcos SPA", "es el punto de partida de este caso: la carga inicial ya respondió");

        var colaLotes = new Queue<TaskCompletionSource<object?>>();
        var tcsRecargaModal = new TaskCompletionSource<object?>();
        var tcsRecargaEnvio = new TaskCompletionSource<object?>();
        colaLotes.Enqueue(tcsRecargaModal);
        colaLotes.Enqueue(tcsRecargaEnvio);

        var tcsEnvio = new TaskCompletionSource<object?>();
        mediador.Interceptar = p => p switch
        {
            EnviarReclamacionCommand => tcsEnvio.Task,
            ObtenerLoteReclamacionQuery when colaLotes.Count > 0 => colaLotes.Dequeue().Task,
            _ => null
        };

        // Primero se envía la reclamación del lote (queda retenida)...
        var envio = BotonPorTexto(cut, $"Enviar reclamación ({lote.Documentos.Count})").ClickAsync(new MouseEventArgs());

        // ...y, con el envío todavía en vuelo, se da de alta un contacto —
        // ruta de código totalmente distinta que también recarga los lotes.
        // Su recarga (ObtenerLoteReclamacionQuery, dequeue #1) queda retenida
        // en tcsRecargaModal: no se puede esperar a que "Guardar" termine
        // todavía, o el test se bloquearía contra esa misma tarea.
        await BotonPorTexto(cut, "Añadir contacto").ClickAsync(new MouseEventArgs());
        var campoNombre = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre");
        var campoEmail = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Email");
        await cut.InvokeAsync(() => campoNombre.Instance.ValorChanged.InvokeAsync("Marta Ruiz"));
        await cut.InvokeAsync(() => campoEmail.Instance.ValorChanged.InvokeAsync("marta@arcos.example"));
        var altaContacto = cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Guardar")
            .ClickAsync(new MouseEventArgs());

        // El envío termina el ÚLTIMO y dispara la recarga más reciente
        // (dequeue #2, tcsRecargaEnvio).
        await cut.InvokeAsync(() => tcsEnvio.SetResult(Result.Exito(new EnvioReclamacionResultado(
            lote.Documentos.Select(d => d.DocumentoId).ToList(), []))));
        await cut.InvokeAsync(() => tcsRecargaEnvio.SetResult(new List<LoteReclamacionClienteDto> { LoteConTodoPreseleccionado("Post-envío SPA") }));
        await envio;

        cut.Markup.Should().Contain("Post-envío SPA", "es la recarga vigente: la disparó lo último que terminó");

        // La recarga que disparó el alta de contacto —más antigua— responde
        // ahora, tarde: no puede pisar a la que ya se pintó.
        await cut.InvokeAsync(() => tcsRecargaModal.SetResult(new List<LoteReclamacionClienteDto> { LoteConTodoPreseleccionado("Post-modal SPA") }));
        await altaContacto;

        cut.Markup.Should().Contain("Post-envío SPA", "sigue siendo la vigente tras la respuesta tardía");
        cut.Markup.Should().NotContain("Post-modal SPA", "su respuesta llegó tarde y ya no es la pregunta vigente");
    }

    // ------------------------------------------------- contrato 2: ciclo de vida

    /// <summary>
    /// Toda consulta de la pestaña viaja con un token cancelable, y retirar la
    /// pestaña corta la que siga en vuelo. Sin lo primero, lo segundo no sirve
    /// de nada: un <c>CancellationToken.None</c> no se entera de que la
    /// pestaña se fue.
    /// </summary>
    [Fact]
    public async Task Las_consultas_viajan_con_un_token_cancelable_y_retirar_la_pestana_corta_la_que_sigue_en_vuelo()
    {
        var mediador = ConHistorial(ReclamacionEnviada());
        var (cut, _) = Renderizar(mediador);

        mediador.Tokens.Should().NotBeEmpty("es el punto de partida de este caso");
        mediador.Tokens.Should().OnlyContain(t => t.CanBeCanceled,
            "una consulta con CancellationToken.None sigue trabajando para una pestaña que ya no existe");
        mediador.Tokens.Should().OnlyContain(t => !t.IsCancellationRequested);

        var enVuelo = new TaskCompletionSource<object?>();
        mediador.Interceptar = p => p is ObtenerReclamacionesEnviadasQuery ? enVuelo.Task : null;

        var recarga = cut.Find(".paginador-tamano-select").ChangeAsync(new ChangeEventArgs { Value = "50" });

        var tokenEnVuelo = mediador.Tokens[^1];
        tokenEnVuelo.IsCancellationRequested.Should().BeFalse("todavía no se ha retirado nada");

        cut.Instance.Dispose();

        tokenEnVuelo.IsCancellationRequested.Should().BeTrue(
            "retirar la pestaña corta la consulta que seguía trabajando para ella");

        mediador.Interceptar = null;
        enVuelo.SetResult(mediador.Historial(new ObtenerReclamacionesEnviadasQuery(1, 50)));
        await recarga;
    }

    /// <summary>
    /// Retirar la pestaña dos veces no puede reventar: <c>Dispose</c> es
    /// idempotente y no vuelve a tocar un <c>CancellationTokenSource</c> ya
    /// desechado.
    /// </summary>
    [Fact]
    public void Retirar_la_pestana_dos_veces_no_lanza()
    {
        var (cut, _) = Renderizar();

        var repetir = () => { cut.Instance.Dispose(); cut.Instance.Dispose(); };

        repetir.Should().NotThrow();
    }

    // ------------------------------------------------- contrato 3: reentrada

    /// <summary>
    /// Dos clics en "Reclamar de nuevo" mandan UN comando. El botón
    /// deshabilitado no basta: el segundo clic ya viajaba cuando se
    /// deshabilitó — la guarda tiene que comprobarse al entrar al método, no
    /// solo apagarse al salir.
    /// </summary>
    [Fact]
    public async Task Dos_clics_en_Reclamar_de_nuevo_mandan_un_solo_comando()
    {
        var reclamacion = ReclamacionEnviada();
        var mediador = ConHistorial(reclamacion);

        var retenido = new TaskCompletionSource<object?>();
        mediador.Interceptar = p => p is EnviarReclamacionCommand ? retenido.Task : null;

        var (cut, _) = Renderizar(mediador);

        // El botón cambia de delegado en cada render (la lambda del @foreach
        // cierra sobre "reclamacion"): capturarlo una sola vez y disparar dos
        // clics sobre esa misma referencia falla en bUnit con un event id ya
        // invalidado por el primer render, no con el defecto que se prueba.
        // Volver a buscarlo simula el clic real que sí llega tras ese primer
        // render — la guarda de entrada es la que tiene que pararlo.
        var primero = BotonPorTexto(cut, "Reclamar de nuevo").ClickAsync(new MouseEventArgs());
        var segundo = BotonPorTexto(cut, "Reclamar de nuevo").ClickAsync(new MouseEventArgs());

        await cut.InvokeAsync(() => retenido.SetResult(Result.Exito(new EnvioReclamacionResultado(reclamacion.DocumentoIds, []))));
        await primero;
        await segundo;

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().ContainSingle(
            "el segundo clic llegó con el primero todavía en vuelo");
    }

    /// <summary>
    /// Mismo contrato en la vista de componer/enviar: dos clics en "Enviar
    /// reclamación" mandan UN comando, no dos.
    /// </summary>
    [Fact]
    public async Task Dos_clics_en_Enviar_reclamacion_mandan_un_solo_comando()
    {
        var lote = LoteConTodoPreseleccionado();
        var mediador = new MediadorControlado { Lotes = _ => [lote] };

        var retenido = new TaskCompletionSource<object?>();
        mediador.Interceptar = p => p is EnviarReclamacionCommand ? retenido.Task : null;

        var (cut, _) = Renderizar(mediador);
        await AbrirComponerAsync(cut);

        // Mismo motivo que en el caso de "Reclamar de nuevo": el delegado del
        // botón cambia en cada render porque su lambda cierra sobre "lote".
        var texto = $"Enviar reclamación ({lote.Documentos.Count})";
        var primero = BotonPorTexto(cut, texto).ClickAsync(new MouseEventArgs());
        var segundo = BotonPorTexto(cut, texto).ClickAsync(new MouseEventArgs());

        await cut.InvokeAsync(() => retenido.SetResult(Result.Exito(new EnvioReclamacionResultado(
            lote.Documentos.Select(d => d.DocumentoId).ToList(), []))));
        await primero;
        await segundo;

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().ContainSingle(
            "el segundo clic llegó con el primero todavía en vuelo");
    }
}
