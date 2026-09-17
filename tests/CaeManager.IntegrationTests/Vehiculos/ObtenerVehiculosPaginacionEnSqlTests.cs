using CaeManager.Application.Documentos;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Vehiculos;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Vehiculos;

/// <summary>
/// Mismo hallazgo que <c>ObtenerTrabajadoresPaginacionEnSqlTests</c> (Módulo 8,
/// PR #389 § 4.1) para <see cref="ObtenerVehiculosQueryHandler"/>.
/// </summary>
public class ObtenerVehiculosPaginacionEnSqlTests : IAsyncLifetime
{
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;
    private const int VehiculosVencidos = 4;
    private const int VehiculosVigentes = 2;

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = DateOnly.FromDateTime(DateTime.UtcNow);

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarDias, UmbralRojoDias));

        var empresa = new Empresa("Flota Delta S.L.");
        contexto.Empresas.Add(empresa);

        var tipo = new TipoDocumento("ITV", 12, true, 1, AmbitoAplicacion.Vehiculo, RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        var vencidos = Enumerable.Range(1, VehiculosVencidos)
            .Select(i => Vehiculo.DeEmpresa(empresa.Id, $"Vencido{i}", "Modelo", $"{i:0000}VEN"))
            .ToList();
        var vigentes = Enumerable.Range(1, VehiculosVigentes)
            .Select(i => Vehiculo.DeEmpresa(empresa.Id, $"Vigente{i}", "Modelo", $"{i:0000}VIG"))
            .ToList();

        contexto.Vehiculos.AddRange(vencidos);
        contexto.Vehiculos.AddRange(vigentes);
        await contexto.SaveChangesAsync();

        foreach (var vehiculo in vencidos)
            contexto.Documentos.Add(Documento.DeVehiculo(vehiculo.Id, tipo.Id, _hoy.AddDays(-400), _hoy.AddDays(-1)));

        foreach (var vehiculo in vigentes)
            contexto.Documentos.Add(Documento.DeVehiculo(vehiculo.Id, tipo.Id, _hoy.AddDays(-10), _hoy.AddDays(UmbralAmbarDias + 60)));

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Con_filtro_de_estado_la_pagina_y_el_total_se_calculan_en_SQL_no_en_memoria()
    {
        var sqlCapturado = new List<string>();
        await using var contexto = CrearContexto(sqlCapturado.Add);
        var handler = new ObtenerVehiculosQueryHandler(
            contexto, contexto, new AlcanceDatosServiceFalso(), contexto, contexto,
            new CalculoEstadoDocumentalService(contexto, contexto));

        var resultado = await handler.Handle(
            new ObtenerVehiculosQuery(
                null, Pagina: 1, TamanoPagina: 2, EstadoDocumental: nameof(EstadoDocumento.Vencido)),
            CancellationToken.None);

        resultado.TotalElementos.Should().Be(VehiculosVencidos);
        resultado.Elementos.Should().HaveCount(2);
        resultado.Elementos.Should().OnlyContain(v => v.EstadoDocumental == EstadoDocumento.Vencido);

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
    public async Task Un_estado_valido_pero_no_aplicable_no_devuelve_ningun_vehiculo()
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerVehiculosQueryHandler(
            contexto, contexto, new AlcanceDatosServiceFalso(), contexto, contexto,
            new CalculoEstadoDocumentalService(contexto, contexto));

        var resultado = await handler.Handle(
            new ObtenerVehiculosQuery(null, Pagina: 1, TamanoPagina: 50, EstadoDocumental: nameof(EstadoDocumento.Faltante)),
            CancellationToken.None);

        resultado.TotalElementos.Should().Be(0);
        resultado.Elementos.Should().BeEmpty();
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
