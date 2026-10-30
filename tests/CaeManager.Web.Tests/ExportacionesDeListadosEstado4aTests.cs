using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Features.Centros;
using CaeManager.Web.Features.Clientes;
using CaeManager.Web.Features.Empresas;
using CaeManager.Web.Features.Subcontratas;
using CaeManager.Web.Features.Subcontratas.Recursos;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Empresas, Subcontratas, Clientes y Centros resuelven en su página el estado 4a
/// («Selecciona una empresa de tu cartera») con <c>ContextoEmpresaActiva.ResolverAsync</c> y no enseñan
/// los datos del Tenant de origen. Su exportación aplica la misma condición
/// (<see cref="ClientesAutorizados.PideElegirEmpresa"/>): sin empresa elegida redirige a su página sin
/// consultar el listado ni generar el archivo; con ella, o sin cartera, exporta como antes.
///
/// <para>
/// Estos cuatro endpoints no registran la exportación (no tienen <c>IRegistroExportacionService</c>):
/// «no se registra nada» lo cubre que el único envío al mediador sea la lista autorizada. No pasa por
/// el routing de ASP.NET: los roles y el enlace de la cadena de consulta los miden
/// <c>EmpresasExportarEndpointTests</c>, los tests de arquitectura y los E2E de exportación.
/// </para>
/// </summary>
public class ExportacionesDeListadosEstado4aTests
{
    private static readonly Guid Origen = Guid.NewGuid();
    private static readonly Guid Externo = Guid.NewGuid();

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId { get; } = tenantId;
    }

    /// <summary>
    /// Responde la lista autorizada y, a cualquier consulta paginada del listado, una página vacía.
    /// Anota todo lo que recibe: así se ve que en el estado 4a no sale nada más que la lista autorizada.
    /// </summary>
    private sealed class MediatorFalso(IReadOnlyList<ClienteAutorizadoDto> autorizados) : IMediator
    {
        public List<object> Enviados { get; } = [];

        public IEnumerable<object> ConsultasDelListado => Enviados.Where(e => e is not ObtenerClientesAutorizadosQuery);

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            if (request is ObtenerClientesAutorizadosQuery)
                return Task.FromResult((TResponse)autorizados);

            var tipo = typeof(TResponse);
            if (!tipo.IsGenericType || tipo.GetGenericTypeDefinition() != typeof(ResultadoPaginado<>))
                throw new NotSupportedException(request.GetType().Name);

            var vacia = Array.CreateInstance(tipo.GetGenericArguments()[0], 0);
            return Task.FromResult((TResponse)Activator.CreateInstance(tipo, vacia, 0, 1, 1)!);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private static readonly IStringLocalizer<TextosSubcontratas> Textos =
        new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider()
            .GetRequiredService<IStringLocalizer<TextosSubcontratas>>();

    /// <summary>Cada listado con su página y el nombre del archivo que entrega.</summary>
    public static TheoryData<string, string, string> Listados => new()
    {
        { "Empresas", "/empresas", "empresas.xlsx" },
        { "Subcontratas", "/subcontratas", "subcontratas.xlsx" },
        { "Clientes", "/clientes", "clientes.xlsx" },
        { "Centros", "/centros", "centros.xlsx" },
    };

    private static Task<IResult> ExportarAsync(string listado, IMediator mediador, Guid tenantActual)
    {
        var tenant = new TenantActualFalso(tenantActual);
        return listado switch
        {
            "Empresas" => EmpresasEndpoints.ExportarAsync(mediador, tenant, default),
            "Subcontratas" => SubcontratasEndpoints.ExportarAsync(mediador, tenant, Textos, default),
            "Clientes" => ClientesEndpoints.ExportarAsync(mediador, tenant, default),
            "Centros" => CentrosEndpoints.ExportarAsync(mediador, tenant, default),
            _ => throw new ArgumentOutOfRangeException(nameof(listado), listado, null),
        };
    }

    private static ClienteAutorizadoDto Propio(bool gestionado = false) =>
        new(Origen, "Operador CAE", EsOrigen: true, EsGestionadoPorOperacion: gestionado);

    private static ClienteAutorizadoDto Cartera() =>
        new(Externo, "Tenant beneficiario", EsOrigen: false, EsGestionadoPorOperacion: true, EsCarteraGestorCae: true);

    [Theory]
    [MemberData(nameof(Listados))]
    public async Task En_el_estado_4a_redirige_a_la_pagina_y_no_consulta_el_listado_del_origen(string listado, string pagina, string archivo)
    {
        var mediador = new MediatorFalso([Propio(), Cartera()]);

        var resultado = await ExportarAsync(listado, mediador, Origen);

        resultado.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be(pagina);
        mediador.ConsultasDelListado.Should().BeEmpty($"no se piden los datos de la organización de origen ni sale {archivo}");
        mediador.Enviados.Should().ContainSingle("control positivo: la guarda sí consultó la lista autorizada")
            .Which.Should().BeOfType<ObtenerClientesAutorizadosQuery>();
    }

    [Theory]
    [MemberData(nameof(Listados))]
    public async Task Con_una_empresa_de_la_cartera_elegida_exporta(string listado, string pagina, string archivo)
    {
        var mediador = new MediatorFalso([Propio(), Cartera()]);

        var resultado = await ExportarAsync(listado, mediador, Externo);

        resultado.Should().NotBeOfType<RedirectHttpResult>($"con empresa elegida no se vuelve a {pagina}");
        resultado.Should().BeOfType<FileStreamHttpResult>().Which.FileDownloadName.Should().Be(archivo);
        mediador.ConsultasDelListado.Should().NotBeEmpty("el listado de la empresa elegida sí se consulta");
    }

    [Theory]
    [MemberData(nameof(Listados))]
    public async Task Con_el_origen_gestionado_o_sin_cartera_exporta_el_origen(string listado, string pagina, string archivo)
    {
        foreach (var autorizados in new[]
                 {
                     new[] { Propio(gestionado: true), Cartera() },
                     [Propio()],
                 })
        {
            var mediador = new MediatorFalso(autorizados);

            var resultado = await ExportarAsync(listado, mediador, Origen);

            resultado.Should().NotBeOfType<RedirectHttpResult>($"sin estado 4a no se vuelve a {pagina}");
            resultado.Should().BeOfType<FileStreamHttpResult>().Which.FileDownloadName.Should().Be(archivo);
            mediador.ConsultasDelListado.Should().NotBeEmpty();
        }
    }
}
