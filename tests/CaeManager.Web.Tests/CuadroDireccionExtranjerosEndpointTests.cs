using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.CuadroDireccion;
using ClosedXML.Excel;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// <c>GET /direccion/extranjeros.xlsx</c> ejecutado de verdad (el endpoint registrado, con su binding de la cadena de
/// consulta): las doce columnas y una fila por Trabajador extranjero y Visita, 400 para un mes no válido o futuro, el
/// rastro de la exportación (lleva DNI y pasaportes), las cabeceras que prohíben la caché, la nota de Tenants sin
/// alcance completo y la política de roles del endpoint. Qué filas entran y el recorrido por Tenant los ata
/// <c>ExtranjerosDelMesMultiTenantTests</c> (el handler real, en PostgreSQL); aquí el doble del mediador devuelve el DTO.
/// </summary>
public class CuadroDireccionExtranjerosEndpointTests
{
    private static readonly FilaExtranjeroDelMesDto Amina = new(
        Guid.NewGuid(), "Ibertec", new DateOnly(2026, 9, 1), "Jordi Puig", "Montajes Norte S.L.", null, "Cyberdyne",
        "Planta Sur", new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 5), "Amina Benali", "AB1234567", "Marta López", Guid.NewGuid());

    private static readonly FilaExtranjeroDelMesDto Oleg = new(
        Guid.NewGuid(), "Ibertec", new DateOnly(2026, 9, 10), null, "Montajes Norte S.L.", "Andamios Este S.L.",
        "Cyberdyne", "Planta Norte", new DateOnly(2026, 9, 12), new DateOnly(2026, 9, 12), "Oleg Ivanov", "X-99", null, Guid.NewGuid());

    private sealed record Resultado(HttpContext Contexto, MediatorDeExtranjeros Mediador, RegistroFalso Registro);

    private static async Task<Resultado> ExportarAsync(string consulta, ExtranjerosDelMesDto respuesta)
    {
        var mediador = new MediatorDeExtranjeros(respuesta);
        var registro = new RegistroFalso();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IMediator>(mediador);
        builder.Services.AddSingleton<IRegistroExportacionService>(registro);
        builder.Services.AddLocalization();
        await using var app = builder.Build();
        app.MapCuadroDireccionEndpoints();
        var endpoint = Endpoint(app);

        using var scope = app.Services.CreateScope();
        var contexto = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        contexto.Request.QueryString = new QueryString(consulta);
        contexto.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(contexto);
        return new Resultado(contexto, mediador, registro);
    }

    private static RouteEndpoint Endpoint(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == CuadroDireccionEndpoints.RutaExtranjerosDelMes);

    private static List<List<string>> Filas(HttpContext contexto)
    {
        contexto.Response.Body.Position = 0;
        using var libro = new XLWorkbook(contexto.Response.Body);
        var hoja = libro.Worksheets.Single();
        var ultimaColumna = hoja.Row(1).LastCellUsed().Address.ColumnNumber;
        return hoja.RowsUsed()
            .Select(f => Enumerable.Range(1, ultimaColumna).Select(c => f.Cell(c).GetFormattedString()).ToList())
            .ToList();
    }

    private static ExtranjerosDelMesDto Mes(int sinAlcance = 0, CuadroDireccionSinCifras? sinCifras = null) =>
        new(2026, 9, sinCifras is null ? [Amina, Oleg] : [], sinAlcance, sinCifras);

    [Fact]
    public async Task Devuelve_las_doce_columnas_y_una_fila_por_Trabajador_extranjero_y_Visita()
    {
        var resultado = await ExportarAsync("?anyo=2026&mes=9", Mes());

        resultado.Contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        resultado.Mediador.Consultas.Should().ContainSingle().Which.Should().Be(new ObtenerExtranjerosDelMesQuery(2026, 9));

        var filas = Filas(resultado.Contexto);
        filas.Should().HaveCount(3, "la cabecera y las dos filas del DTO, sin nota cuando todos los Tenants se alcanzan");
        filas[0].Should().HaveCount(12);
        filas[0].Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c), "cada columna tiene su rótulo");
        filas[1][0].Should().Be("Ibertec");
        filas[1][9].Should().Be("Amina Benali");
        filas[1][10].Should().Be("AB1234567");
        filas[1][11].Should().Be("Marta López");
        filas[2][4].Should().Be("Andamios Este S.L.", "la Subcontrata que lo trae va en su columna");
        filas[2][11].Should().BeEmpty("sin Gestor CAE conocido la celda queda vacía, no se inventa");
    }

    [Fact]
    public async Task Con_tenantId_el_libro_solo_lleva_las_filas_de_esa_Empresa_gestionada_y_el_rastro_lo_dice()
    {
        // Amina y Oleg son de Tenants beneficiarios distintos: el detalle de una Empresa no puede llevar filas de la otra.
        var resultado = await ExportarAsync($"?anyo=2026&mes=9&tenantId={Oleg.TenantId}", Mes());

        resultado.Contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        resultado.Mediador.Consultas.Should().BeEmpty("con tenantId el libro sale de la consulta acotada a la vigencia, no del Excel general");
        resultado.Mediador.ConsultasPorEmpresa.Should().ContainSingle().Which
            .Should().Be(new ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery(2026, 9, Oleg.TenantId));
        var filas = Filas(resultado.Contexto);
        filas.Should().HaveCount(2, "la cabecera y la única fila de la Empresa pedida");
        filas[1][9].Should().Be("Oleg Ivanov");
        filas.Select(f => f[9]).Should().NotContain("Amina Benali");

        var llamada = resultado.Registro.Llamadas.Should().ContainSingle().Subject;
        llamada.Filas.Should().Be(1);
        llamada.Criterios.Should().Contain("tenantId", Oleg.TenantId.ToString());
    }

    [Fact]
    public async Task Con_un_tenantId_sin_filas_el_libro_sale_solo_con_la_cabecera_y_nunca_con_las_de_otra_Empresa()
    {
        var resultado = await ExportarAsync($"?anyo=2026&mes=9&tenantId={Guid.NewGuid()}", Mes());

        resultado.Contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        Filas(resultado.Contexto).Should().ContainSingle("solo la cabecera");
        resultado.Registro.Llamadas.Should().ContainSingle().Which.Filas.Should().Be(0);
    }

    [Fact]
    public async Task Sin_tenantId_el_rastro_no_lleva_ese_criterio_y_el_libro_las_lleva_todas()
    {
        var resultado = await ExportarAsync("?anyo=2026&mes=9", Mes());

        Filas(resultado.Contexto).Should().HaveCount(3);
        resultado.Registro.Llamadas.Single().Criterios.Should().NotContainKey("tenantId");
    }

    [Theory]
    [InlineData(CuadroDireccionSinCifras.MesNoValido)]
    [InlineData(CuadroDireccionSinCifras.MesFuturo)]
    public async Task Un_mes_no_valido_o_futuro_responde_400_y_no_deja_rastro_de_exportacion(CuadroDireccionSinCifras motivo)
    {
        var resultado = await ExportarAsync("?anyo=2026&mes=9", Mes(sinCifras: motivo));

        resultado.Contexto.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        resultado.Registro.Llamadas.Should().BeEmpty("no se ha exportado nada");
    }

    [Fact]
    public async Task Registra_la_exportacion_con_la_entidad_Trabajador_las_filas_y_el_mes()
    {
        var resultado = await ExportarAsync("?anyo=2026&mes=9", Mes());

        var llamada = resultado.Registro.Llamadas.Should().ContainSingle().Subject;
        llamada.EntidadTipo.Should().Be("Trabajador");
        llamada.Filas.Should().Be(2);
        llamada.Criterios.Should().Contain("listado", "extranjerosDelMes")
            .And.Contain("anyo", "2026")
            .And.Contain("mes", "9")
            .And.NotContainKey("tenantsSinAlcanceCompleto");
    }

    [Fact]
    public async Task Prohibe_la_cache_del_fichero_porque_lleva_documentos_de_identidad()
    {
        var resultado = await ExportarAsync("?anyo=2026&mes=9", Mes());

        resultado.Contexto.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
        resultado.Contexto.Response.Headers.Pragma.ToString().Should().Be("no-cache");
        resultado.Contexto.Response.ContentType.Should().Contain("spreadsheetml");
    }

    [Fact]
    public async Task Con_Tenants_sin_alcance_completo_el_libro_lo_avisa_al_final_y_el_rastro_cuenta_solo_Trabajadores()
    {
        var resultado = await ExportarAsync("?anyo=2026&mes=9", Mes(sinAlcance: 3));

        var filas = Filas(resultado.Contexto);
        filas.Should().HaveCount(4, "cabecera, dos filas y la nota (la línea en blanco no cuenta como usada)");
        filas[^1][0].Should().Contain("3").And.NotBeNullOrWhiteSpace();

        var llamada = resultado.Registro.Llamadas.Should().ContainSingle().Subject;
        llamada.Filas.Should().Be(2, "la línea en blanco y la nota no son Trabajadores exportados");
        llamada.Criterios.Should().Contain("tenantsSinAlcanceCompleto", "3");
    }

    [Theory]
    [InlineData(Roles.DireccionCae, true)]
    [InlineData(Roles.CoordinadorCae, true)]
    [InlineData(Roles.GestorCae, false)]
    [InlineData(Roles.Consulta, false)]
    [InlineData(Roles.Cliente, false)]
    public async Task La_politica_del_endpoint_solo_deja_pasar_a_los_roles_de_lectura_del_Cuadro(string rol, bool permitido)
    {
        // El 403 de un Gestor CAE o de un Usuario de Cliente lo da esta política antes de llegar a la consulta: la
        // UnauthorizedAccessException de AutorizacionCuadroDireccion queda como segunda puerta y no se convierte en 500.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IMediator>(new MediatorDeExtranjeros(Mes()));
        builder.Services.AddSingleton<IRegistroExportacionService>(new RegistroFalso());
        builder.Services.AddLocalization();
        builder.Services.AddAuthorizationCore();
        builder.Services.AddLogging();
        await using var app = builder.Build();
        app.MapCuadroDireccionEndpoints();
        var politica = Endpoint(app).Metadata.GetOrderedMetadata<AuthorizationPolicy>().Should().ContainSingle().Subject;

        var usuario = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "Prueba"));
        var autorizacion = await app.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(usuario, politica);

        autorizacion.Succeeded.Should().Be(permitido);
    }

    private sealed record LlamadaRegistro(string EntidadTipo, int Filas, IReadOnlyDictionary<string, string> Criterios);

    private sealed class RegistroFalso : IRegistroExportacionService
    {
        public List<LlamadaRegistro> Llamadas { get; } = [];

        public Task RegistrarAsync(
            string entidadTipo, int filas, IReadOnlyDictionary<string, string> criterios,
            CancellationToken cancellationToken = default)
        {
            Llamadas.Add(new LlamadaRegistro(entidadTipo, filas, criterios));
            return Task.CompletedTask;
        }
    }

    private sealed class MediatorDeExtranjeros(ExtranjerosDelMesDto respuesta) : IMediator
    {
        public List<ObtenerExtranjerosDelMesQuery> Consultas { get; } = [];
        public List<ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery> ConsultasPorEmpresa { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerExtranjerosDelMesQuery consulta:
                    Consultas.Add(consulta);
                    return Task.FromResult((TResponse)(object)respuesta);
                case ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery porEmpresa:
                    // El handler real acota a la Empresa pedida y a su vigencia (lo prueban FacturacionOperadorCaeTests y los de
                    // integración): aquí el doble solo entrega las filas de esa Empresa.
                    ConsultasPorEmpresa.Add(porEmpresa);
                    return Task.FromResult((TResponse)(object)(respuesta with
                    {
                        Filas = respuesta.Filas.Where(f => porEmpresa.TenantId is not { } t || f.TenantId == t).ToList(),
                    }));
                default:
                    throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.");
            }
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
