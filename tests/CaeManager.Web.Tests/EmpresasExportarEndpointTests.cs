using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Features.Empresas;
using ClosedXML.Excel;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// <c>GET /empresas/exportar.xlsx</c> ejecutado de verdad (el endpoint registrado, con su binding de la cadena de
/// consulta): «Exportar esta vista» con el filtro «Cliente» devuelve solo las Empresas de ese Cliente, y un Cliente
/// que no es un Id es una petición mal formada (400), no una exportación de todo. Qué Empresas alcanza cada rol lo ata
/// <c>ObtenerEmpresasPorClienteQueryTests</c> (el handler real); aquí el doble del mediador solo reproduce el filtro.
/// </summary>
public class EmpresasExportarEndpointTests
{
    private static readonly Guid Cyberdyne = Guid.NewGuid();

    private static readonly EmpresaListaDto Aragon = Empresa("Aragon Montajes S.L.");
    private static readonly EmpresaListaDto Basauri = Empresa("Basauri Obras S.L.");
    private static readonly EmpresaListaDto Castro = Empresa("Castro Servicios S.L.");

    private static EmpresaListaDto Empresa(string razonSocial) =>
        new(Guid.NewGuid(), razonSocial, null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static async Task<(HttpContext Contexto, MediatorDeEmpresas Mediador)> ExportarAsync(string consulta)
    {
        var (contexto, mediador, _) = await ExportarConRegistroAsync(consulta);
        return (contexto, mediador);
    }

    private static async Task<(HttpContext Contexto, MediatorDeEmpresas Mediador, RegistroDeExportaciones Registro)> ExportarConRegistroAsync(string consulta)
    {
        var mediador = new MediatorDeEmpresas();
        var registro = new RegistroDeExportaciones();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IMediator>(mediador);
        builder.Services.AddSingleton<ITenantActual>(new TenantActualFalso());
        builder.Services.AddSingleton<IRegistroExportacionService>(registro);
        await using var app = builder.Build();
        app.MapEmpresasEndpoints();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/empresas/exportar.xlsx");

        using var scope = app.Services.CreateScope();
        var contexto = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        contexto.Request.QueryString = new QueryString(consulta);
        contexto.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(contexto);
        return (contexto, mediador, registro);
    }

    private static List<string> RazonesSociales(HttpContext contexto)
    {
        contexto.Response.Body.Position = 0;
        using var libro = new XLWorkbook(contexto.Response.Body);
        var hoja = libro.Worksheet("Empresas");
        return hoja.RowsUsed().Skip(1).Select(f => f.Cell(1).GetString()).ToList();
    }

    [Fact]
    public async Task Con_el_filtro_Cliente_la_exportacion_solo_lleva_las_Empresas_de_ese_Cliente()
    {
        var (contexto, mediador) = await ExportarAsync($"?cliente={Cyberdyne}");

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        mediador.Consultas.Should().NotBeEmpty().And.OnlyContain(q => q.ClienteEmpresarialId == Cyberdyne,
            "el Cliente de la URL llega a la consulta con la que se exporta");
        RazonesSociales(contexto).Should().Equal(Aragon.RazonSocial, Basauri.RazonSocial);
    }

    [Fact]
    public async Task Sin_filtro_Cliente_la_exportacion_lleva_todas_las_Empresas()
    {
        var (contexto, mediador) = await ExportarAsync(string.Empty);

        mediador.Consultas.Should().OnlyContain(q => q.ClienteEmpresarialId == null);
        RazonesSociales(contexto).Should().Equal(Aragon.RazonSocial, Basauri.RazonSocial, Castro.RazonSocial);
    }

    [Theory]
    [InlineData("?cliente=no-es-un-id")]
    [InlineData("?cliente=1234")]
    public async Task Un_Cliente_que_no_es_un_Id_responde_400_y_no_exporta_nada(string consulta)
    {
        var (contexto, mediador) = await ExportarAsync(consulta);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        mediador.Consultas.Should().BeEmpty("no se exporta «todo» por haber ignorado un filtro mal formado");
    }

    [Fact]
    public async Task La_exportacion_registra_las_filas_entregadas_y_el_criterio_aplicado()
    {
        var (contexto, _, registro) = await ExportarConRegistroAsync($"?cliente={Cyberdyne}&q=01234567Z&estado=Vencido,%20SinDocumentos,Garcia,7");

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var entrada = registro.Registros.Should().ContainSingle().Subject;
        entrada.Tipo.Should().Be("Empresa");
        entrada.Filas.Should().Be(2, "las dos Empresas del Cliente que lleva el libro");
        entrada.Criterios.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["listado"] = "empresas",
            ["busqueda"] = "true",
            ["estado"] = "Vencido,SinDocumentos",
            ["cliente"] = Cyberdyne.ToString()
        }, "solo consta lo que la consulta reconoció: ni el texto de la búsqueda ni los estados que no existen");
    }

    [Fact]
    public async Task Un_Cliente_que_no_es_un_Id_no_registra_exportacion()
    {
        var (_, _, registro) = await ExportarConRegistroAsync("?cliente=no-es-un-id");

        registro.Registros.Should().BeEmpty();
    }

    private sealed class RegistroDeExportaciones : IRegistroExportacionService
    {
        public List<(string Tipo, int Filas, IReadOnlyDictionary<string, string> Criterios)> Registros { get; } = [];

        public Task RegistrarAsync(string entidadTipo, int filas, IReadOnlyDictionary<string, string> criterios,
            CancellationToken cancellationToken = default)
        {
            Registros.Add((entidadTipo, filas, criterios));
            return Task.CompletedTask;
        }
    }

    private static readonly Guid Origen = Guid.NewGuid();

    private sealed class TenantActualFalso : ITenantActual
    {
        public Guid? TenantId => Origen;
    }

    private sealed class MediatorDeEmpresas : IMediator
    {
        public List<ObtenerEmpresasQuery> Consultas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            // Sin cartera: la guarda del estado 4a deja pasar (la cubre ExportacionesDeListadosEstado4aTests).
            if (request is ObtenerClientesAutorizadosQuery)
                return Task.FromResult((TResponse)(object)new List<ClienteAutorizadoDto> { new(Origen, "Operador CAE", EsOrigen: true) });

            if (request is not ObtenerEmpresasQuery consulta)
                throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.");

            Consultas.Add(consulta);
            IReadOnlyList<EmpresaListaDto> empresas = consulta.ClienteEmpresarialId is { } cliente
                ? (cliente == Cyberdyne ? [Aragon, Basauri] : [])
                : [Aragon, Basauri, Castro];
            return Task.FromResult((TResponse)(object)new ResultadoPaginado<EmpresaListaDto>(
                empresas, empresas.Count, consulta.Pagina, consulta.TamanoPagina));
        }

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
