using System.Security.Claims;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
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

using PaginaDocumentos = CaeManager.Web.Features.Documentos.Pages.Documentos;

/// <summary>
/// Lo que ve un usuario de <b>Consulta</b> (p. ej. una Dirección CAE que opera un
/// Tenant propietario como Consulta delegada) donde antes había un hueco en
/// blanco o un botón que fallaba al pulsarlo.
///
/// <para>
/// <b>Lo que SÍ observa:</b> que las pestañas de gestión de Documentos (Plataformas
/// CAE, Reclamaciones, Revisión IA, Plantillas), que un rol de Consulta no
/// incluye, dicen que no están disponibles para su rol en vez de quedarse en
/// blanco; que la pestaña Preventivo, que no lleva puerta, sí pinta contenido;
/// que <c>SoloConEscritura</c> pinta su contenido solo para los roles de escritura;
/// que «+ Nuevo documento» no se ofrece a Consulta y sí a un Coordinador; y que el
/// aviso de solo consulta sale para Consulta y no para quien escribe.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> el rol efectivo de verdad —aquí el principal lo
/// fabrica el test; que <c>RolEfectivoDelWorkspaceMiddleware</c> lo sustituya por
/// el del workspace delegado es cosa suya—, ni la autorización de los comandos, ni
/// el contenido interior de las pestañas de gestión con un rol permitido más allá
/// de que no reciben el mensaje de falta de permiso, ni los botones de pantallas
/// que no monta (los vigila, solo por estructura, el trinquete de Architecture.Tests).
/// </para>
/// </summary>
public class SoloLecturaEnLaInterfazTests : BunitContext
{
    public SoloLecturaEnLaInterfazTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class Autenticacion(string rol) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "test"))));
    }

    /// <summary>Evalúa los roles de verdad: un doble que autorizara siempre dejaría pasar lo que se oculta por rol.</summary>
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

    /// <summary>Contesta con listas vacías a lo que la página y sus pestañas preguntan al cargar.</summary>
    private sealed class MediadorVacio : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object? respuesta = request switch
            {
                ObtenerDocumentosQuery q => new ResultadoPaginado<DocumentoListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerFiltrosGuardadosQuery => Array.Empty<FiltroGuardadoDto>(),
                _ => ListaVaciaSiLoEs(typeof(TResponse))
            };

            // Una lista vacía para toda consulta que devuelva una lista; lo demás, null. Las
            // pestañas de gestión pintan su estado vacío con una lista vacía, y estas pruebas
            // solo miran el hueco de la pestaña, no su contenido.
            return Task.FromResult((TResponse)respuesta!);
        }

        private static object? ListaVaciaSiLoEs(Type tipo)
        {
            var lista = tipo.IsGenericType && tipo.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ? tipo : null;
            return lista is null ? null : Array.CreateInstance(lista.GetGenericArguments()[0], 0);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class UsuarioActual(string rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class AlmacenSinUso : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ConversorSinUso : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private void RegistrarComun(string rol)
    {
        Services.AddScoped<AuthenticationStateProvider>(_ => new Autenticacion(rol));
        Services.AddAuthorizationCore();
        Services.AddScoped<IAuthorizationService, AutorizacionPorRoles>();
        Services.AddCascadingAuthenticationState();
    }

    private IRenderedComponent<PaginaDocumentos> RenderizarDocumentos(string rol, string pestana)
    {
        RegistrarComun(rol);
        Services.AddScoped<IMediator, MediadorVacio>();
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService>(_ => new UsuarioActual(rol));
        Services.AddScoped<IFileStorageService, AlmacenSinUso>();
        Services.AddScoped<IConversorWordPdfService, ConversorSinUso>();
        Services.AddSingleton<ILogger<PaginaDocumentos>>(_ => NullLogger<PaginaDocumentos>.Instance);

        Services.GetRequiredService<NavigationManager>().NavigateTo($"documentos?pestana={pestana}");
        return Render<PaginaDocumentos>();
    }

    // ----------------------------------------------------------------------- pestañas en blanco

    [Theory]
    [InlineData("plataforma", "Plataformas CAE")]
    [InlineData("reclamaciones", "Reclamaciones")]
    [InlineData("revision-ia", "Revisión IA")]
    [InlineData("plantillas", "Plantillas")]
    public void Consulta_no_ve_en_blanco_una_pestana_de_gestion_sino_que_se_le_dice_que_su_rol_no_la_incluye(
        string pestana, string rotulo)
    {
        var cut = RenderizarDocumentos("Consulta", pestana);

        var mensaje = cut.FindAll(".estado-vacio");
        mensaje.Should().ContainSingle("el hueco de la pestaña tiene que decir algo, no quedarse en blanco");
        mensaje[0].QuerySelector("h3")!.TextContent.Should().Be("Esta sección no está disponible para tu rol");
        mensaje[0].QuerySelector("p")!.TextContent.Should()
            .Contain($"«{rotulo}»", "dice de qué sección habla")
            .And.Contain("Consulta", "dice con qué rol se está mirando: el efectivo en el workspace, que puede no ser el propio");
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    public void Un_rol_de_gestion_documental_no_recibe_el_mensaje_de_falta_de_permiso_en_las_pestanas_de_gestion(string rol)
    {
        foreach (var pestana in new[] { "plataforma", "reclamaciones", "revision-ia", "plantillas" })
        {
            using var contexto = new SoloLecturaEnLaInterfazTests();
            var cut = contexto.RenderizarDocumentos(rol, pestana);

            cut.Markup.Should().NotContain("Esta sección no está disponible para tu rol",
                $"el rol {rol} sí gestiona la pestaña {pestana}: el mensaje de permiso solo es para quien no puede");
        }
    }

    [Fact]
    public void La_pestana_Preventivo_no_lleva_puerta_de_rol_y_para_Consulta_pinta_contenido()
    {
        var cut = RenderizarDocumentos("Consulta", "sugerencias");

        cut.Markup.Should().NotContain("Esta sección no está disponible para tu rol");
        cut.Markup.Should().Contain("Nada próximo a vencer",
            "Preventivo tiene que pintar su estado vacío o su lista; un blanco sería el mismo defecto que en las otras pestañas");
    }

    // ----------------------------------------------------------------------- botones de escritura

    [Fact]
    public void Nuevo_documento_no_se_ofrece_a_Consulta_y_si_a_un_Coordinador()
    {
        var consulta = RenderizarDocumentos("Consulta", "listado");
        consulta.Markup.Should().NotContain("+ Nuevo documento", "Consulta no puede crear: ofrecerlo es un botón que falla al pulsarlo");

        using var contexto = new SoloLecturaEnLaInterfazTests();
        var coordinador = contexto.RenderizarDocumentos("CoordinadorCae", "listado");
        coordinador.Markup.Should().Contain("+ Nuevo documento");
    }

    [Theory]
    [InlineData("Consulta", false)]
    [InlineData("Cliente", false)]
    [InlineData("Administrador", true)]
    [InlineData("DireccionCae", true)]
    [InlineData("CoordinadorCae", true)]
    [InlineData("GestorCae", true)]
    public void SoloConEscritura_pinta_su_contenido_solo_para_los_roles_que_escriben(string rol, bool debePintar)
    {
        RegistrarComun(rol);

        var cut = Render<SoloConEscritura>(p => p.AddChildContent("<button>Crear</button>"));

        cut.Markup.Contains("<button>Crear</button>", StringComparison.Ordinal).Should().Be(debePintar);
    }

    // ----------------------------------------------------------------------- aviso

    [Theory]
    [InlineData("Consulta", true)]
    [InlineData("Administrador", false)]
    [InlineData("DireccionCae", false)]
    [InlineData("CoordinadorCae", false)]
    [InlineData("GestorCae", false)]
    [InlineData("Cliente", false)]
    public void El_aviso_de_solo_consulta_sale_para_Consulta_y_para_nadie_mas(string rol, bool debeSalir)
    {
        RegistrarComun(rol);

        var cut = Render<AvisoSoloConsulta>();

        cut.FindAll(".aviso-solo-consulta").Count.Should().Be(debeSalir ? 1 : 0);
    }
}
