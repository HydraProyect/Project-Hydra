using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Dashboard.TarifasOperadorCae;
using CaeManager.Domain.Dashboard;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Facturacion;
using CaeManager.Web.Features.Facturacion.Recursos;
using ClosedXML.Excel;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// <c>GET /facturacion/cuota-operador.xlsx</c> ejecutado de verdad (el endpoint registrado, con su binding de la cadena de
/// consulta): las once columnas y una fila por Empresa gestionada con el Total del mes, 400 para un mes no válido o
/// futuro, sin importes para el Coordinador CAE, 403 si una consulta lanza <see cref="UnauthorizedAccessException"/>, el
/// rastro de la descarga (lleva lo que el Operador CAE cobra) y la política de roles. El importe lo calcula el Cuadro
/// (aquí el doble del mediador devuelve las líneas ya valoradas); lo que se ata es qué se escribe y quién puede bajarlo.
/// </summary>
public class FacturacionCuotaOperadorEndpointTests
{
    private static readonly Guid Ebro = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid Reformas = Guid.Parse("e0000000-0000-0000-0000-000000000002");
    private static readonly DateTime Ahora = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Desde = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Escenario
    {
        /// <summary>Null como cuando quien mira no gestiona las tarifas del Operador (Coordinador CAE).</summary>
        public IReadOnlyList<TarifaOperadorCaeDto>? Tarifas { get; set; } =
        [
            new(Ebro, ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 40m, "EUR", true, Desde),
            new(Ebro, ConceptoTarifaOperadorCae.CentroNuevo, 25m, "EUR", true, Desde),
            new(Ebro, ConceptoTarifaOperadorCae.ObraNueva, 120m, "EUR", true, Desde),
        ];

        public int SinAlcance { get; set; }
        public bool Prohibido { get; set; }
        public IReadOnlyList<Guid> Operadas { get; set; } = [Ebro];
    }

    private sealed record LlamadaRegistro(string EntidadTipo, int Filas, IReadOnlyDictionary<string, string> Criterios);

    private sealed class RegistroFalso : IRegistroExportacionService
    {
        public List<LlamadaRegistro> Llamadas { get; } = [];

        public Task RegistrarAsync(
            string entidadTipo, int filas, IReadOnlyDictionary<string, string> criterios, CancellationToken cancellationToken = default)
        {
            Llamadas.Add(new LlamadaRegistro(entidadTipo, filas, criterios));
            return Task.CompletedTask;
        }
    }

    private sealed record Resultado(HttpContext Contexto, MediadorDeCuota Mediador, RegistroFalso Registro);

