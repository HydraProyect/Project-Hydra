using CaeManager.Application.Centros;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Empresas;

/// <summary>
/// Mismo hallazgo que <c>ObtenerTrabajadoresPaginacionEnSqlTests</c> (Módulo 8,
/// PR #389 § 4.1) para <see cref="ObtenerEmpresasQueryHandler"/>: con un
/// filtro o un orden por <c>EstadoDocumental</c>, materializaba todas las
/// Empresas visibles para calcular el peor vencimiento en memoria. Ahora se
/// pide a SQL con una subconsulta correlacionada (MIN por propietario), y el
/// filtro, el orden y la paginación se resuelven ahí.
/// </summary>
public class ObtenerEmpresasPaginacionEnSqlTests : IAsyncLifetime
{
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;
    private const int EmpresasVencidas = 4;
    private const int EmpresasVigentes = 2;

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = DateOnly.FromDateTime(DateTime.UtcNow);

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarDias, UmbralRojoDias));

        var vencidas = Enumerable.Range(1, EmpresasVencidas)
            .Select(i => new Empresa($"Vencida {i} S.L."))
            .ToList();
        var vigentes = Enumerable.Range(1, EmpresasVigentes)
            .Select(i => new Empresa($"Vigente {i} S.L."))
            .ToList();

        var tipo = new TipoDocumento("Seguro RC", 12, true, 1, AmbitoAplicacion.Empresa, RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        contexto.Empresas.AddRange(vencidas);
        contexto.Empresas.AddRange(vigentes);
        await contexto.SaveChangesAsync();

        foreach (var empresa in vencidas)
            contexto.Documentos.Add(Documento.DeEmpresa(empresa.Id, tipo.Id, _hoy.AddDays(-400), _hoy.AddDays(-1)));

        foreach (var empresa in vigentes)
            contexto.Documentos.Add(Documento.DeEmpresa(empresa.Id, tipo.Id, _hoy.AddDays(-10), _hoy.AddDays(UmbralAmbarDias + 60)));

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Con_filtro_de_estado_la_pagina_y_el_total_se_calculan_en_SQL_no_en_memoria()
    {
        var sqlCapturado = new List<string>();
        await using var contexto = CrearContexto(sqlCapturado.Add);
        var handler = CrearHandler(contexto);

        var resultado = await handler.Handle(
            new ObtenerEmpresasQuery(
                null, Pagina: 1, TamanoPagina: 2, EstadoDocumental: nameof(EstadoDocumento.Vencido)),
            CancellationToken.None);

        resultado.TotalElementos.Should().Be(EmpresasVencidas);
        resultado.Elementos.Should().HaveCount(2);
        resultado.Elementos.Should().OnlyContain(e => e.EstadoDocumental == EstadoDocumento.Vencido);

        var sentenciasSelect = sqlCapturado.Where(s => s.Contains("SELECT", StringComparison.OrdinalIgnoreCase)).ToList();
        sentenciasSelect.Should().Contain(
            s => s.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) && s.Contains("OFFSET", StringComparison.OrdinalIgnoreCase),
            "la página debe recortarse en SQL, no con Skip/Take sobre una lista ya materializada");
        sentenciasSelect.Should().Contain(
            s => s.Contains("COUNT", StringComparison.OrdinalIgnoreCase),
            "el total debe salir de un agregado en SQL, no de List.Count sobre lo materializado");
    }

    /// <summary>Mismo hallazgo de Codex que ObtenerTrabajadoresPaginacionEnSqlTests.</summary>
    [Fact]
    public async Task Un_estado_valido_pero_no_aplicable_no_devuelve_ninguna_empresa()
    {
        await using var contexto = CrearContexto();
        var resultado = await CrearHandler(contexto).Handle(
            new ObtenerEmpresasQuery(null, Pagina: 1, TamanoPagina: 50, EstadoDocumental: nameof(EstadoDocumento.Faltante)),
            CancellationToken.None);

        resultado.TotalElementos.Should().Be(0);
        resultado.Elementos.Should().BeEmpty();
    }

    private static ObtenerEmpresasQueryHandler CrearHandler(CaeManagerDbContext contexto) =>
        new(contexto, new AlcanceDatosServiceFalso(),
            new CalculoEstadoDocumentalService(contexto, contexto),
            contexto, contexto, contexto, contexto,
            new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto));

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
