using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Documentos.Commands.EliminarDocumento;
using CaeManager.Application.Documentos.Commands.EliminarDocumentos;
using CaeManager.Application.Documentos.Commands.RestaurarDocumento;
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
/// <b>Lo que esto SÍ observa:</b> la cabecera del listado (contador medido junto a
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

        /// <summary>
        /// Tenants que alcanza el usuario. Por defecto uno solo (el de origen): sin selector ni cabecera
        /// de empresa gestionada, la pantalla de siempre. Retener la respuesta: <see cref="Interceptar"/>.
        /// </summary>
        public List<ClienteAutorizadoDto> Autorizados { get; } = [new(Guid.NewGuid(), "Propia", EsOrigen: true)];

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
            ObtenerClientesAutorizadosQuery => (IReadOnlyList<ClienteAutorizadoDto>)Autorizados.ToList(),
            ObtenerDocumentosQuery q => Pagina(q),
            GuardarFiltroCommand => Result.Exito(Guid.NewGuid()),
            EliminarDocumentoCommand c => AlEliminar?.Invoke(c) ?? Result.Exito(),
            EliminarDocumentosCommand c => AlEliminarLote?.Invoke(c)
                ?? Result.Exito(new ResultadoEliminacionLoteDto(c.Ids.Count, [])),
            RestaurarDocumentoCommand => Result.Exito(),
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

    private sealed class UsuarioActualFalso(string rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
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

    private sealed class AutenticacionFalsa(string rol) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "test"))));
    }

    // ---------------------------------------------------------------- datos

    private static DocumentoListaDto Documento(string tipoDocumento) => new(
        Guid.NewGuid(), AmbitoAplicacion.Trabajador, "Salas Moreno, Javier", tipoDocumento,
        new DateOnly(2026, 1, 15), new DateOnly(2027, 1, 15), EstadoDocumento.Vigente,
        ArchivoUrl: null, Acreditaciones: []);

    // ------------------------------------- Borrar un filtro guardado (T9)

    [Fact]
    public async Task Borrar_un_filtro_guardado_pide_confirmacion_y_solo_entonces_lo_borra()
    {
        var filtro = new FiltroGuardadoDto(Guid.NewGuid(), "Vencidos", "{\"Estado\":\"Vencido\"}", DateTime.UtcNow);
        var guardados = new List<FiltroGuardadoDto> { filtro };
        var mediador = new MediadorControlado();
        mediador.Interceptar = p => p switch
        {
            ObtenerFiltrosGuardadosQuery => Task.FromResult<object?>(guardados.ToArray()),
            EliminarFiltroGuardadoCommand e => Task.FromResult<object?>(BorrarDe(guardados, e.Id)),
            _ => null,
        };
        var (cut, _) = Renderizar(mediador);
        cut.WaitForAssertion(() => PastillaDocumentoFase1(cut, "Más filtros"));

        await PastillaDocumentoFase1(cut, "Más filtros").ClickAsync(new MouseEventArgs());
        await cut.Find("[aria-label='Borrar filtro guardado Vencidos']").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarFiltroGuardadoCommand>().Should().BeEmpty("pulsar el aspa solo pide confirmación");
        var dialogo = cut.Find("[role=dialog]");
        dialogo.QuerySelector("h2")!.TextContent.Should().Be("¿Borrar el filtro guardado «Vencidos»?");
        dialogo.TextContent.Should().Contain("No borra ningún documento");

        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Borrar filtro").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarFiltroGuardadoCommand>().Should().Equal([new EliminarFiltroGuardadoCommand(filtro.Id)]);
        cut.WaitForAssertion(() => cut.FindAll("[role=dialog]").Should().BeEmpty());
        cut.FindAll("[aria-label='Borrar filtro guardado Vencidos']").Should().BeEmpty();
    }

    private static Result BorrarDe(List<FiltroGuardadoDto> guardados, Guid id)
    {
        guardados.RemoveAll(f => f.Id == id);
        return Result.Exito();
    }

    // ---------------------------------------------------------------- arnés

    private (IRenderedComponent<PaginaDocumentos> Cut, MediadorControlado Mediador) Renderizar(
        MediadorControlado? mediador = null, string url = "documentos", string rol = "Administrador",
        Guid? tenantSeleccionado = null)
    {
        mediador ??= new MediadorControlado();
        Services.AddScoped<ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba(tenantSeleccionado));

        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddLocalization();
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService>(_ => new UsuarioActualFalso(rol));
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton<ILogger<PaginaDocumentos>>(_ => NullLogger<PaginaDocumentos>.Instance);
        Services.AddScoped<AuthenticationStateProvider>(_ => new AutenticacionFalsa(rol));
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
        cut.FindAll(selector).Single(b => (b.GetAttribute("aria-label") ?? b.TextContent.Trim()) == texto);

    /// <summary>Activa los checkboxes de fila y marca las <paramref name="cuantas"/> primeras.</summary>
    private static async Task SeleccionarFilas(IRenderedComponent<PaginaDocumentos> cut, int cuantas)
    {
        await BotonPorTexto(cut, ".cabecera-pagina button[aria-label]", "Selección múltiple").ClickAsync(new MouseEventArgs());

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
            await BotonPorTexto(cut, ".cabecera-pagina button[aria-label]", "Selección múltiple").ClickAsync(new MouseEventArgs());

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
    public void La_cabecera_lleva_titulo_y_contador_medido_sin_antetitulo()
    {
        var (cut, mediador) = Renderizar(ConDocumentos(Documento("Contrato de cabecera")));
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty();
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)").Should().ContainSingle();
        var cabecera = cut.Find(".cabecera-pagina");
        cabecera.QuerySelectorAll(".cabecera-pagina-kicker").Should().BeEmpty();
        cabecera.QuerySelector("h1.titulo-pagina")!.TextContent.Trim().Should().Be("Documentos");
        cabecera.QuerySelector(".cabecera-listado-contador")!.TextContent.Trim().Should().Be("1");
    }

    /// <summary>
    /// D-27: el filtro Ámbito rotula al Cliente empresarial con su nombre completo
    /// (CONTRATO_TERMINOLOGIA § 3.2), no «Cliente» a secas.
    /// </summary>
    [Fact]
    public async Task El_filtro_Ambito_y_su_chip_rotulan_Cliente_empresarial_y_no_Cliente_a_secas()
    {
        var (cut, mediador) = Renderizar();
        await PastillaDocumentoFase1(cut, "Ámbito").ClickAsync(new MouseEventArgs());
        var opcion = cut.FindAll(".barra-filtros-pastillas [role=menuitemradio]")
            .Single(b => b.TextContent.Trim() == "Cliente empresarial");
        await opcion.ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ámbito: Cliente empresarial",
            "el chip del filtro activo no puede devolver Cliente a secas"));
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Last().Ambito.Should().Be(AmbitoAplicacion.Cliente);
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("Ambito=Cliente");
    }

    /// <summary>
    /// Las acciones del mockup siguen en la cabecera, y con ellas «Exportar a
    /// Excel», que el mockup no dibuja: un mockup omite comportamiento, no lo
    /// deroga. La barrera va delante para que la aserción no sea verde vacío.
    /// </summary>
    [Fact]
    public void Las_acciones_de_cabecera_conservan_la_exportacion_que_el_mockup_no_dibuja()
    {
        var (cut, mediador) = Renderizar(ConDocumentos(Documento("Contrato visible")));
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty();
        var acciones = cut.Find(".cabecera-pagina .acciones-cabecera");
        acciones.TextContent.Should().Contain("+ Nuevo documento");
        acciones.TextContent.Should().NotContain("Subida múltiple");
        cut.Find(".cabecera-pagina .menu-acciones-disparador").Click();
        cut.Find(".cabecera-pagina a[href='/documentos/exportar.xlsx']").TextContent.Should().Contain("Exportar a Excel");
        cut.FindAll(".cabecera-pagina .menu-acciones-item").Select(i => i.TextContent.Trim())
            .Should().Contain("Importar documentos").And.Contain("Subida múltiple");
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
    /// D-30: en la pestaña Plantillas había dos primarias a la vez («+ Nuevo documento» de la
    /// cabecera y «+ Nueva plantilla» de la pestaña). La primaria de Plantillas es la suya (mockup
    /// «Plantillas TALVEG»); «+ Nuevo documento» baja a secundaria, y en el resto de pestañas sigue
    /// siendo la primaria.
    /// </summary>
    [Fact]
    public void En_Plantillas_la_primaria_es_Nueva_plantilla_y_Nuevo_documento_baja_a_secundaria()
    {
        var (cut, _) = Renderizar(url: "documentos?pestana=plantillas");

        Pestana(cut, "Plantillas").GetAttribute("aria-selected").Should().Be("true", "barrera: la pestaña activa es la que se mide");
        var nuevoDocumento = cut.Find(".cabecera-pagina .acciones-cabecera").QuerySelectorAll("button")
            .Single(b => b.TextContent.Trim() == "+ Nuevo documento");
        nuevoDocumento.ClassList.Should().Contain("boton-secundario").And.NotContain("boton-primario");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "+ Nueva plantilla")
            .ClassList.Should().Contain("boton-primario");
        cut.FindAll("button.boton-primario").Should().ContainSingle(b => b.TextContent.Trim() == "+ Nueva plantilla",
            "una sola primaria visible por vista");
    }

    [Fact]
    public void Fuera_de_Plantillas_Nuevo_documento_sigue_siendo_la_primaria()
    {
        var (cut, _) = Renderizar();

        cut.Find(".cabecera-pagina .acciones-cabecera").QuerySelectorAll("button")
            .Single(b => b.TextContent.Trim() == "+ Nuevo documento")
            .ClassList.Should().Contain("boton-primario");
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

    [Fact]
    public void La_fecha_copiable_recibe_el_vencimiento_y_no_la_emision()
    {
        // Sin FechaEmision: fuera de Detección/Revisión IA solo la vigencia es
        // copiable (P9, 2026-09-18) — la emisión ya tiene su propia columna,
        // sin Alt+clic (hallazgo corregido, Project-Hydra-Negocio/tecnico/CAPA-USUARIO-AVANZADO-TALVEG.md
        // § 6.1 quinquies).
        var documento = Documento("Reconocimiento médico");
        var (cut, _) = Renderizar(ConDocumentos(documento));
        var fecha = cut.FindComponent<TextoFechaCopiable>().Instance;
        fecha.Fecha.Should().Be(documento.FechaVencimiento);
        fecha.FechaEmision.Should().BeNull();
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

        var filtroVencido = ElegirPastillaDocumentoFase1(cut, "Estado", "Vencido");
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Last().Estado.Should().Be(EstadoDocumento.Vencido));
        var filtroUrgente = ElegirPastillaDocumentoFase1(cut, "Estado", "Urgente");
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Last().Estado.Should().Be(EstadoDocumento.Urgente));

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
        var recarga = ElegirPastillaDocumentoFase1(cut, "Estado", "Vencido");
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Last().Estado.Should().Be(EstadoDocumento.Vencido));

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

        await ElegirPastillaDocumentoFase1(cut, "Estado", "Vencido");

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

        // «Guardar filtro» vive dentro de Más filtros: solo con un filtro activo.
        var (cut, _) = Renderizar(mediador, url: "documentos?Estado=Vencido");

        await GuardarFiltroDocumentoFase1(cut);
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

    // ------------------------------------------- «Guardar filtro» con ModalFormulario (S12, lote 3b)

    private async Task<(IRenderedComponent<PaginaDocumentos> Cut, MediadorControlado Mediador)> AbrirGuardarFiltroAsync(Result<Guid>? respuesta = null)
    {
        var mediador = ConDocumentos(Documento("Reconocimiento médico"));
        if (respuesta is { } r)
            mediador.Interceptar = p => p is GuardarFiltroCommand ? Task.FromResult<object?>(r) : null;

        var (cut, _) = Renderizar(mediador, url: "documentos?Estado=Vencido");
        await GuardarFiltroDocumentoFase1(cut);
        return (cut, mediador);
    }

    private static Task EscribirNombreDelFiltroAsync(IRenderedComponent<PaginaDocumentos> cut, string nombre)
    {
        // El campo rebota 300 ms sobre oninput: se invoca su callback (ver Dos_envios_de_Guardar_filtro_mandan_un_solo_comando).
        var campoNombre = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre");
        return cut.InvokeAsync(() => campoNombre.Instance.ValorChanged.InvokeAsync(nombre));
    }

    [Fact]
    public async Task Guardar_filtro_sin_nombre_esta_deshabilitado_y_dice_por_que_y_con_nombre_se_habilita()
    {
        var (cut, _) = await AbrirGuardarFiltroAsync();
        IElement Guardar() => cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Guardar");

        Guardar().HasAttribute("disabled").Should().BeTrue("sin nombre no hay nada que guardar");
        Guardar().GetAttribute("title").Should().Be("Escribe un nombre para el filtro", "un primario deshabilitado sin motivo es el de D-02 y D-06");

        await EscribirNombreDelFiltroAsync(cut, "Vencidos que bloquean centro");

        Guardar().HasAttribute("disabled").Should().BeFalse();
        Guardar().HasAttribute("title").Should().BeFalse("habilitado no hay motivo que decir");
    }

    [Fact]
    public async Task Un_rechazo_al_guardar_el_filtro_se_ve_en_el_aviso_fijo_del_modal_y_lo_escrito_no_se_pierde()
    {
        var (cut, _) = await AbrirGuardarFiltroAsync(Result.Fallo<Guid>(Error.Crear("Filtro.NombreEnUso", "Ya tienes un filtro con ese nombre.")));
        await EscribirNombreDelFiltroAsync(cut, "Vencidos que bloquean centro");

        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        cut.Find(".modal-aviso .alerta-formulario").TextContent.Should().Contain("Ya tienes un filtro con ese nombre.",
            "el mensaje del Result es el que el usuario ve, fuera del cuerpo desplazable y no en un toast que desaparece");
        cut.FindAll(".modal-cuerpo .alerta-formulario").Should().BeEmpty("el aviso va fuera del cuerpo desplazable (D-20)");
        cut.FindAll("[role=dialog]").Should().NotBeEmpty("el rechazo no cierra el modal ni tira lo escrito");

        await EscribirNombreDelFiltroAsync(cut, "Vencidos 2");

        cut.FindAll(".modal-aviso").Should().BeEmpty("escribir de nuevo retira el error anterior");
    }

    [Fact]
    public async Task Cancelar_el_filtro_con_el_nombre_escrito_pregunta_como_la_X()
    {
        var (cut, _) = await AbrirGuardarFiltroAsync();

        await EscribirNombreDelFiltroAsync(cut, "Vencidos que bloquean centro");
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?", "con el nombre escrito «Cancelar» pregunta, como la X (D-05)");
    }

    [Fact]
    public async Task Cancelar_el_filtro_sin_nombre_cierra_sin_preguntar()
    {
        var (cut, _) = await AbrirGuardarFiltroAsync();

        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        cut.FindAll("h2").Should().NotContain(h => h.TextContent.Trim() == "¿Descartar cambios?", "sin nombre no hay nada que perder");
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    /// <summary>
    /// Guardar un filtro es autoservicio: <c>GuardarFiltroCommand</c> lleva
    /// <c>IComandoDeAutoservicio</c> y el servidor lo deja pasar a Consulta, así
    /// que el botón no se esconde a quien solo lee. «+ Nuevo documento», que sí
    /// escribe en el Tenant, sigue sin pintarse: es el control de que el render
    /// corre de verdad con el rol Consulta.
    /// </summary>
    [Fact]
    public void Consulta_ve_Guardar_filtro_porque_guardar_sus_filtros_es_autoservicio()
    {
        var (cut, mediador) = Renderizar(ConDocumentos(Documento("Reconocimiento médico")), url: "documentos?Estado=Vencido", rol: "Consulta");
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Last().Estado.Should().Be(EstadoDocumento.Vencido);
        PastillaDocumentoFase1(cut, "Más filtros").Click();
        var guardar = cut.FindAll(".barra-filtros-pastillas [role=menuitem]").Single(b => b.TextContent.Trim() == "Guardar filtro");
        guardar.GetAttribute("disabled").Should().BeNull();
        guardar.GetAttribute("aria-disabled").Should().NotBe("true");
        cut.Markup.Should().NotContain("+ Nuevo documento", "Consulta no crea documentos del Tenant propietario");
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

    /// <summary>
    /// El Workspace no es modal: con la ficha del documento abierta, la baja se
    /// confirma desde la fila que queda detrás. La ficha ya no tiene baja propia
    /// (P41b), así que la lista es quien la retira.
    /// </summary>
    [Fact]
    public async Task Eliminar_el_documento_cuya_ficha_esta_abierta_retira_la_ficha()
    {
        var documento = Documento("Reconocimiento médico");
        var (cut, mediador) = Renderizar(ConDocumentos(documento));
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Documento, documento.Id, "Reconocimiento médico", "informacion"));
        workspace.EstaAbierto.Should().BeTrue("control positivo: la ficha estaba abierta");

        // Hay dos menús: «Más» de la cabecera y el «⋯» de la fila.
        await cut.Find("table .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await BotonPorTexto(cut, "tbody .menu-acciones-item", "Eliminar").ClickAsync(new MouseEventArgs());
        await ConfirmarDialogo(cut);

        mediador.Enviadas.OfType<EliminarDocumentoCommand>().Should().ContainSingle("la baja se ejecutó");
        workspace.EstaAbierto.Should().BeFalse("una ficha abierta de un documento ya dado de baja no puede seguir editable");
    }

    /// <summary>
    /// Baja en lote. El DTO del lote no dice qué documentos cayeron, así que la
    /// lista retira las fichas de TODO lo pedido en cuanto cayó alguno (decisión:
    /// pasarse de retirar antes que dejar abierta una ficha muerta). Y no toca la
    /// ficha de un documento que no iba en el lote.
    /// </summary>
    [Theory]
    [InlineData(true, false)]  // iba en el lote, lote completo
    [InlineData(true, true)]   // iba en el lote, lote parcial: se retira igualmente
    [InlineData(false, false)] // no iba en el lote: se queda
    public async Task Eliminar_en_lote_retira_la_ficha_abierta_de_lo_pedido_y_solo_de_lo_pedido(bool ibaEnElLote, bool parcial)
    {
        var d1 = Documento("Reconocimiento médico");
        var d2 = Documento("Formación PRL");
        var d3 = Documento("Certificado TGSS");
        var mediador = ConDocumentos(d1, d2, d3);
        mediador.AlEliminarLote = c => Result.Exito(parcial
            ? new ResultadoEliminacionLoteDto(1, ["Uno de los documentos no pudo eliminarse."])
            : new ResultadoEliminacionLoteDto(c.Ids.Count, []));
        var (cut, _) = Renderizar(mediador);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        var abierto = ibaEnElLote ? d1 : d3;
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Documento, abierto.Id, "Ficha abierta", "informacion"));
        workspace.EstaAbierto.Should().BeTrue("control positivo: la ficha estaba abierta");

        await SeleccionarFilas(cut, 2);
        await AbrirConfirmacionDeLote(cut);
        await ConfirmarDialogo(cut);

        mediador.Enviadas.OfType<EliminarDocumentosCommand>().Single().Ids.Should().BeEquivalentTo([d1.Id, d2.Id],
            "el caso solo vale si el lote pidió esos dos y ninguno más");
        workspace.EstaAbierto.Should().Be(!ibaEnElLote);
    }

    /// <summary>La guarda de la retirada: un lote que no eliminó NADA no toca la ficha abierta de un documento que iba en él.</summary>
    [Fact]
    public async Task Un_lote_que_no_elimina_nada_no_retira_la_ficha_abierta()
    {
        var d1 = Documento("Reconocimiento médico");
        var mediador = ConDocumentos(d1, Documento("Formación PRL"), Documento("Certificado TGSS"));
        mediador.AlEliminarLote = _ => Result.Exito(new ResultadoEliminacionLoteDto(0, ["Ninguno pudo eliminarse."]));
        var (cut, _) = Renderizar(mediador);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Documento, d1.Id, "Reconocimiento médico", "informacion"));

        await SeleccionarFilas(cut, 2);
        await AbrirConfirmacionDeLote(cut);
        await ConfirmarDialogo(cut);

        mediador.Enviadas.OfType<EliminarDocumentosCommand>().Single().Ids.Should().Contain(d1.Id, "el caso solo vale si el lote pidió ese documento");
        workspace.EstaAbierto.Should().BeTrue("no cayó nada: no hay nada muerto que retirar");
    }

    /// <summary>
    /// FS-09 (auditoría UX de flujos sin salida, 2026-09-24): el diálogo prometía
    /// «Podrás recuperarlos desde Auditoría», pantalla que solo abre el Administrador
    /// del Tenant. El aviso del lote ofrece ahora «Deshacer», que restaura solo lo que
    /// el lote eliminó, y el diálogo dice quién puede recuperarlos después.
    /// </summary>
    [Fact]
    public async Task Eliminar_en_lote_ofrece_deshacer_que_restaura_solo_los_documentos_eliminados()
    {
        var elegido = Documento("Reconocimiento médico");
        var superviviente = Documento("Formación PRL");
        var mediador = ConDocumentos(elegido, superviviente);
        mediador.AlEliminarLote = _ => Result.Exito(new ResultadoEliminacionLoteDto(1, ["Un documento ya no existía."], [elegido.Id]));
        var (cut, _) = Renderizar(mediador);

        await SeleccionarFilas(cut, 2);
        await AbrirConfirmacionDeLote(cut);
        cut.Find("[role=dialog]").TextContent.Should().Contain(
            "Podrás deshacer la eliminación desde el aviso que aparecerá; después, solo un Administrador de esta organización puede recuperarlos desde Auditoría.");
        await ConfirmarDialogo(cut);

        var aviso = Toasts().Single(t => t.TextoAccion == "Deshacer");
        await cut.InvokeAsync(aviso.OnAccion!);

        mediador.Enviadas.OfType<EliminarDocumentosCommand>().Single().Ids.Should().BeEquivalentTo([elegido.Id, superviviente.Id],
            "el caso solo vale si el superviviente iba en el lote");
        mediador.Enviadas.OfType<RestaurarDocumentoCommand>().Select(c => c.Id).Should().Equal([elegido.Id],
            "se restaura solo lo que el lote eliminó, no lo que pidió");
        Toasts().Should().Contain(t => t.Mensaje == "1 documento(s) restaurado(s)." && t.Tono == TonoToast.Exito);
    }

    // ------------------------------------------------- lote 3 del selector: empresa gestionada activa

    private static readonly Guid Origen = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid EmpresaNorte = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid EmpresaSur = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    /// <summary>Un Operador CAE externo con dos Tenants beneficiarios; el origen no está gestionado por Operación.</summary>
    private static MediadorControlado ConCartera(bool origenGestionado, params DocumentoListaDto[] documentos)
    {
        var mediador = ConDocumentos(documentos);
        mediador.Autorizados.Clear();
        mediador.Autorizados.AddRange(
        [
            new ClienteAutorizadoDto(Origen, "Operador de prueba", EsOrigen: true, EsGestionadoPorOperacion: origenGestionado),
            new ClienteAutorizadoDto(EmpresaNorte, "Empresa Norte", EsOrigen: false, EsGestionadoPorOperacion: true),
            new ClienteAutorizadoDto(EmpresaSur, "Empresa Sur", EsOrigen: false, EsGestionadoPorOperacion: true),
        ]);
        return mediador;
    }

    [Fact]
    public void Con_Tenant_seleccionado_la_lista_se_monta_sin_repetir_su_cabecera()
    {
        var (cut, mediador) = Renderizar(ConCartera(origenGestionado: false, Documento("Reconocimiento médico")), tenantSeleccionado: EmpresaSur);
        Services.GetRequiredService<ITenantActual>().TenantId.Should().Be(EmpresaSur);
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty();
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)").Should().ContainSingle();
        cut.FindAll(".barra-filtros-pastillas").Should().ContainSingle();
        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();
    }

    [Fact]
    public void Un_usuario_mono_Tenant_no_ve_cabecera_de_empresa_gestionada()
    {
        var (cut, _) = Renderizar(ConDocumentos(Documento("Reconocimiento médico")));

        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();
        cut.FindAll("table, [role=grid]").Should().NotBeEmpty("la lista se pinta como siempre");
    }

    [Fact]
    public void Sin_empresa_elegida_y_con_el_origen_sin_gestionar_pide_elegir_y_no_muestra_datos_del_origen()
    {
        var (cut, mediador) = Renderizar(ConCartera(origenGestionado: false, Documento("Reconocimiento médico")));

        cut.Markup.Should().Contain("Selecciona una empresa de tu cartera");
        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty("el origen no es la empresa elegida: la cabecera no lo enseña como tal");
        cut.FindAll(".barra-filtros-pastillas").Should().BeEmpty();
        cut.FindAll(".cabecera-pagina a[href='/documentos/exportar.xlsx']").Should().BeEmpty("exportaría los datos del origen");
        cut.Markup.Should().NotContain("+ Nuevo documento");
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().BeEmpty("no se piden los documentos de la organización de origen");
        mediador.Enviadas.OfType<ObtenerFiltrosGuardadosQuery>().Should().BeEmpty("tampoco sus filtros guardados");
    }

    [Fact]
    public void Con_el_origen_gestionado_y_sin_empresa_elegida_la_lista_es_la_del_origen()
    {
        var (cut, mediador) = Renderizar(ConCartera(origenGestionado: true, Documento("Reconocimiento médico")));
        cut.Markup.Should().NotContain("Selecciona una empresa de tu cartera");
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty();
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)").Should().ContainSingle();
        cut.FindAll(".barra-filtros-pastillas").Should().ContainSingle();
        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();
    }

    [Fact]
    public void Mientras_se_resuelve_la_empresa_activa_no_se_monta_la_lista_ni_se_ofrece_la_exportacion()
    {
        var puerta = new TaskCompletionSource<object?>();
        var mediador = ConCartera(origenGestionado: false, Documento("Reconocimiento médico"));
        mediador.Interceptar = p => p is ObtenerClientesAutorizadosQuery ? puerta.Task : null;

        var (cut, _) = Renderizar(mediador, tenantSeleccionado: EmpresaSur);

        cut.FindAll(".barra-filtros-pastillas").Should().BeEmpty();
        cut.FindAll(".cabecera-pagina a[href='/documentos/exportar.xlsx']").Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().BeEmpty();

        puerta.SetResult((IReadOnlyList<ClienteAutorizadoDto>)mediador.Autorizados.ToList());

        cut.WaitForAssertion(() => cut.FindAll(".barra-filtros-pastillas").Should().NotBeEmpty());
        cut.Find(".cabecera-pagina .menu-acciones-disparador").Click();
        cut.FindAll(".cabecera-pagina a[href='/documentos/exportar.xlsx']").Should().ContainSingle();
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Retirar_la_pagina_mientras_se_resuelve_la_empresa_no_lanza_consultas_posteriores()
    {
        var puerta = new TaskCompletionSource<object?>();
        var mediador = ConCartera(origenGestionado: false, Documento("Reconocimiento médico"));
        mediador.Interceptar = p => p is ObtenerClientesAutorizadosQuery ? puerta.Task : null;
        var (cut, _) = Renderizar(mediador, tenantSeleccionado: EmpresaSur);
        mediador.Tokens.Should().ContainSingle("solo la resolución de la empresa está en vuelo");
        mediador.Tokens[0].CanBeCanceled.Should().BeTrue("un token no cancelable sigue consultando para una página que ya no existe");

        cut.Instance.Dispose();
        mediador.Tokens[0].IsCancellationRequested.Should().BeTrue();
        await cut.InvokeAsync(() => puerta.SetResult(mediador.Autorizados.ToList()));
        // Es una ausencia: se deja correr la continuación de la resolución antes de mirar que no pidió nada.
        await Task.Delay(200);

        mediador.Enviadas.Select(r => r.GetType().Name).Should().OnlyContain(n => n == nameof(ObtenerClientesAutorizadosQuery),
            "la página desmontada no pide filtros guardados ni documentos");
    }

    [Fact]
    public void Un_enlace_profundo_no_abre_el_drawer_mientras_hay_que_elegir_empresa()
    {
        var mediador = ConCartera(origenGestionado: false);

        var (cut, _) = Renderizar(mediador, url: "documentos?accion=crear");

        cut.Markup.Should().Contain("Selecciona una empresa de tu cartera");
        mediador.Enviadas.Select(r => r.GetType().Name).Should().OnlyContain(
            n => n == nameof(ObtenerClientesAutorizadosQuery),
            "el drawer de alta escribiría en el Tenant de origen y solo la consulta de la cartera es legítima aquí");
    }

    [Fact]
    public void La_lista_nunca_pide_consultas_agregadas_entre_Tenants()
    {
        // I5: «Todos» existe solo en Mi trabajo y Dashboard; una lista trabaja sobre un Tenant.
        var (cut, mediador) = Renderizar(ConCartera(origenGestionado: false, Documento("Reconocimiento médico")), tenantSeleccionado: EmpresaNorte);

        cut.FindAll("table, [role=grid]").Should().NotBeEmpty("control positivo: la lista se pintó");
        mediador.Enviadas.Select(r => r.GetType().Namespace ?? "").Should().NotContain(
            n => n.Contains(".Dashboard") || n.Contains(".MiTrabajo"));
    }

    // ------------------------------------------------- patrón único de lista

    [Fact]
    public void Los_filtros_activos_salen_como_chips_y_cada_chip_quita_su_filtro_de_la_url()
    {
        var (cut, _) = Renderizar(ConDocumentos(Documento("Reconocimiento médico")),
            url: "documentos?q=medico&Ambito=Trabajador&Estado=Vencido");

        var chips = cut.FindAll(".barra-filtros-pastillas .chip-filtro").Select(c => c.TextContent.Trim()).ToList();
        chips.Should().HaveCount(3);
        chips.Should().Contain(c => c.Contains("medico")).And.Contain(c => c.Contains("Trabajador")).And.Contain(c => c.Contains("Vencido"));

        cut.FindAll(".barra-filtros-pastillas .chip-filtro")
            .Single(c => c.TextContent.Contains("Vencido")).QuerySelector(".chip-filtro-quitar")!.Click();

        var url = Services.GetRequiredService<NavigationManager>().Uri;
        url.Should().NotContain("Estado=").And.Contain("Ambito=Trabajador").And.Contain("q=medico");
        cut.FindAll(".barra-filtros-pastillas .chip-filtro").Should().HaveCount(2);
    }

    [Fact]
    public void Limpiar_todo_quita_los_tres_filtros_de_la_url()
    {
        var (cut, _) = Renderizar(ConDocumentos(Documento("Reconocimiento médico")),
            url: "documentos?q=medico&Ambito=Trabajador&Estado=Vencido");

        cut.Find(".limpiar-filtros-barra").Click();

        var url = Services.GetRequiredService<NavigationManager>().Uri;
        url.Should().NotContain("q=").And.NotContain("Estado=").And.NotContain("Ambito=");
        cut.FindAll(".barra-filtros-pastillas .chip-filtro").Should().BeEmpty();
    }

    [Fact]
    public void Las_pastillas_llevan_su_etiqueta_accesible()
    {
        var (cut, mediador) = Renderizar(ConDocumentos(Documento("Reconocimiento médico")));
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty();
        PastillaDocumentoFase1(cut, "Ámbito").GetAttribute("aria-label").Should().Be("Ámbito");
        PastillaDocumentoFase1(cut, "Estado").GetAttribute("aria-label").Should().Be("Estado");
    }

    [Fact]
    public void Las_pestanas_se_quedan_entre_la_cabecera_y_la_barra_de_filtros()
    {
        var (cut, _) = Renderizar(ConDocumentos(Documento("Reconocimiento médico")));

        var html = cut.Markup;
        var cabecera = html.IndexOf("cabecera-pagina", StringComparison.Ordinal);
        var pestanas = html.IndexOf("role=\"tablist\"", StringComparison.Ordinal);
        var filtros = html.IndexOf("barra-filtros-pastillas", StringComparison.Ordinal);
        cabecera.Should().BeGreaterThanOrEqualTo(0);
        pestanas.Should().BeGreaterThan(cabecera);
        filtros.Should().BeGreaterThan(pestanas);
    }

    [Fact]
    public void Nunca_hay_acciones_de_la_pagina_dentro_de_la_barra_de_filtros()
    {
        var (cut, mediador) = Renderizar(ConDocumentos(Documento("Reconocimiento médico")));
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty();
        cut.FindAll(".barra-filtros-pastillas a").Should().BeEmpty();
        cut.Find(".cabecera-pagina .menu-acciones-disparador").Click();
        cut.FindAll(".cabecera-pagina a[href='/documentos/exportar.xlsx']").Should().ContainSingle();
        cut.FindAll(".barra-filtros-pastillas a").Should().BeEmpty();
    }

    [Fact]
    public void El_paginador_ofrece_Mostrar_N_y_cambiarlo_vuelve_a_pedir_con_ese_tamano()
    {
        var mediador = ConDocumentos(Enumerable.Range(1, 21).Select(i => Documento("Contrato " + i)).ToArray());
        var (cut, _) = Renderizar(mediador);
        var consultasAntes = mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count();
        cut.Find(".paginador-tamano-select").Change("50");
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Last().TamanoPagina.Should().Be(50);
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().HaveCount(consultasAntes + 1);
    }

    [Fact]
    public async Task El_propietario_de_la_fila_abre_el_panel_del_documento_y_no_un_segundo_drawer()
    {
        // Pieza 6: en Documentos la vista previa ES el panel del Context Workspace, que ya existe; el
        // contrato prohíbe sumarle un PreviewDrawer.
        var documento = Documento("Reconocimiento médico");
        var (cut, _) = Renderizar(ConDocumentos(documento));
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        workspace.EstaAbierto.Should().BeFalse("control: cerrado al inicio");
        await cut.Find("table .nombre-fila-entidad").ClickAsync(new MouseEventArgs());

        workspace.EstaAbierto.Should().BeTrue("la entidad asociada abre el panel del documento");
        cut.FindAll("[class*='drawer-preview']").Should().BeEmpty("no hay un segundo drawer de vista previa");
    }

    [Fact]
    public async Task El_menu_de_fila_conserva_Ver_Renovar_y_Eliminar_con_el_destructivo_al_final()
    {
        var (cut, _) = Renderizar(ConDocumentos(Documento("Reconocimiento médico")));

        await cut.Find("table .menu-acciones-disparador").ClickAsync(new MouseEventArgs());

        var items = cut.FindAll("tbody .menu-acciones-item").Select(i => i.TextContent.Trim()).ToList();
        items.Should().Equal("Ver", "Renovar", "Eliminar");
    }

    [Fact]
    public void El_recuento_de_la_barra_de_herramientas_dice_cuantos_documentos_coinciden()
    {
        var (cut, _) = Renderizar(ConDocumentos(Documento("Reconocimiento médico"), Documento("Formación PRL")),
            url: "documentos?Estado=Vencido");

        cut.Find(".cabecera-pagina .cabecera-listado-contador").TextContent.Trim().Should().Be("2");
    }

    // QuickGrid: eventos nativos de opciones/cabecera; las aserciones decisivas observan ObtenerDocumentosQuery.
    public static IEnumerable<object[]> CamposOrdenDocumentoFase1()
    {
        foreach (var campo in new[] { "PropietarioNombre", "Ambito", "FechaEmision", "FechaVencimiento" })
            foreach (var misma in new[] { false, true })
                foreach (var descendente in new[] { false, true })
                    yield return [campo, misma, descendente];
    }

    [Theory]
    [MemberData(nameof(CamposOrdenDocumentoFase1))]
    public async Task Opciones_nativas_envian_el_campo_real_y_el_sentido_documental(
        string campo, bool mismaColumna, bool descendente)
    {
        var (cut, m) = Renderizar(ConDocumentos(Documento("Contrato de prueba")));
        var entidad = campo is "PropietarioNombre" or "Ambito";
        var titulo = entidad ? "Entidad asociada" : "Vigencia";
        var previo = campo switch
        {
            "PropietarioNombre" => "Ambito",
            "Ambito" => "PropietarioNombre",
            "FechaEmision" => "FechaVencimiento",
            _ => "FechaEmision"
        };
        await ElegirOrdenDocumentoFase1(cut, previo);
        await FijarSentidoDocumentoFase1(cut, m, mismaColumna ? titulo : "Tipo de documento", descendente);
        UltimoOrdenDocumentoFase1(m).OrdenarPor.Should().Be(mismaColumna ? previo : "TipoDocumentoNombre");
        UltimoOrdenDocumentoFase1(m).Descendente.Should().Be(descendente);
        var antes = m.Enviadas.OfType<ObtenerDocumentosQuery>().Count();

        await ElegirOrdenDocumentoFase1(cut, campo);

        cut.WaitForAssertion(() =>
        {
            m.Enviadas.OfType<ObtenerDocumentosQuery>().Should().HaveCount(antes + 1);
            UltimoOrdenDocumentoFase1(m).OrdenarPor.Should().Be(campo);
            UltimoOrdenDocumentoFase1(m).Descendente.Should().Be(mismaColumna && descendente);
        });
    }

    [Fact]
    public async Task Entidad_ambito_y_vigencia_conservan_datos_tinte_y_foco()
    {
        var vencido = Documento("Contrato vencido") with { Ambito = AmbitoAplicacion.Cliente, Estado = EstadoDocumento.Vencido };
        var urgente = Documento("Contrato urgente") with { Estado = EstadoDocumento.Urgente };
        var sinVence = Documento("Contrato sin vencimiento") with { FechaVencimiento = null };
        var (cut, m) = Renderizar(ConDocumentos(vencido, urgente, sinVence));
        m.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty();
        var filas = cut.FindAll("tbody tr:has(button.enlace-nombre-fila)");
        filas.Should().HaveCount(3);
        cut.Find(".cabecera-pagina .cabecera-listado-contador").TextContent.Trim().Should().Be("3");
        var celda = filas[0].QuerySelectorAll("td").Single(td => td.TextContent.Contains(vencido.PropietarioNombre));
        celda.TextContent.Should().Contain("Cliente empresarial");
        filas[0].TextContent.Should().Contain(vencido.FechaEmision.ToString("dd/MM/yyyy"))
            .And.Contain(vencido.FechaVencimiento!.Value.ToString("dd/MM/yyyy"));
        filas[2].TextContent.Should().Contain("Emitido " + sinVence.FechaEmision.ToString("dd/MM/yyyy"));
        cut.FindComponents<TextoFechaCopiable>().Should().HaveCount(2);
        cut.FindComponents<TextoFechaCopiable>().Select(c => c.Instance.Fecha).Should()
            .Equal(vencido.FechaVencimiento, urgente.FechaVencimiento);
        filas[0].ClassList.Should().Contain("fila-tintada-peligro");
        filas[1].ClassList.Should().Contain("fila-tintada-aviso");
        filas[2].ClassList.Should().NotContain("fila-tintada-peligro").And.NotContain("fila-tintada-aviso");
        await cut.InvokeAsync(() => cut.FindComponent<AtajosListaTeclado>().Instance.OnAtajo.InvokeAsync("j"));
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)")[0].ClassList.Should().Contain("fila-enfocada").And.Contain("fila-tintada-peligro");
        await cut.InvokeAsync(() => cut.FindComponent<AtajosListaTeclado>().Instance.OnAtajo.InvokeAsync("j"));
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)")[0].ClassList.Should().NotContain("fila-enfocada").And.Contain("fila-tintada-peligro");
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)")[1].ClassList.Should().Contain("fila-enfocada").And.Contain("fila-tintada-aviso");
    }

    [Fact]
    public async Task Retirar_documentos_durante_orden_pendiente_cancela_y_no_reconsulta()
    {
        var (cut, m) = Renderizar(ConDocumentos(Documento("Contrato de prueba")));
        var respuesta = m.Pagina(UltimoOrdenDocumentoFase1(m));
        var retenida = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Interceptar = x => x is ObtenerDocumentosQuery { OrdenarPor: "Ambito" } ? retenida.Task : null;
        Task? cambiar = null;
        var huboError = false;
        var consultas = 0;
        try
        {
            cambiar = ElegirOrdenDocumentoFase1(cut, "Ambito");
            cut.WaitForAssertion(() => UltimoOrdenDocumentoFase1(m).OrdenarPor.Should().Be("Ambito"));
            var indice = m.Enviadas.FindLastIndex(x => x is ObtenerDocumentosQuery);
            var token = m.Tokens[indice];
            token.CanBeCanceled.Should().BeTrue();
            token.IsCancellationRequested.Should().BeFalse();
            consultas = m.Enviadas.OfType<ObtenerDocumentosQuery>().Count();
            await DisposeComponentsAsync();
            token.IsCancellationRequested.Should().BeTrue();
        }
        catch
        {
            huboError = true;
            throw;
        }
        finally
        {
            retenida.TrySetResult(respuesta);
            if (cambiar is not null)
            {
                try { await cambiar; }
                catch when (huboError) { /* Conserva el error original si la liberación también falla. */ }
            }
        }
        m.Enviadas.OfType<ObtenerDocumentosQuery>().Should().HaveCount(consultas);
        // Observa cancelación/actividad; no atribuye cobertura a la guarda de HideColumnOptionsAsync.
    }

    private static ObtenerDocumentosQuery UltimoOrdenDocumentoFase1(MediadorControlado m) =>
        m.Enviadas.OfType<ObtenerDocumentosQuery>().Last();

    private static IElement CabeceraDocumentoFase1(IRenderedComponent<PaginaDocumentos> cut, string titulo) =>
        cut.FindAll("thead th").Single(th => th.TextContent.Trim().StartsWith(titulo, StringComparison.Ordinal));

    private static async Task FijarSentidoDocumentoFase1(
        IRenderedComponent<PaginaDocumentos> cut, MediadorControlado m, string titulo, bool descendente)
    {
        await CabeceraDocumentoFase1(cut, titulo).QuerySelector("button.col-title")!.ClickAsync(new MouseEventArgs());
        if (UltimoOrdenDocumentoFase1(m).Descendente != descendente)
            await CabeceraDocumentoFase1(cut, titulo).QuerySelector("button.col-title")!.ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => UltimoOrdenDocumentoFase1(m).Descendente.Should().Be(descendente));
    }

    private static async Task ElegirOrdenDocumentoFase1(IRenderedComponent<PaginaDocumentos> cut, string campo)
    {
        var titulo = campo is "PropietarioNombre" or "Ambito" ? "Entidad asociada" : "Vigencia";
        var texto = campo switch
        {
            "PropietarioNombre" => "Ordenar por entidad asociada",
            "Ambito" => "Ordenar por ámbito",
            "FechaEmision" => "Ordenar por emisión",
            _ => "Ordenar por vencimiento"
        };
        await CabeceraDocumentoFase1(cut, titulo).QuerySelector("button.col-options-button")!.ClickAsync(new MouseEventArgs());
        await cut.FindAll(".documentos-opciones-orden button").Single(b => b.TextContent.Trim() == texto)
            .ClickAsync(new MouseEventArgs());
    }


    // Helpers de adaptación: insertar dentro de DocumentosGen2Tests.
    private static IElement PastillaDocumentoFase1(IRenderedComponent<PaginaDocumentos> cut, string etiqueta) =>
        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla")
            .Single(b => b.GetAttribute("aria-label") is { } nombre &&
                (nombre == etiqueta || nombre.StartsWith(etiqueta + ": ", StringComparison.Ordinal)));

    private static async Task ElegirPastillaDocumentoFase1(
        IRenderedComponent<PaginaDocumentos> cut, string etiqueta, string opcion)
    {
        var pastilla = PastillaDocumentoFase1(cut, etiqueta);
        if (pastilla.GetAttribute("aria-expanded") != "true")
            await pastilla.ClickAsync(new MouseEventArgs());
        var panelId = PastillaDocumentoFase1(cut, etiqueta).GetAttribute("aria-controls")!;
        await cut.Find("#" + panelId).QuerySelectorAll("[role=menuitemradio]")
            .Single(b => b.TextContent.Trim() == opcion).ClickAsync(new MouseEventArgs());
    }

    private static async Task GuardarFiltroDocumentoFase1(IRenderedComponent<PaginaDocumentos> cut)
    {
        await PastillaDocumentoFase1(cut, "Más filtros").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".barra-filtros-pastillas [role=menuitem]")
            .Single(b => b.TextContent.Trim() == "Guardar filtro").ClickAsync(new MouseEventArgs());
    }

    // Sustituye el test de exportación antiguo; verifica destino y permisos de lectura por fixture.
    [Fact]
    public async Task Consulta_conserva_exportacion_en_menu_sin_acciones_de_escritura()
    {
        var (cut, m) = Renderizar(ConDocumentos(Documento("Contrato visible")), rol: "Consulta");
        m.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty();
        await cut.Find(".cabecera-pagina .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        var cabecera = cut.Find(".cabecera-pagina");
        cabecera.QuerySelectorAll("a[href='/documentos/exportar.xlsx']").Should().ContainSingle();
        cabecera.TextContent.Should().NotContain("Importar documentos").And.NotContain("Subida múltiple");
        cabecera.QuerySelectorAll("button[aria-label='Selección múltiple']").Should().BeEmpty();
        cabecera.TextContent.Should().NotContain("Nuevo documento");
    }

}
