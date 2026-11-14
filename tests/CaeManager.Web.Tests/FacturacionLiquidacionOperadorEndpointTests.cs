using System.Security.Claims;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Dashboard.TarifasOperadorCae;
using CaeManager.Application.Facturacion.LiquidacionesOperadorCae;
using CaeManager.Domain.Dashboard;
using CaeManager.Domain.Facturacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Facturacion;
using CaeManager.Web.Features.Facturacion.Recursos;
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
/// <c>GET /facturacion/liquidacion-operador.csv</c> ejecutado de verdad (el endpoint registrado, con su binding de la cadena
/// de consulta): la liquidación interna del Operador CAE externo para importarla en su programa de facturación (V-2). Se
/// ata que no es una factura (cada fila dice «sin valor fiscal» y no hay número de factura), el formato importable (BOM,
/// punto y coma, coma decimal), la marca de cobro, la neutralización de fórmulas, que solo sale un mes terminado y solo
/// para quien ve importes, el rastro de la descarga y la política de roles (D-F9: el Coordinador CAE no la baja).
/// </summary>
public class FacturacionLiquidacionOperadorEndpointTests
{
    private static readonly Guid Ebro = Guid.Parse("e0000000-0000-0000-0000-000000000001");
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

        public string NombreEmpresa { get; set; } = "Obras y Estructuras del Ebro S.L.";
        public CobrosLiquidacionOperadorCaeDto Cobros { get; set; } = CobrosLiquidacionOperadorCaeDto.Ninguno;
        public bool Prohibido { get; set; }
        public int TenantsSinAlcanceCompleto { get; set; }
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

    private sealed record Resultado(HttpContext Contexto, Mediador Mediador, RegistroFalso Registro);

    private static WebApplication Aplicacion(Mediador mediador, RegistroFalso registro)
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
            .Single(e => e.RoutePattern.RawText == FacturacionEndpoints.RutaLiquidacionDelOperador);

    private static async Task<Resultado> DescargarAsync(string consulta, Escenario? escenario = null)
    {
        var mediador = new Mediador(escenario ?? new Escenario());
        var registro = new RegistroFalso();
        await using var app = Aplicacion(mediador, registro);

        using var scope = app.Services.CreateScope();
        var contexto = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        contexto.Request.QueryString = new QueryString(consulta);
        contexto.Response.Body = new MemoryStream();
        await Endpoint(app).RequestDelegate!(contexto);
        return new Resultado(contexto, mediador, registro);
    }

    private static byte[] Bytes(HttpContext contexto) => ((MemoryStream)contexto.Response.Body).ToArray();

