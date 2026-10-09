using System.Security.Claims;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.TiposDocumento.Pages;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Quien administra por Encargo de administración (decisión D-8, 2026-10-08) ve deshabilitada la
/// configuración global de IA de cada tipo de documento: los comandos <c>…GlobalCommand</c> están
/// entre los actos excluidos del encargo.
///
/// <para>
/// <b>Lo que NO observa:</b> que el comando se rechace. Esa barrera es de Application
/// (<c>ExclusionesDelEncargoBehavior</c>); aquí solo se mira que la pantalla no ofrezca lo que falla.
/// </para>
/// </summary>
public class TiposDocumentoBajoEncargoTests : BunitContext
{
    public TiposDocumentoBajoEncargoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura(Roles.Administrador);
        Services.AddScoped<IMediator>(_ => new MediatorFalso());
        Services.AddScoped<ToastService>();
        Services.AddLocalization();
    }

    private IRenderedComponent<TiposDocumento> Renderizar(bool porEncargo)
    {
        // La última inscripción gana: mismo rol que ConRolDeEscritura y, además, el claim del encargo.
        Services.AddScoped<AuthenticationStateProvider>(_ => new Autenticacion(porEncargo));
        var cut = Render<TiposDocumento>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Formación PRL"));
        return cut;
    }

    [Fact]
    public void A_quien_administra_por_encargo_los_interruptores_globales_de_IA_se_le_pintan_deshabilitados()
    {
        var cut = Renderizar(porEncargo: true);

        var interruptores = cut.FindAll("input.interruptor-tipo");
        interruptores.Should().NotBeEmpty();
        interruptores.Should().OnlyContain(i => i.HasAttribute("disabled"));
        cut.FindAll("label.celda-interruptor").Should().OnlyContain(
            l => l.GetAttribute("title")!.Contains("no forma parte del Encargo de administración"));
    }

    [Fact]
    public void Un_Administrador_propio_los_tiene_habilitados()
    {
        var cut = Renderizar(porEncargo: false);

        var interruptores = cut.FindAll("input.interruptor-tipo");
        interruptores.Should().NotBeEmpty();
        interruptores.Should().OnlyContain(i => !i.HasAttribute("disabled"), "control positivo: sin el claim nada se reserva");
    }

    private sealed class Autenticacion(bool porEncargo) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.Role, Roles.Administrador) };
            if (porEncargo)
                claims.Add(new Claim(RolEfectivoDelWorkspaceMiddleware.TipoClaimEncargoAdministracion, Guid.NewGuid().ToString()));
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))));
        }
    }

    private sealed class MediatorFalso : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)[],
                ObtenerCentrosParaSelectorQuery => (IReadOnlyList<CentroSelectorDto>)[],
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)
                [
                    new TipoDocumentoListaDto(Guid.NewGuid(), "Formación PRL", 12, true, 1, AmbitoAplicacion.Trabajador,
                        RequisitoDocumental.Si, NaturalezaJuridica.ObligacionLegal, null, null, null, null,
                        false, false, false, default, []),
                ],
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));

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
}
