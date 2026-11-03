using CaeManager.Application.Auditoria.Queries;
using CaeManager.Domain.Auditoria;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// M-21: <c>RegistrosAuditoria</c> está particionada por <c>FechaUtc</c>, así que la consulta
/// acepta una cota (<c>DesdeUtc</c> inclusiva, <c>HastaUtc</c> exclusiva). Sin cotas no oculta
/// nada: la pestaña Historial y la exportación sin parámetros siguen viendo todo el historial.
/// Observa el recuento y las filas contra PostgreSQL real; NO observa el plan de ejecución
/// (que la cota pode particiones).
/// </summary>
public class ObtenerAuditoriaQueryRangoFechasTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly string _tipo = "PruebaRango" + Guid.NewGuid().ToString("N");
    private static readonly DateTime Corte = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        await InsertarAsync(Corte.AddDays(-30)); // antiguo
        await InsertarAsync(Corte);              // justo en la cota inferior
        await InsertarAsync(Corte.AddDays(10));  // dentro
        await InsertarAsync(Corte.AddDays(20));  // justo en la cota superior (exclusiva)
        await InsertarAsync(Corte.AddDays(40));  // posterior
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Sin_cotas_devuelve_todo_el_historial()
    {
        var resultado = await ConsultarAsync(desde: null, hasta: null);

        resultado.TotalElementos.Should().Be(5, "sin rango la consulta no oculta registros a ningún lector");
    }

    [Fact]
    public async Task Desde_es_inclusivo_y_deja_fuera_lo_anterior()
    {
        var resultado = await ConsultarAsync(desde: Corte, hasta: null);

        resultado.TotalElementos.Should().Be(4);
        resultado.Elementos.Should().OnlyContain(r => r.FechaUtc >= Corte);
    }

    [Fact]
    public async Task Hasta_es_exclusivo()
    {
        var resultado = await ConsultarAsync(desde: null, hasta: Corte.AddDays(20));

        resultado.TotalElementos.Should().Be(3);
        resultado.Elementos.Should().OnlyContain(r => r.FechaUtc < Corte.AddDays(20));
    }

    [Fact]
    public async Task Desde_y_hasta_juntos_acotan_el_total_y_la_pagina()
    {
        var resultado = await ConsultarAsync(desde: Corte, hasta: Corte.AddDays(20));

        resultado.TotalElementos.Should().Be(2);
        resultado.Elementos.Select(r => r.FechaUtc).Should().Equal(Corte.AddDays(10), Corte);
    }

    private async Task<CaeManager.Application.Common.ResultadoPaginado<RegistroAuditoriaListaDto>> ConsultarAsync(DateTime? desde, DateTime? hasta)
    {
        await using var contexto = CrearContexto();
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var handler = new ObtenerAuditoriaQueryHandler(contexto, contexto, contexto, contexto, contexto, contexto, contexto, contexto, tenantActual);

        return await handler.Handle(
            new ObtenerAuditoriaQuery(_tipo, UsuarioId: null, Pagina: 1, TamanoPagina: 30, DesdeUtc: desde, HastaUtc: hasta),
            CancellationToken.None);
    }

    private async Task InsertarAsync(DateTime fechaUtc)
    {
        await using var contexto = CrearContexto();
        var registro = new RegistroAuditoria(
            _tipo, Guid.NewGuid(), "Creado", datosAntes: null, datosDespues: null,
            usuarioId: null, tipoActor: TipoActorAuditoria.Desconocido);
        contexto.RegistrosAuditoria.Add(registro);
        contexto.Entry(registro).Property(r => r.FechaUtc).CurrentValue = fechaUtc;
        await contexto.SaveChangesAsync();
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
