using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerLogoTenant;
using CaeManager.Web.Features.Tenants;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// I11 por el pipeline: <c>UseStatusCodePagesWithReExecute("/not-found")</c> de Program.cs convierte
/// cualquier 404 sin cuerpo en la página Blazor. Los tests del endpoint que llaman a
/// <c>ServirAsync</c> directamente no pasan por ese middleware, así que aquí se monta el mismo
/// middleware con el endpoint real sobre Kestrel en un puerto efímero (el proyecto no referencia
/// TestHost; límite del instrumento: el resto del pipeline de Program.cs no está).
/// Revisión Codex, ronda 2.
/// </summary>
public class LogoTenantEndpointPipelineCompletoTests
{
    [Fact]
    public async Task El_404_de_Tenant_no_autorizado_no_lo_reejecuta_StatusCodePages()
    {
        var (status, cuerpo) = await PedirAsync(logo: null, almacenamiento: new AlmacenFalso(existe: true));

        status.Should().Be(404);
        cuerpo.Should().BeEmpty("el cuerpo del 404 tiene que ser vacío e idéntico, no la página /not-found");
    }

    [Fact]
    public async Task El_404_de_blob_ausente_tampoco_se_reejecuta()
    {
        var (status, cuerpo) = await PedirAsync(
            logo: new LogoTenantDto("k/x.png", "0123456789abcdef"), almacenamiento: new AlmacenFalso(existe: false));

        status.Should().Be(404);
        cuerpo.Should().BeEmpty();
    }

    [Fact]
    public async Task El_pipeline_de_prueba_si_reejecuta_un_404_de_otra_ruta()
    {
        // Control positivo: sin él, un pipeline que no reejecuta nada haría pasar los dos tests de arriba.
        var (status, cuerpo) = await PedirAsync(logo: null, almacenamiento: new AlmacenFalso(true), ruta: "/otra-ruta");

        cuerpo.Should().Be("PAGINA-NOT-FOUND");
        status.Should().Be(404);
    }

    private static async Task<(int Status, string Cuerpo)> PedirAsync(
        LogoTenantDto? logo, IFileStorageService almacenamiento, string? ruta = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IMediator>(new MediatorFalso(logo));
        builder.Services.AddSingleton(almacenamiento);
        await using var app = builder.Build();
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.MapGet("/not-found", () => "PAGINA-NOT-FOUND");
        app.MapLogoTenantEndpoints();
        await app.StartAsync();
        try
        {
            var direccion = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var cliente = new HttpClient { BaseAddress = new Uri(direccion) };
            var respuesta = await cliente.GetAsync(ruta ?? $"/tenants/{Guid.NewGuid()}/logo");
            return ((int)respuesta.StatusCode, await respuesta.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private sealed class AlmacenFalso(bool existe) : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            existe ? Task.FromResult<Stream>(new MemoryStream([1])) : throw new FileNotFoundException(identificador);

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MediatorFalso(LogoTenantDto? logo) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object?)logo!);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
