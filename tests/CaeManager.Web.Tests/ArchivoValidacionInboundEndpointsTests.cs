using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerLecturaInbound;
using CaeManager.Web.Features.Inbound.Validacion;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CaeManager.Web.Tests;

/// <summary>
/// <c>GET /inbound/validacion/contratos/{contrato}/documentos/{documento}/archivo</c> (demo Inbound P6). Ejecuta el
/// <c>IResult</c> del manejador real detrás de <c>UseCabecerasSeguridad</c> real, como
/// <c>DocumentosEndpointsVisorInlineTests</c>. La autoridad (capacidades, cartera, autorización del Tenant TEC) es de
/// <see cref="ObtenerArchivoParaValidarInboundQuery"/> y la prueban sus tests de Application; aquí, que el endpoint pide
/// esa query y no otra, que su null es un 404 sin cuerpo ni cabeceras de archivo, que la validación apagada no lee nada y
/// que la respuesta buena lleva las cabeceras de un archivo sensible. No recorre el routing ni la autorización por rol
/// (eso, el test de metadatos de abajo).
/// </summary>
public class ArchivoValidacionInboundEndpointsTests
{
    private static readonly Guid Contrato = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid Documento = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

    [Fact]
    public async Task Con_la_query_concedida_sirve_el_contenido_inline_sin_cache_ni_incrustacion_y_con_nosniff()
    {
        var mediador = new MediadorFalso(new ContenidoArchivoInboundDto("%PDF-original"u8.ToArray(), "application/pdf"));

        var contexto = await ServirAsync(mediador);

        mediador.Peticiones.Should().Equal([new ObtenerArchivoParaValidarInboundQuery(Contrato, Documento)],
            "la query Inbound de la cola, con el par de la ruta; nunca ObtenerDocumentoPorIdQuery de Outbound");
        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        contexto.Response.ContentType.Should().Be("application/pdf");
        ((MemoryStream)contexto.Response.Body).ToArray().Should().Equal("%PDF-original"u8.ToArray());
        var disposicion = contexto.Response.Headers.ContentDisposition.ToString();
        disposicion.Should().StartWith("inline").And.Contain("filename=documento.pdf").And.NotContainEquivalentOf("attachment");
        contexto.Response.Headers.CacheControl.ToString().Should().Be("no-store, no-cache, must-revalidate, private");
        contexto.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        contexto.Response.Headers["X-Frame-Options"].ToString().Should().Be("DENY", "la pantalla no lo incrusta: no se relaja");
    }

    [Fact]
    public async Task Un_tipo_desconocido_viaja_como_octet_stream_y_sin_extension()
    {
        var contexto = await ServirAsync(new MediadorFalso(new ContenidoArchivoInboundDto([1, 2], "application/octet-stream")));

        contexto.Response.ContentType.Should().Be("application/octet-stream");
        var disposicion = contexto.Response.Headers.ContentDisposition.ToString();
        disposicion.Should().StartWith("inline").And.Contain("filename=documento").And.NotContain("documento.");
    }

    /// <summary>
    /// Toda negativa de la query (sin ValidarInbound o LeerInbound, fuera de cartera, Documento no autorizado, revocado,
    /// sustituido, de un tipo de salud sin marca o de otro Tenant TCE) llega aquí como null: un 404 que no se distingue. Este
    /// arnés no monta <c>UseStatusCodePagesWithReExecute</c>: en la aplicación, ese 404 lleva la página genérica de «no
    /// encontrado», la misma para toda negativa; lo que se prueba aquí es que no sale ningún byte del archivo.
    /// </summary>
    [Fact]
    public async Task Una_negativa_de_la_query_es_un_404_sin_cuerpo_ni_cabeceras_de_archivo()
    {
        var contexto = await ServirAsync(new MediadorFalso(null));

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        ((MemoryStream)contexto.Response.Body).Length.Should().Be(0);
        contexto.Response.Headers.ContentDisposition.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Con_la_validacion_apagada_es_un_404_y_no_se_pide_nada()
    {
        var mediador = new MediadorFalso(new ContenidoArchivoInboundDto([1], "application/pdf"));

        var contexto = await ServirAsync(mediador, habilitado: false);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        mediador.Peticiones.Should().BeEmpty("apagada no hay lectura entre Tenants ni traza en el Tenant TEC");
    }

    [Fact]
    public async Task Sin_contexto_firmado_responde_403_sin_cuerpo_como_Outbound()
    {
        var contexto = await ServirAsync(new MediadorFalso(new PostgresException("sin contexto", "ERROR", "ERROR", "42501")));

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ((MemoryStream)contexto.Response.Body).Length.Should().Be(0);
    }

    [Fact]
    public void La_ruta_es_la_de_Inbound_y_solo_admite_la_cookie_de_sesion_y_los_roles_de_la_pantalla_de_validacion()
    {
        // Con ISender registrado, como en la aplicación: sin él, el enlace de parámetros lo tomaría por el cuerpo.
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ISender>(new MediadorFalso(null));
        var app = builder.Build();
        app.MapArchivoValidacionInboundEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().Single();

        endpoint.RoutePattern.RawText.Should().Be(ArchivoValidacionInboundEndpoints.Plantilla);
        ArchivoValidacionInboundEndpoints.Ruta(Contrato, Documento)
            .Should().Be($"/inbound/validacion/contratos/{Contrato}/documentos/{Documento}/archivo");
        var autorizacion = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Should().ContainSingle().Subject;
        autorizacion.AuthenticationSchemes.Should().Be(IdentityConstants.ApplicationScheme);
        autorizacion.Roles.Should().Be("Administrador,DireccionCae,CoordinadorCae,GestorCae");
        endpoint.Metadata.Should().NotContain(m => m is IAllowAnonymous);
    }

    // ── Andamiaje ─────────────────────────────────────────────────────────

    private static async Task<DefaultHttpContext> ServirAsync(MediadorFalso mediador, bool habilitado = true)
    {
        var servicios = new ServiceCollection().AddLogging().BuildServiceProvider();
        var app = new ApplicationBuilder(servicios);
        app.UseCabecerasSeguridad();
        app.Run(async contexto =>
        {
            var resultado = await ArchivoValidacionInboundEndpoints.ServirAsync(
                Contrato, Documento, contexto, mediador, Options.Create(new OpcionesValidacionInbound { Habilitado = habilitado }),
                NullLoggerFactory.Instance, CancellationToken.None);
            await resultado.ExecuteAsync(contexto);
        });
        var pipeline = app.Build();

        var contexto = new DefaultHttpContext { RequestServices = servicios };
        contexto.Response.Body = new MemoryStream();
        await pipeline(contexto);
        return contexto;
    }

    /// <summary>Responde lo que se le da (o lanza, si es una excepción) y anota cada petición.</summary>
    private sealed class MediadorFalso(object? respuesta) : ISender
    {
        public List<object> Peticiones { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Peticiones.Add(request);
            return respuesta is Exception error ? Task.FromException<TResponse>(error) : Task.FromResult((TResponse)respuesta!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
