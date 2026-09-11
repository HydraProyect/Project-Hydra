using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

// Alias: el tipo de la página se llama igual que su espacio de nombres, y sin
// esto el compilador resuelve "Documentos" al namespace.
using PaginaDocumentos = CaeManager.Web.Features.Documentos.Pages.Documentos;

/// <summary>
/// Cierra por render el hueco declarado del trinquete para Documentos: tres
/// filtros —búsqueda, estado documental y ámbito—, los tres por URL.
///
/// <para>
/// <b>Y encontró un defecto que el trinquete daba por bueno.</b> «Quitar los
/// filtros» borraba <c>q</c> de la URL pero dejaba <c>Estado</c> y
/// <c>Ambito</c>, y <c>OnParametersSet</c> —que re-sincroniza desde la URL— los
/// devolvía en la siguiente pasada de parámetros: la lista seguía igual de
/// recortada después de pulsar. Es el mismo defecto de Clientes y de Visitas,
/// las tres encontradas al escribir estas pruebas. Un trinquete de fuente
/// comprueba que la rama EXISTE; que el botón haga lo que dice solo se ve
/// renderizando.
/// </para>
/// </summary>
public class DocumentosVacioPorFiltroTests : BunitContext
{
    /// <summary>La página monta AtajosListaTeclado, que importa ./js/atajos-lista.js.</summary>
    public DocumentosVacioPorFiltroTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorPorTipo : IMediator
    {
        public required IReadOnlyList<DocumentoListaDto> Documentos { get; init; }

        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerFiltrosGuardadosQuery => (object)Array.Empty<FiltroGuardadoDto>(),
                ObtenerDocumentosQuery q => new ResultadoPaginado<DocumentoListaDto>(
                    Documentos, Documentos.Count, q.Pagina, q.TamanoPagina),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));
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

    /// <summary>La página los inyecta para descargar y previsualizar; con la lista vacía nadie los toca.</summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Sin filas no se abre ningún archivo; si esto salta, la página cambió de camino.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Sin filas no se convierte nada; si esto salta, la página cambió de camino.");
    }

    /// <summary>
    /// Mismo montaje que <see cref="ClientesVacioPorFiltroTests"/>: la página
    /// lleva AuthorizeView, y bUnit registra un IAuthorizationService que lanza
    /// salvo que se use su propio helper. Este evalúa los roles de verdad — uno
    /// que autorizara siempre dejaría pasar lo que la página oculta por rol.
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

    private static DocumentoListaDto Documento(string tipoDocumento) => new(
        Guid.NewGuid(), AmbitoAplicacion.Trabajador, "Salas Moreno, Javier", tipoDocumento,
        new DateOnly(2026, 1, 15), new DateOnly(2027, 1, 15), EstadoDocumento.Vigente,
        ArchivoUrl: null, Acreditaciones: []);

    /// <param name="busqueda">Filtro de texto que llega por la URL (?q=).</param>
    /// <param name="estado">Filtro documental que llega por la URL (?Estado=).</param>
    /// <param name="ambito">Filtro de ámbito que llega por la URL (?Ambito=).</param>
    private IRenderedComponent<PaginaDocumentos> Renderizar(string? busqueda = null, string? estado = null,
        string? ambito = null, params DocumentoListaDto[] documentos) =>
        RenderizarConMediador(busqueda, estado, ambito, documentos).Cut;

    private (IRenderedComponent<PaginaDocumentos> Cut, MediatorPorTipo Mediador) RenderizarConMediador(
        string? busqueda = null, string? estado = null, string? ambito = null, params DocumentoListaDto[] documentos)
    {
        var mediador = new MediatorPorTipo { Documentos = documentos };
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

        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(busqueda)) partes.Add("q=" + Uri.EscapeDataString(busqueda));
        if (!string.IsNullOrWhiteSpace(estado)) partes.Add("Estado=" + Uri.EscapeDataString(estado));
        if (!string.IsNullOrWhiteSpace(ambito)) partes.Add("Ambito=" + Uri.EscapeDataString(ambito));
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo(partes.Count == 0 ? "documentos" : "documentos?" + string.Join('&', partes));

        return (Render<PaginaDocumentos>(), mediador);
    }

    [Fact]
    public void Sin_resultados_y_con_busqueda_no_invita_a_subir_el_primero()
    {
        var cut = Renderizar(busqueda: "Reconocimiento médico");

        cut.Markup.Should().Contain("Ningún documento con estos filtros");
        cut.Markup.Should().Contain("Quitar los filtros");
        cut.Markup.Should().NotContain("Aún no hay documentos",
            "decirle «sube el primero» a quien acaba de buscar lo manda a duplicar un documento que ya existe");
    }

    [Fact]
    public void Sin_resultados_y_con_filtro_documental_tambien_lo_distingue()
    {
        var cut = Renderizar(estado: nameof(EstadoDocumento.Vencido));

        cut.Markup.Should().Contain("Ningún documento con estos filtros");
        cut.Markup.Should().NotContain("Aún no hay documentos",
            "cero vencidos es una buena noticia, no una lista sin documentos");
    }

    [Fact]
    public void Sin_filtros_y_sin_registros_sigue_invitando_a_subir_el_primero()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("Aún no hay documentos");
        cut.Markup.Should().Contain("Sube el primero para empezar a controlar vigencias.");
        cut.Markup.Should().NotContain("Ningún documento con estos filtros");
    }

    /// <summary>
    /// El filtrado es de servidor: la consulta devuelve el total YA filtrado.
    /// La barrera va delante — una aserción de ausencia sola sería verde vacío.
    /// </summary>
    [Fact]
    public void La_copia_no_afirma_cuantos_documentos_hay_subidos()
    {
        var cut = Renderizar(busqueda: "Reconocimiento médico");

        cut.Markup.Should().Contain("Ningún documento con estos filtros");
        cut.Markup.Should().NotContain("Hay documentos subidos");
    }

    /// <summary>
    /// <b>Este caso encontró el defecto.</b> Los tres filtros viajan por la URL
    /// y <c>OnParametersSet</c> los re-sincroniza desde ella; borrar solo
    /// <c>q</c> devolvía <c>Estado</c> y <c>Ambito</c> en la siguiente pasada, y
    /// la lista seguía recortada tras pulsar el botón.
    /// </summary>
    [Fact]
    public void Quitar_los_filtros_borra_los_tres_de_la_url_y_no_solo_la_busqueda()
    {
        var cut = Renderizar(busqueda: "Reconocimiento", estado: nameof(EstadoDocumento.Vencido),
            ambito: nameof(AmbitoAplicacion.Trabajador));
        cut.Markup.Should().Contain("Ningún documento con estos filtros", "es el punto de partida de este caso");

        cut.Find(".estado-vacio button").Click();

        var uri = Services.GetRequiredService<NavigationManager>().Uri;
        uri.Should().NotContain("Estado=", "dejarlo en la URL lo devuelve en la siguiente pasada de parámetros");
        uri.Should().NotContain("Ambito=");
        uri.Should().NotContain("q=Reconocimiento");

        cut.Markup.Should().NotContain("Ningún documento con estos filtros");
        cut.Markup.Should().Contain("Aún no hay documentos");
    }

    [Fact]
    public void Con_resultados_no_se_pinta_ningun_estado_vacio()
    {
        var cut = Renderizar(busqueda: "Reconocimiento", documentos: Documento("Reconocimiento médico"));

        cut.Markup.Should().NotContain("Ningún documento con estos filtros");
        cut.Markup.Should().NotContain("Aún no hay documentos");
        cut.Markup.Should().Contain("Reconocimiento médico");
    }

    // --- Recuento de consultas ----------------------------------------------------------------

    private static int ConsultasDeLista(MediatorPorTipo mediador) =>
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count();

    private static IRenderedComponent<CampoTexto> CajaDeBusqueda(IRenderedComponent<PaginaDocumentos> cut) =>
        cut.FindComponents<CampoTexto>().First(c => c.Instance.Placeholder?.StartsWith("Buscar por propietario") == true);

    /// <summary>
    /// Cambiar el tamaño de página pide la página 1 del tamaño nuevo UNA vez.
    /// <c>SetCurrentPageIndexAsync</c> ya avisa a QuickGrid aunque la página no
    /// cambie, así que refrescar además la rejilla pedía lo mismo dos veces
    /// (ver <c>RecargarAsync</c> en <c>Documentos.razor.cs</c>). Los mismos dos
    /// documentos antes y después mantienen el total quieto, así que lo que se
    /// cuenta es lo que pide la página y no una repetición de QuickGrid.
    /// </summary>
    [Fact]
    public void Cambiar_el_tamano_de_pagina_hace_una_sola_consulta()
    {
        var (cut, mediador) = RenderizarConMediador(documentos: [Documento("Reconocimiento médico"), Documento("Formación PRL")]);
        var consultasAntes = ConsultasDeLista(mediador);

        cut.Find(".paginador-tamano-select").Change("50");

        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Last().TamanoPagina.Should().Be(50);
        (ConsultasDeLista(mediador) - consultasAntes).Should().Be(1,
            "avisar a la paginación y refrescar la rejilla son dos formas de pedir lo mismo");
    }

    /// <summary>
    /// Buscar recarga la lista UNA vez, por el mismo motivo. Los dos documentos
    /// distintos que trae el doble del mediador no cambian con el texto (el
    /// doble no filtra de verdad), así que el total se queda quieto y no puede
    /// colarse una repetición de QuickGrid en el recuento.
    /// </summary>
    [Fact]
    public async Task Buscar_sin_cambiar_el_total_hace_una_sola_consulta()
    {
        var (cut, mediador) = RenderizarConMediador(documentos: [Documento("Reconocimiento médico"), Documento("Formación PRL")]);
        var consultasAntes = ConsultasDeLista(mediador);

        await cut.InvokeAsync(() => CajaDeBusqueda(cut).Instance.ValorChanged.InvokeAsync("Reconocimiento"));

        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Last().Busqueda.Should().Be("Reconocimiento");
        (ConsultasDeLista(mediador) - consultasAntes).Should().Be(1,
            "avisar a la paginación y refrescar la rejilla son dos formas de pedir lo mismo");
    }
}