    private static List<string[]> Filas(HttpContext contexto)
    {
        var bytes = Bytes(contexto);
        var texto = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return texto.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(';')).ToList();
    }

    [Fact]
    public async Task Devuelve_una_liquidacion_interna_sin_valor_fiscal_importable_con_una_fila_por_concepto()
    {
        var resultado = await DescargarAsync("?anyo=2026&mes=9");

        resultado.Contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        resultado.Contexto.Response.ContentType.Should().Be(LiquidacionOperadorCsv.TipoContenido);
        resultado.Contexto.Response.Headers.ContentDisposition.ToString().Should().Contain("liquidacion-interna-operador-2026-09.csv")
            .And.NotContainEquivalentOf("factura", "el fichero no se presenta como una factura");
        Bytes(resultado.Contexto).Take(3).Should().Equal(Encoding.UTF8.GetPreamble(), "BOM para que la hoja de cálculo lea los acentos");
        Encoding.UTF8.GetString(Bytes(resultado.Contexto)).Should().Contain("\r\n");

        var filas = Filas(resultado.Contexto);
        filas[0].Should().Equal(
            "Tipo de documento", "Periodo", "Estado de la cifra", "NIF", "Razón social", "Empresa gestionada", "Concepto", "Unidades",
            "Precio unitario", "Base imponible", "Tipo de IVA", "Moneda", "Estado de cobro", "Referencia de tu factura", "Cobertura");
        var datos = filas.Skip(1).ToList();
        datos.Should().HaveCount(3, "una fila por concepto de la Empresa gestionada");
        datos.Should().OnlyContain(f => f.Length == 15 && f[0] == "Liquidación interna sin valor fiscal" && f[1] == "2026-09"
            && f[5] == "Obras y Estructuras del Ebro S.L." && f[12] == "Pendiente" && f[13] == "" && f[14] == "Completa");
        datos.Should().OnlyContain(f => f[3] == "" && f[4] == "" && f[10] == "",
            "NIF, razón social y tipo de IVA no están en el modelo: van vacíos, no inventados");
        Encoding.UTF8.GetString(Bytes(resultado.Contexto)).Should().NotContainEquivalentOf("factura n",
            "no hay número de factura: es un documento interno del emisor");

        var extranjeros = datos.Single(f => f[6] == "Extranjeros gestionados");
        extranjeros[7].Should().Be("14");
        extranjeros[8].Should().Be("40,00", "coma decimal sin separador de miles");
        extranjeros[9].Should().Be("560,00");
        extranjeros[11].Should().Be("EUR");
        datos.Single(f => f[6] == "Obras nuevas")[9].Should().Be("120,00");
    }

    [Fact]
    public async Task Con_alcance_parcial_cada_fila_dice_que_faltan_Empresas()
    {
        var resultado = await DescargarAsync("?anyo=2026&mes=9", new Escenario { TenantsSinAlcanceCompleto = 2 });

        Filas(resultado.Contexto).Skip(1).Should().OnlyContain(f => f[14] == "Parcial: faltan 2 empresas que no alcanzas por completo",
            "como la nota del Excel: la liquidación no se importa como completa si faltan Empresas");
    }

    [Fact]
    public async Task Lleva_la_marca_de_cobro_y_la_referencia_de_la_factura_real_del_emisor()
    {
        var escenario = new Escenario
        {
            Cobros = new(true, [new CobroLiquidacionOperadorCaeDto(Ebro, EstadoCobroLiquidacion.Impagada, "F-2026/118", Ahora, Guid.NewGuid())]),
        };

        var filas = Filas((await DescargarAsync("?anyo=2026&mes=9", escenario)).Contexto);

        filas.Skip(1).Should().OnlyContain(f => f[12] == "Impagada" && f[13] == "F-2026/118");
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")")]
    [InlineData("+34 600")]
    [InlineData("-1")]
    [InlineData("@SUM(A1)")]
    public async Task Un_nombre_de_Empresa_que_empieza_como_formula_se_escribe_neutralizado(string nombre)
    {
        var filas = Filas((await DescargarAsync("?anyo=2026&mes=9", new Escenario { NombreEmpresa = nombre })).Contexto);

        filas.Skip(1).Select(f => f[5]).Should().OnlyContain(c => c.TrimStart('"').StartsWith('\''),
            "la hoja de cálculo no debe ejecutarlo como fórmula (ASVS 4.0.3 § 5.3.10, CWE-1236)");
    }

    [Fact]
    public void Un_texto_con_separador_comillas_o_salto_de_linea_va_entrecomillado()
    {
        LiquidacionOperadorCsv.Texto("Ebro; S.L.").Should().Be("\"Ebro; S.L.\"");
        LiquidacionOperadorCsv.Texto("Ebro \"Norte\"").Should().Be("\"Ebro \"\"Norte\"\"\"");
        LiquidacionOperadorCsv.Texto("Ebro\nNorte").Should().Be("\"Ebro\nNorte\"");
        LiquidacionOperadorCsv.Texto("=1;2").Should().Be("\"'=1;2\"");
        LiquidacionOperadorCsv.Texto("\tcmd").Should().Be("'\tcmd", "un tabulador al principio también se neutraliza");
        LiquidacionOperadorCsv.Texto("\rcmd").Should().Be("\"'\rcmd\"", "y un retorno de carro, que además obliga a entrecomillar");
        LiquidacionOperadorCsv.Texto("Ebro").Should().Be("Ebro", "control: un texto normal sale tal cual");
    }

    [Theory]
    [InlineData("?anyo=2026&mes=10", "mes en curso: la liquidación aún no está cerrada")]
    [InlineData("?anyo=2026&mes=11", "mes futuro")]
    [InlineData("?anyo=2026&mes=13", "mes no válido")]
    [InlineData("?anyo=1900&mes=1", "año anterior al mínimo")]
    public async Task Un_mes_sin_terminar_o_no_valido_responde_400_sin_consultar_nada_ni_dejar_rastro(string consulta, string motivo)
    {
        var resultado = await DescargarAsync(consulta);

        resultado.Contexto.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest, motivo);
        resultado.Mediador.Enviadas.Should().BeEmpty();
        resultado.Registro.Llamadas.Should().BeEmpty("no se ha exportado nada");
    }

    [Fact]
    public async Task Sin_importes_como_el_Coordinador_CAE_es_un_403_y_no_deja_rastro()
    {
        var (resultado, mediador, registro) = await ExportarDirectoAsync(new Escenario { Tarifas = null });

        resultado.Should().BeOfType<ForbidHttpResult>(
            "sin importes el fichero no sirve para facturar, y solo lo bajan el Administrador y la Dirección CAE (D-F9)");
        mediador.Enviadas.OfType<ObtenerCobrosLiquidacionOperadorCaeQuery>().Should().BeEmpty();
        registro.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_UnauthorizedAccessException_de_las_consultas_es_un_403_y_no_deja_rastro()
    {
        var (resultado, _, registro) = await ExportarDirectoAsync(new Escenario { Prohibido = true });

        resultado.Should().BeOfType<ForbidHttpResult>();
        registro.Llamadas.Should().BeEmpty();
    }

    /// <summary>Llama al método del endpoint sin pasar por la tubería: el 403 necesita autenticación para escribirse.</summary>
    private static async Task<(IResult Resultado, Mediador Mediador, RegistroFalso Registro)> ExportarDirectoAsync(Escenario escenario)
    {
        var mediador = new Mediador(escenario);
        var registro = new RegistroFalso();
        await using var app = Aplicacion(mediador, registro);
        using var scope = app.Services.CreateScope();

        var resultado = await FacturacionEndpoints.ExportarLiquidacionDelOperadorAsync(
            2026, 9, new DefaultHttpContext { RequestServices = scope.ServiceProvider }, mediador, new RelojFijo(Ahora), registro,
            scope.ServiceProvider.GetRequiredService<IStringLocalizer<TextosFacturacion>>(), CancellationToken.None);
        return (resultado, mediador, registro);
    }

    [Fact]
    public async Task Registra_la_descarga_y_prohibe_la_cache()
    {
        var resultado = await DescargarAsync("?anyo=2026&mes=9");

        var llamada = resultado.Registro.Llamadas.Should().ContainSingle().Subject;
        llamada.EntidadTipo.Should().Be(nameof(CobroLiquidacionOperadorCae));
        llamada.Filas.Should().Be(1, "cuenta Empresas gestionadas, no filas de concepto");
        llamada.Criterios.Should().Contain("listado", "liquidacionOperador").And.Contain("anyo", "2026").And.Contain("mes", "9");
        resultado.Contexto.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
    }

    [Theory]
    [InlineData(Roles.Administrador, true)]
    [InlineData(Roles.DireccionCae, true)]
    [InlineData(Roles.CoordinadorCae, false)]
    [InlineData(Roles.GestorCae, false)]
    [InlineData(Roles.Consulta, false)]
    [InlineData(Roles.Cliente, false)]
    public async Task La_politica_del_endpoint_solo_deja_pasar_al_Administrador_y_a_la_Direccion_CAE(string rol, bool permitido)
    {
        await using var app = Aplicacion(new Mediador(new Escenario()), new RegistroFalso());
        var politica = Endpoint(app).Metadata.GetOrderedMetadata<AuthorizationPolicy>().Should().ContainSingle().Subject;

        var usuario = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "Prueba"));
        var autorizacion = await app.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(usuario, politica);

        autorizacion.Succeeded.Should().Be(permitido);
    }

    private sealed class RelojFijo(DateTime ahoraUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahoraUtc, TimeSpan.Zero);
    }

    private sealed class Mediador(Escenario escenario) : IMediator
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
                ObtenerEmpresasGestionadasDelMesQuery q => new EmpresasGestionadasDelMesDto(true, new Dictionary<Guid, IReadOnlyList<TramoDeVigencia>>
                {
                    [Ebro] = [new TramoDeVigencia(new DateTime(q.Anyo, q.Mes, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(q.Anyo, q.Mes, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1))],
                }),
                ObtenerCobrosLiquidacionOperadorCaeQuery => escenario.Cobros,
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}."),
            };
            return Task.FromResult((TResponse)respuesta!);
        }

        private SerieCuadroDireccionDto Serie(ObtenerSerieCuadroDireccionQuery q)
        {
            static CifraMensual C(int valor) => new(valor, EstadoCifraMensual.Existe);
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
            var punto = new PuntoSerieCuadroDto(q.Anyo, q.Mes, EstadoCifraMensual.Existe, cifras, lineas);
            return new SerieCuadroDireccionDto(
                [punto with { Facturacion = null }], [new SerieTenantCuadroDto(Ebro, escenario.NombreEmpresa, [punto])],
                escenario.TenantsSinAlcanceCompleto);
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
