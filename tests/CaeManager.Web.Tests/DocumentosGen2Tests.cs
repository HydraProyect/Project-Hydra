using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Documentos.Commands.EliminarDocumento;
using CaeManager.Application.Documentos.Commands.EliminarDocumentos;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

// Alias: el tipo de la página se llama igual que su espacio de nombres, y sin
// esto el compilador resuelve "Documentos" al namespace.
using PaginaDocumentos = CaeManager.Web.Features.Documentos.Pages.Documentos;

/// <summary>
/// <c>/documentos</c> contra su mockup Gen 2 («Documentos TALVEG.dc.html») y
/// contra el contrato de concurrencia, reentrada y desenlaces de la pantalla.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> la cabecera Gen 2 (antetítulo del ciclo sobre
/// el título) y los rótulos de la tira de pestañas, con el apellido semántico
/// que el contrato terminológico exige donde el mockup abrevia; que la píldora
/// de recuento sale del total que devuelve la consulta y no de un número
/// inventado; que la respuesta de un filtro ya abandonado no pisa a la vigente;
/// que las consultas viajan con el token del ciclo y que retirar la página lo
/// cancela; que lo preparado para un contexto no se ejecuta en otro; que dos
/// envíos de una escritura mandan un comando y no dos; y que un lote que borró
/// menos de lo pedido no se anuncia como éxito.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> la autorización real ni el alcance de cartera (de
/// los handlers, en Application); el contenido de las cinco pestañas restantes,
/// que son componentes propios con sus propias consultas; el aspecto (CSS);
/// ni el aislamiento por tenant.
/// </para>
/// </summary>
public class DocumentosGen2Tests : BunitContext
{
    /// <summary>La página monta AtajosListaTeclado y Modal, que importan sus módulos JS.</summary>
    public DocumentosGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    // ---------------------------------------------------------------- dobles

    /// <summary>
    /// Mediador que responde por tipo, apunta lo enviado <b>y el token con el
    /// que llegó</b>, y permite retener una respuesta para provocar una carrera
    /// de verdad en vez de simularla.
    /// </summary>
    private sealed class MediadorControlado : IMediator
    {
        public List<object> Enviadas { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        /// <summary>Lista que devuelve <see cref="ObtenerDocumentosQuery"/> — puede depender de la consulta.</summary>
        public Func<ObtenerDocumentosQuery, IReadOnlyList<DocumentoListaDto>> Documentos { get; set; } = _ => [];

        /// <summary>Si devuelve una tarea, esa consulta se queda esperando a que el test la suelte.</summary>
        public Func<object, Task<object?>?>? Interceptar { get; set; }

        public Func<EliminarDocumentosCommand, Result<ResultadoEliminacionLoteDto>>? AlEliminarLote { get; set; }

        public Func<EliminarDocumentoCommand, Result>? AlEliminar { get; set; }

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
            ObtenerFiltrosGuardadosQuery => Array.Empty<FiltroGuardadoDto>(),
            ObtenerDocumentosQuery q => Pagina(q),
            GuardarFiltroCommand => Result.Exito(Guid.NewGuid()),
            EliminarDocumentoCommand c => AlEliminar?.Invoke(c) ?? Result.Exito(),
            EliminarDocumentosCommand c => AlEliminarLote?.Invoke(c)
                ?? Result.Exito(new ResultadoEliminacionLoteDto(c.Ids.Count, [])),
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
        };

        public ResultadoPaginado<DocumentoListaDto> Pagina(ObtenerDocumentosQuery q)
        {
            var documentos = Documentos(q);
            return new ResultadoPaginado<DocumentoListaDto>(documentos, documentos.Count, q.Pagina, q.TamanoPagina);
        }

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

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Estas pruebas no abren ningún archivo; si esto salta, la página cambió de camino.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Estas pruebas no convierten nada; si esto salta, la página cambió de camino.");
    }

