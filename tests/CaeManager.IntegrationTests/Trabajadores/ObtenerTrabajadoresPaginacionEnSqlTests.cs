using CaeManager.Application.Documentos;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Trabajadores;

/// <summary>
/// Módulo 8, PR #389 § 4.1: con un filtro o un orden por
/// <c>EstadoDocumental</c>, <see cref="ObtenerTrabajadoresQueryHandler"/>
/// materializaba TODOS los Trabajadores visibles en memoria para poder
/// filtrar/ordenar/paginar — el estado no es una columna, así que antes hacía
/// falta traerlo todo para calcularlo. Ahora el peor vencimiento por
/// Trabajador se pide a Postgres con una subconsulta correlacionada
/// (MIN por propietario) y el filtro, el orden y la paginación se resuelven
/// en SQL, igual que <c>DocumentosPaginacionEnSqlTests</c> hace para
/// Documento. <see cref="OrdenacionTrabajadoresConEstadoDocumentalTests"/> ya
/// fija el resultado; este archivo fija el MECANISMO — que deja de haber una
/// carga completa — capturando el SQL real que EF Core genera.
/// </summary>
public class ObtenerTrabajadoresPaginacionEnSqlTests : IAsyncLifetime
{
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;
    private const int TrabajadoresVencidos = 4;
    private const int TrabajadoresVigentes = 2;

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = DateOnly.FromDateTime(DateTime.UtcNow);

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarDias, UmbralRojoDias));

        var empresa = new Empresa("Gamma Obras S.L.");
        contexto.Empresas.Add(empresa);

        var tipo = new TipoDocumento("Reconocimiento medico", 12, true, 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        var vencidos = Enumerable.Range(1, TrabajadoresVencidos)
            .Select(i => Trabajador.DeEmpresa(empresa.Id, $"Vencido{i}", "Apellido", DniValido(i)))
            .ToList();
        var vigentes = Enumerable.Range(1, TrabajadoresVigentes)
            .Select(i => Trabajador.DeEmpresa(empresa.Id, $"Vigente{i}", "Apellido", DniValido(1000 + i)))
            .ToList();

        contexto.Trabajadores.AddRange(vencidos);
        contexto.Trabajadores.AddRange(vigentes);
        await contexto.SaveChangesAsync();

        foreach (var trabajador in vencidos)
            contexto.Documentos.Add(Documento.DeTrabajador(trabajador.Id, tipo.Id, _hoy.AddDays(-400), _hoy.AddDays(-1)));

        foreach (var trabajador in vigentes)
            contexto.Documentos.Add(Documento.DeTrabajador(trabajador.Id, tipo.Id, _hoy.AddDays(-10), _hoy.AddDays(UmbralAmbarDias + 60)));

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private static string DniValido(int numero)
    {
        const string letras = "TRWAGMYFPDXBNJZSQVHLCKE";
        return $"{numero:00000000}{letras[numero % 23]}";
    }

    [Fact]
    public async Task Con_filtro_de_estado_la_pagina_y_el_total_se_calculan_en_SQL_no_en_memoria()
    {
        var sqlCapturado = new List<string>();
        await using var contexto = CrearContexto(sqlCapturado.Add);
        var handler = new ObtenerTrabajadoresQueryHandler(
            contexto, contexto, contexto, contexto,
            new AlcanceDatosServiceFalso(), new CalculoEstadoDocumentalService(contexto, contexto));

        var resultado = await handler.Handle(
            new ObtenerTrabajadoresQuery(
                null, Pagina: 1, TamanoPagina: 2, EstadoDocumental: nameof(EstadoDocumento.Vencido)),
            CancellationToken.None);

        resultado.TotalElementos.Should().Be(TrabajadoresVencidos, "el total cuenta TODOS los que pasan el filtro, no solo la página");
        resultado.Elementos.Should().HaveCount(2);

        var sentenciasSelect = sqlCapturado.Where(s => s.Contains("SELECT", StringComparison.OrdinalIgnoreCase)).ToList();
        sentenciasSelect.Should().NotBeEmpty("control positivo: el handler sí debe haber ejecutado alguna consulta");

        // La firma de "se pagina en SQL": si volviera a materializar todos los
        // visibles para paginar en memoria (Skip/Take de LINQ to Objects), el
        // SQL real nunca llevaría LIMIT/OFFSET para la consulta de la página.
        sentenciasSelect.Should().Contain(
            s => s.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) && s.Contains("OFFSET", StringComparison.OrdinalIgnoreCase),
            "la página debe recortarse en SQL (LIMIT/OFFSET), no con Skip/Take de LINQ to Objects sobre una lista ya materializada");

        // El total tampoco puede salir de una lista materializada: tiene que
        // ser un COUNT/EXISTS agregado que Postgres resuelve sin traer filas.
        sentenciasSelect.Should().Contain(
            s => s.Contains("COUNT", StringComparison.OrdinalIgnoreCase),
            "el total debe salir de un agregado en SQL, no de List.Count sobre lo materializado");
    }

    [Fact]
    public async Task Ordenar_por_estado_documental_tambien_pagina_en_SQL()
    {
        var sqlCapturado = new List<string>();
        await using var contexto = CrearContexto(sqlCapturado.Add);
        var handler = new ObtenerTrabajadoresQueryHandler(
            contexto, contexto, contexto, contexto,
            new AlcanceDatosServiceFalso(), new CalculoEstadoDocumentalService(contexto, contexto));

        var resultado = await handler.Handle(
            new ObtenerTrabajadoresQuery(
                null, Pagina: 1, TamanoPagina: 3,
                OrdenarPor: nameof(TrabajadorListaDto.EstadoDocumental), Descendente: false),
            CancellationToken.None);

        resultado.TotalElementos.Should().Be(TrabajadoresVencidos + TrabajadoresVigentes);
        resultado.Elementos.Should().HaveCount(3);
        resultado.Elementos.Should().OnlyContain(t => t.EstadoDocumental == EstadoDocumento.Vencido,
            "los 3 primeros por urgencia son los 4 Vencidos, nunca los Vigentes");

        var sentenciasSelect = sqlCapturado.Where(s => s.Contains("SELECT", StringComparison.OrdinalIgnoreCase)).ToList();
        sentenciasSelect.Should().Contain(
            s => s.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) && s.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    private CaeManagerDbContext CrearContexto(Action<string>? capturarSql = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var builder = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual));

        if (capturarSql is not null)
            builder = builder.LogTo(capturarSql, Microsoft.Extensions.Logging.LogLevel.Information);

        return new CaeManagerDbContext(builder.Options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
