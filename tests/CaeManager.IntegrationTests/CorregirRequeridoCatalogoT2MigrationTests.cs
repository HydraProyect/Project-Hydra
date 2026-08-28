using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Repara de paso un defecto de T1 (#301): su <c>UPDATE</c> masivo puso
/// <c>Naturaleza = 'RequisitoCliente'</c> en TODAS las filas de TODOS los
/// tenants, y solo lo corrigió después con <c>UpdateData</c> por Id — Id que
/// solo existen en la semilla del tenant #1
/// (<c>TenantSeedData.IdPorDefecto</c>). Cualquier otro tenant que ya
/// existiera cuando corrió la migración de T1 se quedó con las 16 naturalezas
/// verificadas rebajadas a RequisitoCliente: sub-afirma, pero es falso igual.
///
/// <para>
/// Reproduce el defecto migrando solo hasta T1, insertando una fila "vieja"
/// de un tenant que NO es el #1 con el valor roto que T1 le habría dejado, y
/// comprobando que <c>CorregirRequeridoCatalogoT2</c> la repara al continuar.
/// </para>
/// </summary>
public class CorregirRequeridoCatalogoT2MigrationTests : IAsyncLifetime
{
    private const string MigracionDeT1 = "PartirEsObligatorioEnRequeridoYNaturaleza";
    private static readonly Guid TenantNoSemilla = Guid.NewGuid();

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(TenantNoSemilla);
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionDeT1);

        // El estado que T1 le habría dejado a un tenant que no es el #1:
        // Requerido correcto (traducción mecánica sí alcanza a todos), pero
        // Naturaleza rebajada a RequisitoCliente pese a ser una obligación
        // legal verificada.
        var evr = new TipoDocumento(
            "EVR (Evaluación de Riesgos Laborales)", null, aplicaVencimientoAutomatico: false, orden: 1,
            AmbitoAplicacion.Empresa, RequisitoDocumental.Si, NaturalezaJuridica.RequisitoCliente);
        contexto.TiposDocumento.Add(evr);
        await contexto.SaveChangesAsync();

        await migrador.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Repone_la_naturaleza_verificada_para_un_tenant_que_no_es_el_de_la_semilla()
    {
        await using var contexto = CrearContexto(TenantNoSemilla);

        var evr = await contexto.TiposDocumento.SingleAsync(t => t.Nombre == "EVR (Evaluación de Riesgos Laborales)");

        evr.Naturaleza.Should().Be(NaturalezaJuridica.ObligacionLegal,
            "el defecto de T1 dejaba esta fila en RequisitoCliente para cualquier tenant que no fuera el de la semilla");
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