    /// <summary>
    /// Evalúa los roles de verdad: uno que autorizara siempre dejaría pasar lo
    /// que la página oculta por rol. Mismo montaje que DocumentosVacioPorFiltroTests.
    /// </summary>
    private sealed class AutorizacionPorRoles : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        {
            var cumple = requirements.All(r => r switch
            {
                RolesAuthorizationRequirement roles => roles.AllowedRoles.Any(user.IsInRole),
                DenyAnonymousAuthorizationRequirement => user.Identity?.IsAuthenticated == true,
                _ => true
            });

            return Task.FromResult(cumple ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(AuthorizationResult.Success());
    }

    private sealed class AutenticacionFalsa : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Role, "Administrador")], "test"))));
    }

    // ---------------------------------------------------------------- datos

    private static DocumentoListaDto Documento(string tipoDocumento) => new(
        Guid.NewGuid(), AmbitoAplicacion.Trabajador, "Salas Moreno, Javier", tipoDocumento,
        new DateOnly(2026, 1, 15), new DateOnly(2027, 1, 15), EstadoDocumento.Vigente,
        ArchivoUrl: null, Acreditaciones: []);

    // ---------------------------------------------------------------- arnés

    private (IRenderedComponent<PaginaDocumentos> Cut, MediadorControlado Mediador) Renderizar(
        MediadorControlado? mediador = null, string url = "documentos")
    {
        mediador ??= new MediadorControlado();

        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton<ILogger<PaginaDocumentos>>(_ => NullLogger<PaginaDocumentos>.Instance);
        Services.AddScoped<AuthenticationStateProvider, AutenticacionFalsa>();
        Services.AddAuthorizationCore();
        Services.AddScoped<IAuthorizationService, AutorizacionPorRoles>();
        Services.AddCascadingAuthenticationState();

        Services.GetRequiredService<NavigationManager>().NavigateTo(url);

        return (Render<PaginaDocumentos>(), mediador);
    }

    private static MediadorControlado ConDocumentos(params DocumentoListaDto[] documentos) =>
        new() { Documentos = _ => documentos };

    private static IReadOnlyList<IElement> Pestanas(IRenderedComponent<PaginaDocumentos> cut) =>
        cut.FindAll("[role=tablist] [role=tab]");

    private static IElement Pestana(IRenderedComponent<PaginaDocumentos> cut, string rotulo) =>
        Pestanas(cut).Single(p => p.TextContent.Contains(rotulo, StringComparison.Ordinal));

    private IReadOnlyList<ToastMensaje> Toasts() => Services.GetRequiredService<ToastService>().Mensajes;

    private static IElement BotonPorTexto(IRenderedComponent<PaginaDocumentos> cut, string selector, string texto) =>
        cut.FindAll(selector).Single(b => b.TextContent.Trim() == texto);

    /// <summary>Activa los checkboxes de fila y marca las <paramref name="cuantas"/> primeras.</summary>
    private static async Task SeleccionarFilas(IRenderedComponent<PaginaDocumentos> cut, int cuantas)
    {
        await BotonPorTexto(cut, ".barra-herramientas-lista button", "Selección múltiple").ClickAsync(new MouseEventArgs());

        var casillas = cut.FindAll(".tabla-datos tbody input[type=checkbox]");
        for (var i = 0; i < cuantas; i++)
            await cut.FindAll(".tabla-datos tbody input[type=checkbox]")[i].ChangeAsync(new ChangeEventArgs { Value = true });

        casillas.Count.Should().BeGreaterThanOrEqualTo(cuantas, "sin filas que marcar no hay lote que probar");
    }

    /// <summary>
    /// Marca la primera fila encendiendo el modo de selección solo si hace
    /// falta: «Selección múltiple» es un interruptor, y volver a pulsarlo
    /// cuando ya está encendido lo apaga y deja la tabla sin casillas.
    /// </summary>
    private static async Task MarcarPrimeraFila(IRenderedComponent<PaginaDocumentos> cut)
    {
        if (cut.FindAll(".tabla-datos tbody input[type=checkbox]").Count == 0)
            await BotonPorTexto(cut, ".barra-herramientas-lista button", "Selección múltiple").ClickAsync(new MouseEventArgs());

        cut.FindAll(".tabla-datos tbody input[type=checkbox]").Should().NotBeEmpty(
            "sin casillas no hay lote que preparar, y el caso se quedaría sin observar nada");
        await cut.FindAll(".tabla-datos tbody input[type=checkbox]")[0].ChangeAsync(new ChangeEventArgs { Value = true });
    }

    private static Task AbrirConfirmacionDeLote(IRenderedComponent<PaginaDocumentos> cut) =>
        BotonPorTexto(cut, ".barra-acciones-lote button", "Eliminar seleccionados").ClickAsync(new MouseEventArgs());

    /// <summary>El botón de confirmar del diálogo abierto — el destructivo del pie de la modal.</summary>
    private static Task ConfirmarDialogo(IRenderedComponent<PaginaDocumentos> cut) =>
        BotonPorTexto(cut, ".modal-pie button", "Eliminar").ClickAsync(new MouseEventArgs());

    // ---------------------------------------------------------------- mockup Gen 2

    /// <summary>
    /// El mockup pinta sobre el <c>h1</c> el antetítulo del ciclo al que
    /// pertenece la pantalla. La cabecera pasa a la primitiva
    /// <see cref="CabeceraPagina"/>, que es la única que sabe pintarlo.
    /// </summary>
    [Fact]
    public void La_cabecera_lleva_el_antetitulo_del_ciclo_documental_sobre_el_titulo()
    {
        var (cut, _) = Renderizar();

        var cabecera = cut.Find(".cabecera-pagina");
        cabecera.QuerySelector(".cabecera-pagina-kicker")!.TextContent.Trim().Should().Be("Ciclo documental");
        cabecera.QuerySelector("h1.titulo-pagina")!.TextContent.Trim().Should().Be("Documentos");
    }

    /// <summary>
    /// Las acciones del mockup siguen en la cabecera, y con ellas «Exportar a
    /// Excel», que el mockup no dibuja: un mockup omite comportamiento, no lo
    /// deroga. La barrera va delante para que la aserción no sea verde vacío.
    /// </summary>
    [Fact]
    public void Las_acciones_de_cabecera_conservan_la_exportacion_que_el_mockup_no_dibuja()
    {
        var (cut, _) = Renderizar();

        var acciones = cut.Find(".cabecera-pagina .acciones-cabecera");
        acciones.TextContent.Should().Contain("+ Nuevo documento", "es el punto de partida de este caso");
        acciones.TextContent.Should().Contain("Subida múltiple");
        acciones.TextContent.Should().Contain("Exportar a Excel");
    }

    /// <summary>
    /// Los rótulos son los del mockup salvo donde éste abrevia un término del
    /// contrato: «plataforma» a secas colisiona con el plano Plataforma del
    /// ADR-011 —quien accede excepcionalmente desde TALVEG—, mientras que lo
    /// que esta pestaña enseña son Plataformas CAE externas (Nalanda, Dokify,
    /// CTAIMA), que es como las nombra el propio dominio.
    /// </summary>
    [Fact]
    public void Los_rotulos_de_las_pestanas_son_los_del_mockup_con_el_apellido_semantico_del_contrato()
    {
        var (cut, _) = Renderizar();

        Pestanas(cut).Select(p => p.TextContent.Trim()).Should().BeEquivalentTo(
            ["Estado", "Plataformas CAE", "Reclamaciones", "Preventivo", "Revisión IA", "Plantillas"],
            o => o.WithoutStrictOrdering(),
            "la píldora de recuento puede añadir texto, pero los rótulos son estos");

        Pestanas(cut)[1].TextContent.Trim().Should().Be("Plataformas CAE");
        Pestanas(cut)[1].TextContent.Should().NotBe("Plataforma",
            "«Plataforma» a secas es el plano de acceso privilegiado, no el portal CAE externo");
    }

    /// <summary>
    /// El deep-link de pestaña y los Ids de la URL NO cambian al cambiar los
    /// rótulos: el timeline de Comunicaciones enlaza por Id.
    /// </summary>
    [Fact]
    public void Cambiar_los_rotulos_no_cambio_los_ids_que_viajan_en_la_url()
    {
        var (cut, _) = Renderizar(url: "documentos?pestana=reclamaciones");

        Pestana(cut, "Reclamaciones").GetAttribute("aria-selected").Should().Be("true");
        Pestana(cut, "Estado").GetAttribute("aria-selected").Should().Be("false");
    }

    /// <summary>
    /// El mockup pone una píldora con el recuento en la pestaña. El número sale
    /// del total que ya devuelve la consulta —con su filtrado aplicado—, no de
    /// un recuento inventado.
    /// </summary>
    [Fact]
    public void La_pestana_Estado_lleva_la_pildora_con_el_total_que_devolvio_la_consulta()
    {
        var (cut, _) = Renderizar(ConDocumentos(Documento("Reconocimiento médico"), Documento("Formación PRL")));

        Pestana(cut, "Estado").QuerySelector(".pestanas-contador")!.TextContent
            .Should().Contain("2").And.Contain("documentos");
    }

    /// <summary>
    /// La glosa de la píldora es lo único que convierte «1» en algo legible sin
    /// ver el color, y va en singular cuando toca.
    /// </summary>
    [Fact]
    public void La_pildora_dice_en_singular_lo_que_cuenta()
    {
        var (cut, _) = Renderizar(ConDocumentos(Documento("Reconocimiento médico")));

        var pildora = Pestana(cut, "Estado").QuerySelector(".pestanas-contador")!.TextContent.Trim();
        pildora.Should().Contain("1 documento");
        pildora.Should().NotContain("documentos");
    }

    /// <summary>
    /// Un total de cero no pinta píldora. «Estado 0» no dice nada que el estado
    /// vacío de la lista no diga mejor, y el mockup tampoco dibuja ninguna.
    /// </summary>
    [Fact]
    public void Sin_documentos_la_pestana_Estado_no_pinta_una_pildora_de_cero()
    {
        var (cut, _) = Renderizar();

        Pestana(cut, "Estado").QuerySelector(".pestanas-contador").Should().BeNull();
        cut.Markup.Should().Contain("Aún no hay documentos",
            "la barrera: si la lista no hubiera respondido, la ausencia de píldora sería verde vacío");
    }

    /// <summary>
    /// Las otras cinco pestañas no llevan píldora: sus recuentos exigirían una
    /// consulta propia que hoy no existe, y un cero sin contar es peor que
    /// ningún número.
    /// </summary>
    [Fact]
    public void Las_pestanas_sin_recuento_medido_no_inventan_una_pildora()
    {
        var (cut, _) = Renderizar(ConDocumentos(Documento("Reconocimiento médico")));

        Pestana(cut, "Estado").QuerySelector(".pestanas-contador").Should().NotBeNull("es el punto de partida de este caso");

        foreach (var rotulo in new[] { "Plataformas CAE", "Reclamaciones", "Preventivo", "Revisión IA", "Plantillas" })
            Pestana(cut, rotulo).QuerySelector(".pestanas-contador").Should().BeNull(
                $"«{rotulo}» no tiene hoy ninguna consulta que cuente lo suyo");
    }

    // ------------------------------------------------- contrato 1: concurrencia

    /// <summary>
    /// Dos filtros seguidos: el segundo responde primero y el primero llega
    /// tarde. El total que se pinta —la píldora de la pestaña— tiene que ser el
    /// de la pregunta vigente.
    /// </summary>
    [Fact]
    public async Task El_total_de_un_filtro_ya_abandonado_no_pisa_al_de_la_pregunta_vigente()
    {
        var vencidos = new[] { Documento("Formación PRL") };
        var urgentes = new[] { Documento("Reconocimiento médico"), Documento("ITV"), Documento("Seguro RC") };

        var respuestaVencidos = new TaskCompletionSource<object?>();
        var respuestaUrgentes = new TaskCompletionSource<object?>();
        var mediador = new MediadorControlado
        {
            Documentos = q => q.Estado switch
            {
                EstadoDocumento.Vencido => vencidos,
                EstadoDocumento.Urgente => urgentes,
                _ => []
            }
        };
        mediador.Interceptar = p => p switch
        {
            ObtenerDocumentosQuery { Estado: EstadoDocumento.Vencido } => respuestaVencidos.Task,
            ObtenerDocumentosQuery { Estado: EstadoDocumento.Urgente } => respuestaUrgentes.Task,
            _ => null
        };

        var (cut, _) = Renderizar(mediador);

        var selectorEstado = cut.FindAll(".barra-filtros select")[1];
        var filtroVencido = selectorEstado.ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoDocumento.Vencido) });
        var filtroUrgente = cut.FindAll(".barra-filtros select")[1]
            .ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoDocumento.Urgente) });

        // El vigente responde primero; el abandonado, después.
        await cut.InvokeAsync(() => respuestaUrgentes.SetResult(
            mediador.Pagina(new ObtenerDocumentosQuery(null, null, null, EstadoDocumento.Urgente))));
        await filtroUrgente;
        await cut.InvokeAsync(() => respuestaVencidos.SetResult(
            mediador.Pagina(new ObtenerDocumentosQuery(null, null, null, EstadoDocumento.Vencido))));
        await filtroVencido;

        Pestana(cut, "Estado").QuerySelector(".pestanas-contador")!.TextContent
            .Should().Contain("3", "el filtro vigente es «Urgente», que devolvió tres: la respuesta de «Vencido» llegó tarde");
    }

    /// <summary>
    /// Toda consulta de la página viaja con un token cancelable, y retirar la
    /// página corta la que siga en vuelo. Sin lo primero, lo segundo no sirve
    /// de nada: un <c>CancellationToken.None</c> no se entera de que la página
    /// se fue.
    ///
    /// La comprobación se hace sobre una consulta <b>todavía en vuelo</b>, no
    /// sobre los tokens de las ya terminadas. El de la rejilla es un token
    /// enlazado —ciclo de vida y QuickGrid— cuya fuente se desecha al terminar
    /// la consulta, así que un token guardado post mortem no dice nada de nada;
    /// lo que el contrato promete es cortar el trabajo que sigue abierto.
    /// </summary>
    [Fact]
    public async Task Las_consultas_viajan_con_un_token_cancelable_y_retirar_la_pagina_corta_la_que_sigue_en_vuelo()
    {
        var enVuelo = new TaskCompletionSource<object?>();
        var mediador = ConDocumentos(Documento("Reconocimiento médico"));
        var (cut, _) = Renderizar(mediador);

        mediador.Tokens.Should().NotBeEmpty("es el punto de partida de este caso");
        mediador.Tokens.Should().OnlyContain(t => t.CanBeCanceled,
            "una consulta con CancellationToken.None sigue trabajando para una página que ya no existe");
        mediador.Tokens.Should().OnlyContain(t => !t.IsCancellationRequested);

        // Una consulta que se queda esperando: es la que de verdad hay que cortar.
        mediador.Interceptar = p => p is ObtenerDocumentosQuery ? enVuelo.Task : null;
        var recarga = cut.FindAll(".barra-filtros select")[1]
            .ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoDocumento.Vencido) });

        var tokenEnVuelo = mediador.Tokens[^1];
        tokenEnVuelo.IsCancellationRequested.Should().BeFalse("todavía no se ha retirado nada");

        cut.Instance.Dispose();

        tokenEnVuelo.IsCancellationRequested.Should().BeTrue(
            "retirar la página corta la consulta que seguía trabajando para ella");

        mediador.Interceptar = null;
        enVuelo.SetResult(mediador.Pagina(new ObtenerDocumentosQuery(null, null, null, EstadoDocumento.Vencido)));
        await recarga;
    }

    /// <summary>
    /// Retirar la página dos veces no puede reventar: <c>Dispose</c> es
    /// idempotente y no vuelve a tocar un <c>CancellationTokenSource</c> ya
    /// desechado.
    /// </summary>
    [Fact]
    public void Retirar_la_pagina_dos_veces_no_lanza()
    {
        var (cut, _) = Renderizar();

        var repetir = () => { cut.Instance.Dispose(); cut.Instance.Dispose(); };

        repetir.Should().NotThrow();
    }

    // --------------------------------- contrato 2: nada preparado para otro contexto

    /// <summary>
    /// El diálogo de borrado y la barra de lote se pintan FUERA del bloque de
    /// pestañas, así que sobrevivían al cambio: dejar preparado un borrado en
    /// «Estado» y cambiar de pestaña dejaba el diálogo apuntando a un documento
    /// que ya no se ve.
    /// </summary>
    [Fact]
    public async Task Cambiar_de_pestana_cierra_el_lote_preparado_y_suelta_la_seleccion()
    {
        var (cut, mediador) = Renderizar(ConDocumentos(Documento("Reconocimiento médico"), Documento("Formación PRL")));

        await SeleccionarFilas(cut, 2);
        await AbrirConfirmacionDeLote(cut);
        cut.Markup.Should().Contain("Se ocultarán de las listas activas", "es el punto de partida de este caso");

        await Pestana(cut, "Preventivo").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().NotContain("Se ocultarán de las listas activas",
            "lo preparado pertenecía a la pestaña anterior");
        cut.FindAll(".barra-acciones-lote").Should().BeEmpty("la selección de la pestaña anterior se soltó");
        mediador.Enviadas.OfType<EliminarDocumentosCommand>().Should().BeEmpty(
            "cerrar lo preparado no es ejecutarlo");
    }

    /// <summary>
    /// Mismo contrato por el otro camino: cambiar un filtro cambia qué hay en
    /// pantalla. La lista se recarga y la selección se vacía; si el diálogo
    /// siguiera abierto, confirmarlo mandaría un lote sin filas.
    /// </summary>
    [Fact]
    public async Task Cambiar_un_filtro_cierra_el_lote_preparado_y_no_manda_un_borrado_vacio()
    {
        var (cut, mediador) = Renderizar(ConDocumentos(Documento("Reconocimiento médico"), Documento("Formación PRL")));

        await SeleccionarFilas(cut, 2);
        await AbrirConfirmacionDeLote(cut);
        cut.Markup.Should().Contain("Se ocultarán de las listas activas", "es el punto de partida de este caso");

        await cut.FindAll(".barra-filtros select")[1]
            .ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoDocumento.Vencido) });

        cut.Markup.Should().NotContain("Se ocultarán de las listas activas");
        mediador.Enviadas.OfType<EliminarDocumentosCommand>().Should().BeEmpty();
    }

    // ------------------------------------------------- contrato 3: reentrada

    /// <summary>
    /// Dos envíos del mismo formulario mandan UN comando. El botón deshabilitado
    /// no basta: el segundo clic ya viajaba cuando se deshabilitó, y en el
    /// servidor nada lo descarta por llegar a un botón ya apagado.
    ///
    /// <para>
    /// Se prueba sobre «Guardar filtro» a propósito: es una <see cref="Modal"/>
    /// con un <see cref="Boton"/> pelado. El borrado pasa por
    /// <see cref="DialogoConfirmacion"/>, que ya trae guarda propia, y por ahí
    /// no se demostraría la guarda de la página.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Dos_envios_de_Guardar_filtro_mandan_un_solo_comando()
    {
        var retenido = new TaskCompletionSource<object?>();
        var mediador = ConDocumentos(Documento("Reconocimiento médico"));
        mediador.Interceptar = p => p is GuardarFiltroCommand ? retenido.Task : null;

        var (cut, _) = Renderizar(mediador);

        await BotonPorTexto(cut, ".barra-filtros button", "Guardar filtro").ClickAsync(new MouseEventArgs());
        // El campo rebota 300 ms sobre oninput y no engancha onchange: teclearlo
        // por DOM dejaría vivo un temporizador que se traga el clic siguiente
        // (medido en TiposDocumentoVacioPorFiltroTests). Se invoca su callback.
        var campoNombre = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre");
        await cut.InvokeAsync(() => campoNombre.Instance.ValorChanged.InvokeAsync("Vencidos que bloquean centro"));

        var guardar = cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Guardar");
        var primero = guardar.ClickAsync(new MouseEventArgs());
        var segundo = guardar.ClickAsync(new MouseEventArgs());

        await cut.InvokeAsync(() => retenido.SetResult(Result.Exito(Guid.NewGuid())));
        await primero;
        await segundo;

        mediador.Enviadas.OfType<GuardarFiltroCommand>().Should().ContainSingle(
            "el segundo envío llegó con el primero todavía en vuelo");
    }

    // ------------------------------------------------- contrato 4: desenlaces honestos

    /// <summary>
    /// <b>Hizo todo lo pedido.</b> Se piden DOS y el comando borra DOS: pedir
    /// dos y dar por bueno recibir uno sería llamar éxito a un lote incompleto.
    /// </summary>
    [Fact]
    public async Task Un_lote_que_borro_todo_lo_pedido_si_se_anuncia_como_exito()
    {
        var mediador = ConDocumentos(Documento("Reconocimiento médico"), Documento("Formación PRL"));
        mediador.AlEliminarLote = c => Result.Exito(new ResultadoEliminacionLoteDto(c.Ids.Count, []));

        var (cut, _) = Renderizar(mediador);

        await SeleccionarFilas(cut, 2);
        await AbrirConfirmacionDeLote(cut);
        await ConfirmarDialogo(cut);

        mediador.Enviadas.OfType<EliminarDocumentosCommand>().Single().Ids.Should().HaveCount(2,
            "el caso solo vale si de verdad se pidieron dos");
        Toasts().Should().ContainSingle(t => t.Tono == TonoToast.Exito);
    }

    /// <summary>
    /// <b>Hizo parte.</b> Dos pedidos, uno borrado y NINGÚN error: la lista de
    /// errores no es la medida de lo hecho. El código anterior anunciaba esto
    /// como éxito porque solo miraba <c>Errores.Count == 0</c>.
    /// </summary>
    [Fact]
    public async Task Un_lote_que_borro_menos_de_lo_pedido_no_se_anuncia_como_exito()
    {
        var mediador = ConDocumentos(Documento("Reconocimiento médico"), Documento("Formación PRL"));
        mediador.AlEliminarLote = _ => Result.Exito(new ResultadoEliminacionLoteDto(1, []));

        var (cut, _) = Renderizar(mediador);

        await SeleccionarFilas(cut, 2);
        await AbrirConfirmacionDeLote(cut);
        await ConfirmarDialogo(cut);

        mediador.Enviadas.OfType<EliminarDocumentosCommand>().Single().Ids.Should().HaveCount(2,
            "el caso solo vale si de verdad se pidieron dos y volvió uno");

        var aviso = Toasts().Should().ContainSingle().Subject;
        aviso.Tono.Should().NotBe(TonoToast.Exito, "un lote incompleto no es un éxito aunque no traiga errores");
        aviso.Mensaje.Should().Contain("1 de 2", "quien lo lee tiene que poder saber cuánto quedó sin hacer");
    }

    /// <summary>
    /// <b>Hizo cero.</b> Un vacío no se presenta como logro.
    /// </summary>
    [Fact]
    public async Task Un_lote_que_no_borro_nada_no_se_anuncia_como_exito()
    {
        var mediador = ConDocumentos(Documento("Reconocimiento médico"), Documento("Formación PRL"));
        mediador.AlEliminarLote = _ => Result.Exito(new ResultadoEliminacionLoteDto(0, []));

        var (cut, _) = Renderizar(mediador);

        await SeleccionarFilas(cut, 2);
        await AbrirConfirmacionDeLote(cut);
        await ConfirmarDialogo(cut);

        var aviso = Toasts().Should().ContainSingle().Subject;
        aviso.Tono.Should().NotBe(TonoToast.Exito);
        aviso.Mensaje.Should().Contain("No se eliminó ningún documento");
    }

    /// <summary>
    /// Un <c>Result</c> fallido no trae DTO: leer <c>.Valor</c> sin mirarlo
    /// reventaba, y la excepción acababa en el catch genérico con un texto que
    /// se comía el motivo que dio el servidor.
    /// </summary>
    [Fact]
    public async Task Un_lote_rechazado_por_el_servidor_muestra_su_motivo_y_no_un_texto_generico()
    {
        var mediador = ConDocumentos(Documento("Reconocimiento médico"), Documento("Formación PRL"));
        mediador.AlEliminarLote = _ => Result.Fallo<ResultadoEliminacionLoteDto>(
            Error.Crear("Documento.SinIdentidad", "No hay identidad con la que borrar."));

        var (cut, _) = Renderizar(mediador);

        await SeleccionarFilas(cut, 2);
        await AbrirConfirmacionDeLote(cut);
        await ConfirmarDialogo(cut);

        var aviso = Toasts().Should().ContainSingle().Subject;
        aviso.Tono.Should().Be(TonoToast.Error);
        aviso.Mensaje.Should().Be("No hay identidad con la que borrar.",
            "el servidor dice qué está mal: repetirlo es útil, «intenta nuevamente» invita a fallar otra vez igual");
    }

    /// <summary>
    /// El hueco que el autor dejó declarado y que la revisión de Codex
    /// confirmó: la comprobación de contexto solo protegía la liberación de la
    /// bandera en el <c>finally</c>, no el cuerpo posterior al <c>await</c>. Un
    /// borrado en lote iniciado en un contexto y terminado en otro cerraba el
    /// diálogo que hubiera abierto ahora, le vaciaba la selección y le
    /// recargaba la lista por debajo.
    /// </summary>
    [Fact]
    public async Task Un_lote_que_termina_tras_cambiar_de_contexto_no_cierra_lo_que_se_haya_preparado_despues()
    {
        var enVuelo = new TaskCompletionSource<object?>();
        var mediador = ConDocumentos(Documento("Reconocimiento médico"), Documento("Formación PRL"));
        mediador.Interceptar = p => p is EliminarDocumentosCommand ? enVuelo.Task : null;
        var (cut, _) = Renderizar(mediador);

        await SeleccionarFilas(cut, 2);
        await AbrirConfirmacionDeLote(cut);
        // Sin await: el comando queda retenido y su continuación, pendiente.
        var loteDelPrimerContexto = ConfirmarDialogo(cut);

        // Cambia lo que hay en pantalla, dos veces, y se prepara otro lote.
        await Pestana(cut, "Preventivo").ClickAsync(new MouseEventArgs());
        await Pestana(cut, "Estado").ClickAsync(new MouseEventArgs());
        await MarcarPrimeraFila(cut);
        await AbrirConfirmacionDeLote(cut);
        cut.Markup.Should().Contain("Se ocultarán de las listas activas", "es el punto de partida de este caso");

        // Ahora termina el borrado del contexto anterior.
        await cut.InvokeAsync(() => enVuelo.SetResult(Result.Exito(new ResultadoEliminacionLoteDto(2, []))));
        await loteDelPrimerContexto;

        cut.Markup.Should().Contain("Se ocultarán de las listas activas",
            "lo que terminó pertenecía a otro contexto: no puede cerrar el diálogo que se acaba de abrir");
        cut.FindAll(".barra-acciones-lote").Should().NotBeEmpty(
            "ni vaciar la selección que se acaba de hacer");
        Toasts().Select(t => t.Mensaje).Should().ContainMatch("*documento(s) eliminado(s)*",
            "el aviso sí se da: los documentos se eliminaron de verdad");
    }
}