    private static WebApplication Aplicacion(MediadorDeCuota mediador, RegistroFalso registro)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IMediator>(mediador);
        builder.Services.AddSingleton<IRegistroExportacionService>(registro);
        builder.Services.AddSingleton<TimeProvider>(new RelojFijo(Ahora));
        builder.Services.AddLocalization();
        builder.Services.AddAuthorizationCore();
        builder.Services.AddLogging();
        var app = builder.Build();
        app.MapFacturacionEndpoints();
        return app;
    }

    private static RouteEndpoint Endpoint(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == FacturacionEndpoints.RutaCuotaDelOperador);

    private static async Task<Resultado> DescargarAsync(string consulta, Escenario? escenario = null)
    {
        var mediador = new MediadorDeCuota(escenario ?? new Escenario());
        var registro = new RegistroFalso();
        await using var app = Aplicacion(mediador, registro);

        using var scope = app.Services.CreateScope();
        var contexto = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        contexto.Request.QueryString = new QueryString(consulta);
        contexto.Response.Body = new MemoryStream();
        await Endpoint(app).RequestDelegate!(contexto);
        return new Resultado(contexto, mediador, registro);
    }

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

    [Fact]
    public async Task Devuelve_las_once_columnas_una_fila_por_Empresa_gestionada_y_el_total_del_mes()
    {
        var resultado = await DescargarAsync("?anyo=2026&mes=10");

        resultado.Contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        resultado.Contexto.Response.ContentType.Should().Contain("spreadsheetml");
        resultado.Mediador.Enviadas.OfType<ObtenerSerieCuadroDireccionQuery>().Should().ContainSingle()
            .Which.Should().Be(new ObtenerSerieCuadroDireccionQuery(2026, 10, 1, ConFacturacion: true));
        resultado.Mediador.Enviadas.OfType<ObtenerEmpresasOperadasEnElMesQuery>().Should().ContainSingle()
            .Which.Should().Be(new ObtenerEmpresasOperadasEnElMesQuery(2026, 10));

        var filas = Filas(resultado.Contexto);
        filas.Should().HaveCount(3, "cabecera, una Empresa y el total del mes");
        filas[0].Should().HaveCount(11).And.OnlyContain(c => !string.IsNullOrWhiteSpace(c));
        filas[1][0].Should().Be("Obras y Estructuras del Ebro S.L.");
        filas[1][1].Should().Be("Octubre 2026");
        filas[1][3].Should().Be("14");
        filas[1][4].Should().Be("560");
        filas[1][5].Should().Be("2");
        filas[1][6].Should().Be("50");
        filas[1][7].Should().Be("1");
        filas[1][8].Should().Be("120");
        filas[1][9].Should().Be("730");
        filas[1][10].Should().Be("EUR");
        filas[2][0].Should().Be("Total del mes");
        filas[2][9].Should().Be("730");
        filas[2][10].Should().Be("EUR");
    }

    [Theory]
    [InlineData("?anyo=2026&mes=11", "mes futuro")]
    [InlineData("?anyo=2027&mes=1", "año futuro")]
    [InlineData("?anyo=2026&mes=13", "mes no válido")]
    [InlineData("?anyo=2026&mes=0", "mes cero")]
    [InlineData("?anyo=1900&mes=1", "año anterior al mínimo")]
    public async Task Un_mes_futuro_o_no_valido_responde_400_sin_consultar_nada_ni_dejar_rastro(string consulta, string motivo)
    {
        var resultado = await DescargarAsync(consulta);

        resultado.Contexto.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest, motivo);
        resultado.Mediador.Enviadas.Should().BeEmpty();
        resultado.Registro.Llamadas.Should().BeEmpty("no se ha exportado nada");
    }

    [Fact]
    public async Task El_Coordinador_CAE_baja_el_libro_sin_importes_ni_total_y_el_rastro_lo_dice()
    {
        var resultado = await DescargarAsync("?anyo=2026&mes=10", new Escenario { Tarifas = null });

        var filas = Filas(resultado.Contexto);
        filas.Should().HaveCount(2, "cabecera y la Empresa: sin fila de total");
        filas[1][3].Should().Be("14", "las unidades se enseñan");
        foreach (var columnaImporte in new[] { 4, 6, 8, 9, 10 })
            filas[1][columnaImporte].Should().BeEmpty("quien no gestiona las tarifas no ve importes ni moneda");
        filas.SelectMany(f => f).Should().NotContain("Total del mes");

        resultado.Registro.Llamadas.Should().ContainSingle().Which.Criterios.Should().Contain("importesOcultos", "true");
    }

    [Fact]
    public async Task Una_UnauthorizedAccessException_de_las_consultas_es_un_403_y_no_un_500_ni_deja_rastro()
    {
        var mediador = new MediadorDeCuota(new Escenario { Prohibido = true });
        var registro = new RegistroFalso();
        await using var app = Aplicacion(mediador, registro);
        using var scope = app.Services.CreateScope();

        var resultado = await FacturacionEndpoints.ExportarCuotaDelOperadorAsync(
            2026, 10, new DefaultHttpContext { RequestServices = scope.ServiceProvider }, mediador, new RelojFijo(Ahora), registro,
            scope.ServiceProvider.GetRequiredService<IStringLocalizer<TextosFacturacion>>(), CancellationToken.None);

        resultado.Should().BeOfType<ForbidHttpResult>();
        registro.Llamadas.Should().BeEmpty("no se ha exportado nada");
    }

    [Fact]
    public async Task Registra_la_descarga_con_la_entidad_las_filas_de_Empresa_y_el_mes()
    {
        var resultado = await DescargarAsync("?anyo=2026&mes=10");

        var llamada = resultado.Registro.Llamadas.Should().ContainSingle().Subject;
        llamada.EntidadTipo.Should().Be("TarifaOperadorCae");
        llamada.Filas.Should().Be(1, "cuenta Empresas gestionadas, no la fila de total");
        llamada.Criterios.Should().Contain("listado", "cuotaOperador")
            .And.Contain("anyo", "2026")
            .And.Contain("mes", "10")
            .And.NotContainKey("importesOcultos")
            .And.NotContainKey("tenantsSinAlcanceCompleto");
    }

    [Fact]
    public async Task Prohibe_la_cache_del_fichero_porque_lleva_lo_que_el_Operador_cobra()
    {
        var resultado = await DescargarAsync("?anyo=2026&mes=10");

        resultado.Contexto.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
        resultado.Contexto.Response.Headers.Pragma.ToString().Should().Be("no-cache");
    }

    [Fact]
    public async Task Con_alcance_parcial_el_total_y_el_libro_lo_dicen_y_el_rastro_lo_anota()
    {
        var resultado = await DescargarAsync("?anyo=2026&mes=10", new Escenario { SinAlcance = 2 });

        var filas = Filas(resultado.Contexto);
        filas.Should().HaveCount(4, "cabecera, Empresa, total y la nota (la línea en blanco no cuenta como usada)");
        filas[2][0].Should().Contain("parcial", "el total no puede pasar por completo si faltan Empresas");
        filas[^1][0].Should().Contain("2").And.Contain("no alcanzas por completo");

        var llamada = resultado.Registro.Llamadas.Should().ContainSingle().Subject;
        llamada.Filas.Should().Be(1, "ni el total ni la nota son Empresas exportadas");
        llamada.Criterios.Should().Contain("tenantsSinAlcanceCompleto", "2");
    }

    [Fact]
    public async Task Solo_baja_las_Empresas_que_el_Operador_opera_en_el_mes()
    {
        var resultado = await DescargarAsync("?anyo=2026&mes=10", new Escenario { Operadas = [Reformas] });

        var filas = Filas(resultado.Contexto);
        filas.Should().HaveCount(1, "ninguna de las Empresas de la serie está operada: solo la cabecera");
        resultado.Registro.Llamadas.Should().ContainSingle().Which.Filas.Should().Be(0);
    }

    [Theory]
    [InlineData(Roles.Administrador, true)]
    [InlineData(Roles.DireccionCae, true)]
    [InlineData(Roles.CoordinadorCae, true)]
    [InlineData(Roles.GestorCae, false)]
    [InlineData(Roles.Consulta, false)]
    [InlineData(Roles.Cliente, false)]
    public async Task La_politica_del_endpoint_deja_pasar_a_los_mismos_roles_que_la_pagina(string rol, bool permitido)
    {
        // El 403 de un Gestor CAE o de un Usuario de Cliente lo da esta política antes de llegar a las consultas; las
        // consultas vuelven a exigir su propia autorización (el endpoint no decide quién ve importes).
        await using var app = Aplicacion(new MediadorDeCuota(new Escenario()), new RegistroFalso());
        var politica = Endpoint(app).Metadata.GetOrderedMetadata<AuthorizationPolicy>().Should().ContainSingle().Subject;

        var usuario = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "Prueba"));
        var autorizacion = await app.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(usuario, politica);

        autorizacion.Succeeded.Should().Be(permitido);
    }

    private sealed class RelojFijo(DateTime ahoraUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahoraUtc, TimeSpan.Zero);
    }

    private sealed class MediadorDeCuota(Escenario escenario) : IMediator
    {
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (escenario.Prohibido)
                throw new UnauthorizedAccessException();

            object? respuesta = request switch
            {
                ObtenerSerieCuadroDireccionQuery q => Serie(q),
                ContarExtranjerosCobrablesDelMesQuery q => new ConteoExtranjerosCobrablesDelMesDto(
                    q.Anyo, q.Mes, [new ConteoExtranjerosCobrablesTenantDto(Ebro, "Ebro", 14, 12)], 0),
                ObtenerTarifasOperadorCaeQuery => escenario.Tarifas,
                ObtenerEmpresasOperadasEnElMesQuery => escenario.Operadas,
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}."),
            };
            return Task.FromResult((TResponse)respuesta!);
        }

        private SerieCuadroDireccionDto Serie(ObtenerSerieCuadroDireccionQuery q)
        {
            static CifraMensual C(int valor) => new(valor, EstadoCifraMensual.Parcial);
            var cifras = new CifrasCuadroDireccionDto(
                C(0), C(0), C(0), C(0), C(2), C(0), C(0), new SolicitudesDelMesDto(C(0), []),
                new CumplimientoActualDto(0, 0, 0, 0, 0, 0, 0, 0, true, Ahora), Gestiones: C(212), ObrasNuevas: C(1));
            IReadOnlyList<LineaFacturacionCuadroDto>? lineas = escenario.Tarifas is null
                ? null
                :
                [
                    new(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, "Visita de Trabajador extranjero", "EUR", 14, 560m),
                    new(ConceptoTarifaOperadorCae.CentroNuevo, "Centro nuevo", "EUR", 2, 50m),
                    new(ConceptoTarifaOperadorCae.ObraNueva, "Obra nueva", "EUR", 1, 120m),
                ];
            var punto = new PuntoSerieCuadroDto(q.Anyo, q.Mes, EstadoCifraMensual.Parcial, cifras, lineas);
            return new SerieCuadroDireccionDto(
                [punto with { Facturacion = null }], [new SerieTenantCuadroDto(Ebro, "Obras y Estructuras del Ebro S.L.", [punto])], escenario.SinAlcance);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
