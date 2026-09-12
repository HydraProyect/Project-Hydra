using System.Security.Claims;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Queries.DetectarCamposDocumento;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests;

// Alias: el tipo de la página vive bajo Features.Documentos.Pages —
// mismo motivo que el alias de DocumentosGen2Tests.
using PaginaSubidaMasiva = CaeManager.Web.Features.Documentos.Pages.SubidaMasiva;

/// <summary>
/// El <c>[Authorize]</c> de /documentos/subida-masiva admite Consulta —
/// decisión del propietario de la pantalla, no de este test (ver el
/// comentario de clase de <see cref="PaginaSubidaMasiva"/>). Lo que SÍ prueba
/// este test es que, dentro, a Consulta no se le pinta ningún disparador de
/// escritura (visible ≠ modificable) y que ni siquiera se toca
/// <see cref="IFileStorageService"/>: los fakes de almacenamiento, conversión
/// y rasterizado lanzan si alguien los invoca, así que un archivo que
/// lograra procesarse igual haría fallar el test con la traza exacta de qué
/// camino se tomó — no hace falta un mock que cuente llamadas.
///
/// La composición real de <c>CrearDocumentoCommand</c> con
/// <c>AutorizacionEscrituraBehavior</c> vive en
/// <c>CrearDocumentoCommandBloqueadoParaConsultaTests</c> (Application/Integration);
/// esto es la capa de interfaz, no la de autorización.
/// </summary>
public class SubidaMasivaSoloLecturaTests : BunitContext
{
    public SubidaMasivaSoloLecturaTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediadorFalso : IMediator
    {
        private object? Responder(object request) => request switch
        {
            ObtenerTrabajadoresParaSelectorQuery => Array.Empty<TrabajadorSelectorDto>(),
            ObtenerTiposDocumentoQuery => Array.Empty<TipoDocumentoListaDto>(),
            _ => throw new NotSupportedException(
                $"Consulta no debería poder enviar {request.GetType().Name}: es un Command/Query de escritura o de detección sobre un archivo que Consulta nunca debería llegar a procesar.")
        };

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)Responder(request)!);

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
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <summary>Mismo criterio que DocumentosGen2Tests: si algo llega aquí, la pantalla tomó un camino que Consulta no debería poder tomar.</summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Consulta no debería llegar a tocar almacenamiento; si esto salta, la pantalla dejó de comprobar la capacidad antes de guardar.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Consulta no debería llegar a convertir ningún archivo.");
    }

    private sealed class RasterizadorQueNadieDebeTocar : IRasterizadorPaginasPdfService
    {
        public Result<byte[]> RasterizarPagina(byte[] contenidoPdf, int indicePagina, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Consulta no debería llegar a rasterizar ningún archivo.");
    }

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

    private IRenderedComponent<PaginaSubidaMasiva> Renderizar(string rol)
    {
        Services.AddScoped<IMediator>(_ => new MediadorFalso());
        Services.AddScoped<ToastService>();
        Services.AddScoped<ICurrentUserService>(_ => new UsuarioActualFalso(rol));
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddScoped<IRasterizadorPaginasPdfService, RasterizadorQueNadieDebeTocar>();
        Services.AddSingleton<ILogger<PaginaSubidaMasiva>>(_ => NullLogger<PaginaSubidaMasiva>.Instance);
        Services.AddScoped<AuthenticationStateProvider>(_ => new AutenticacionFalsa(rol));
        Services.AddAuthorizationCore();
        Services.AddScoped<IAuthorizationService, AutorizacionPorRoles>();
        Services.AddCascadingAuthenticationState();

        return Render<PaginaSubidaMasiva>();
    }

    [Fact]
    public void Consulta_no_ve_la_zona_de_subida_sino_un_aviso_de_solo_lectura()
    {
        var cut = Renderizar("Consulta");

        cut.FindAll(".zona-soltar-archivo").Should().BeEmpty("Consulta no puede crear documentos: no debe poder ni empezar a subir uno");
        cut.Markup.Should().Contain("Solo lectura");
    }

    [Fact]
    public void GestorCae_si_ve_la_zona_de_subida()
    {
        var cut = Renderizar("GestorCae");

        cut.FindAll(".zona-soltar-archivo").Should().ContainSingle("GestorCae sí puede crear documentos: la misma pantalla debe ofrecerle la zona de subida");
        cut.Markup.Should().NotContain("Solo lectura");
    }
}
