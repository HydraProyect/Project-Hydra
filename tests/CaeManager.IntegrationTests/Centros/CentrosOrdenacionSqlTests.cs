using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Centros;

/// <summary>
/// El camino sin filtro/orden por Estado de ObtenerCentrosQuery ordena y
/// pagina en SQL sobre una proyección construida con <c>new FilaCentro(...)</c>
/// — a diferencia del resto de listados (ver ObtenerClientesQuery), que
/// ordenan las entidades del join y proyectan al DTO recién al final. EF Core
/// no traduce un <c>OrderBy</c> sobre una propiedad de un tipo construido por
/// constructor: /centros se caía con "No pudimos cargar los centros" salvo
/// cuando se filtraba/ordenaba por Estado (ese camino materializa antes de
/// ordenar). Solo Postgres reproduce el fallo — el proveedor InMemory no
/// valida traducción a SQL.
/// </summary>
public class CentrosOrdenacionSqlTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var clienteAlfa = new Cliente("Alfa Cliente S.L.", "B10000016", esCritico: false);
        var clienteZeta = new Cliente("Zeta Cliente S.L.", "B10000024", esCritico: false);
        var empresaAlfa = new Empresa("Alfa Empresa S.L.", "B20000014");
        var empresaZeta = new Empresa("Zeta Empresa S.L.", "B20000022");
        contexto.Clientes.AddRange(clienteAlfa, clienteZeta);
        contexto.Empresas.AddRange(empresaAlfa, empresaZeta);
        await contexto.SaveChangesAsync();

        // Nombre y CodigoCentro deliberadamente en orden inverso entre sí, y
        // cruzados con el cliente/empresa a los que pertenece cada centro,
        // para que ordenar por una columna no pueda "acertar" por coincidir
        // con el orden de otra ni con el de inserción.
        contexto.Centros.AddRange(
            new Centro(clienteZeta.Id, empresaAlfa.Id, "Centro Beta", "300"),
            new Centro(clienteAlfa.Id, empresaZeta.Id, "Centro Alfa", "100"));

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_camino_sin_filtro_de_Estado_no_lanza_al_ordenar_en_SQL()
    {
        var resultado = await ObtenerAsync(nameof(CentroListaDto.Nombre), descendente: false);

        resultado.TotalElementos.Should().Be(2);
        resultado.Elementos.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(nameof(CentroListaDto.Nombre))]
    [InlineData(nameof(CentroListaDto.CodigoCentro))]
    [InlineData(nameof(CentroListaDto.ClienteRazonSocial))]
    [InlineData(nameof(CentroListaDto.EmpresaRazonSocial))]
    public async Task Cada_columna_ordenable_ordena_de_verdad_en_los_dos_sentidos(string columna)
    {
        var ascendente = await ObtenerAsync(columna, descendente: false);
        var descendente = await ObtenerAsync(columna, descendente: true);

        ClaveDe(ascendente.Elementos, columna).Should().BeInAscendingOrder();
        ClaveDe(descendente.Elementos, columna).Should().BeInDescendingOrder();
    }

    private static IEnumerable<IComparable?> ClaveDe(IEnumerable<CentroListaDto> elementos, string columna) =>
        columna switch
        {
            nameof(CentroListaDto.Nombre) => elementos.Select(c => (IComparable?)c.Nombre),
            nameof(CentroListaDto.CodigoCentro) => elementos.Select(c => (IComparable?)c.CodigoCentro),
            nameof(CentroListaDto.ClienteRazonSocial) => elementos.Select(c => (IComparable?)c.ClienteRazonSocial),
            nameof(CentroListaDto.EmpresaRazonSocial) => elementos.Select(c => (IComparable?)c.EmpresaRazonSocial),
            _ => throw new ArgumentOutOfRangeException(nameof(columna), columna, "Columna no cubierta por el test.")
        };

    private async Task<ResultadoPaginado<CentroListaDto>> ObtenerAsync(string? columna, bool descendente)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerCentrosQueryHandler(
            contexto, contexto, contexto, new AlcanceDatosServiceFalso(),
            new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto, contexto));

        return await handler.Handle(
            new ObtenerCentrosQuery(null, null, Estado: null, OrdenarPor: columna, Descendente: descendente),
            CancellationToken.None);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
