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

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Reproduce, para <c>AgregarSensibilidadDocumental</c>, el defecto exacto que
/// <c>CorregirRequeridoCatalogoT2MigrationTests</c> documenta para T1: un
/// <c>UPDATE</c>/<c>UpdateData</c> keyed solo por Id de la semilla del tenant
/// #1 deja las copias del catálogo de otros tenants
/// (<c>SegundoTenantSeeder</c>, <c>DelegacionDemoSeeder</c> — Ids nuevos,
/// mismo <c>Nombre</c>) ancladas en el valor por defecto para siempre.
///
/// <para>
/// A diferencia de T1, <c>AgregarSensibilidadDocumental</c> traduce la
/// propuesta por <b>Nombre</b> (SQL <c>CASE "Nombre" ... END</c> sobre toda la
/// tabla), no por Id — este test comprueba que esa traducción alcanza
/// también a una fila de un tenant que no es el de la semilla, insertada
/// ANTES de que la migración corra.
/// </para>
/// </summary>
public class AgregarSensibilidadDocumentalOtroTenantMigrationTests : IAsyncLifetime
{
    private const string MigracionAnterior = "FkTenantEmpresaEnConversacion";
    private static readonly Guid TenantNoSemilla = Guid.NewGuid();

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    private static readonly Guid IdFilaOtroTenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(TenantNoSemilla);
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionAnterior);

        // Simula lo que SegundoTenantSeeder/DelegacionDemoSeeder ya dejarían
        // en la base antes de desplegar esta migración: una copia del
        // catálogo con un Id nuevo (no el de TipoDocumentoSeedData.Datos) y
        // el mismo Nombre exacto que un tipo ya clasificado. SQL directo, no
        // el DbContext actual: el modelo compilado de TipoDocumento ya
        // incluye Sensibilidad, así que cualquier consulta EF contra esta
        // base —que todavía no tiene esa columna, migrada solo hasta la
        // migración anterior— fallaría por columna inexistente antes incluso
        // de llegar a esta migración.
        await contexto.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "TiposDocumento"
                ("Id", "TenantId", "Nombre", "VigenciaMeses", "AplicaVencimientoAutomatico", "Orden",
                 "Notas", "Descripcion", "CriteriosValidacion", "SeSolicitaA", "Observaciones",
                 "Requerido", "Naturaleza", "AmbitoAplicacion",
                 "LecturaIaActiva", "DeteccionTrabajadoresActiva", "VerificacionIaActiva", "PerfilDocumentoOficial")
            VALUES
                ({IdFilaOtroTenant}, {TenantNoSemilla}, 'RLC', 3, true, 19,
                 NULL, NULL, NULL, NULL, NULL,
                 'Si', 'PracticaSector', 'Empresa',
                 true, false, false, 'Ninguno');
            """);

        await migrador.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Clasifica_por_nombre_una_fila_de_otro_tenant_que_ya_existia_antes_de_la_migracion()
    {
        await using var contexto = CrearContexto(TenantNoSemilla);

        var rlc = await contexto.TiposDocumento.SingleAsync(t => t.Nombre == "RLC");

        rlc.Sensibilidad.Should().Be(SensibilidadDocumental.SinDatosPersonales,
            "el UPDATE de AgregarSensibilidadDocumental traduce por Nombre sobre toda la tabla, no solo por Id de la semilla del tenant #1 — igual que CorregirRequeridoCatalogoT2 tuvo que arreglar para T1 después de desplegarla");
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
