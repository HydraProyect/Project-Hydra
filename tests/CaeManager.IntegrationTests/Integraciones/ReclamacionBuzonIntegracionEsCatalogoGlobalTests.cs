using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// Incremento 1 de PROPUESTA-BUZONES-COMPARTIDOS-M365 § 5.4, capa
/// Infrastructure/PostgreSQL. Mismo tratamiento y misma prueba que
/// <c>AvisoRevisionNormativaEsCatalogoGlobalTests</c>: <see cref="ReclamacionBuzonIntegracion"/>
/// es un catálogo global (sin RLS, sin columna TenantId) porque la unicidad
/// que impone cruza Tenants por definición — ver el comentario de clase de la
/// entidad. Esto NO prueba autorización (quién puede invocar los Commands que
/// la usan), solo que el esquema no filtra esta tabla por tenant.
/// </summary>
public class ReclamacionBuzonIntegracionEsCatalogoGlobalTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    [Fact]
    public async Task Una_reclamacion_insertada_bajo_un_tenant_es_visible_identica_desde_otro_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        Guid reclamacionId;
        await using (var contextoA = CrearContexto(tenantA))
        {
            var reclamacion = new ReclamacionBuzonIntegracion("cae@arcosspa.com", tenantA, Guid.NewGuid(), DateTime.UtcNow);
            contextoA.ReclamacionesBuzonIntegracion.Add(reclamacion);
            await contextoA.SaveChangesAsync();
            reclamacionId = reclamacion.Id;
        }

        await using var contextoB = CrearContexto(tenantB);

        var vista = await contextoB.ReclamacionesBuzonIntegracion.SingleOrDefaultAsync(r => r.Id == reclamacionId);

        vista.Should().NotBeNull("un catálogo global no depende de qué tenant esté fijado en la sesión que lee");
        vista!.BuzonEmail.Should().Be("cae@arcosspa.com");
        vista.TenantPropietarioId.Should().Be(tenantA, "TenantPropietarioId documenta propiedad, no es una coordenada de aislamiento de lectura");
    }

    /// <summary>
    /// Documentación del diseño verificada contra el catálogo real (mismo
    /// patrón que <c>AvisoRevisionNormativaEsCatalogoGlobalTests</c>): la
    /// tabla no lleva RLS ni columna TenantId porque la entidad extiende
    /// <c>Entity</c> y no <c>EntidadConTenant</c>.
    /// </summary>
    [Fact]
    public async Task La_tabla_no_tiene_RLS_ni_columna_TenantId_documentando_que_es_un_catalogo_global()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using (var comandoRls = conexion.CreateCommand())
        {
            comandoRls.CommandText = "SELECT relrowsecurity FROM pg_class WHERE relname = 'ReclamacionesBuzonIntegracion';";
            var relrowsecurity = (bool?)await comandoRls.ExecuteScalarAsync();

            relrowsecurity.Should().NotBeNull("la tabla ReclamacionesBuzonIntegracion debe existir tras migrar");
            relrowsecurity!.Value.Should().BeFalse(
                "es un catálogo global (mismo patrón que AvisosRevisionNormativa): RLS aquí sería una política sin " +
                "columna que comparar, y neutralizaría la guarda de unicidad entre Tenants");
        }

        await using var comandoColumnas = conexion.CreateCommand();
        comandoColumnas.CommandText = @"
SELECT COUNT(*) FROM information_schema.columns
WHERE table_name = 'ReclamacionesBuzonIntegracion' AND column_name = 'TenantId';";
        var columnasTenantId = (long)(await comandoColumnas.ExecuteScalarAsync())!;

        columnasTenantId.Should().Be(0,
            "una columna TenantId convertiría la unicidad en una unicidad POR tenant, exactamente el bug que esta tabla existe para cerrar");
    }

    [Fact]
    public async Task El_indice_unico_de_BuzonEmail_rechaza_una_segunda_reclamacion_del_mismo_correo_entre_tenants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using (var contextoA = CrearContexto(tenantA))
        {
            contextoA.ReclamacionesBuzonIntegracion.Add(
                new ReclamacionBuzonIntegracion("cae@arcosspa.com", tenantA, Guid.NewGuid(), DateTime.UtcNow));
            await contextoA.SaveChangesAsync();
        }

        await using var contextoB = CrearContexto(tenantB);
        contextoB.ReclamacionesBuzonIntegracion.Add(
            new ReclamacionBuzonIntegracion("CAE@ArcosSPA.com", tenantB, Guid.NewGuid(), DateTime.UtcNow));

        var accion = () => contextoB.SaveChangesAsync();

        // Mismo correo con distinta capitalización: la normalización a
        // minúsculas del constructor es justo lo que hace que este choque
        // ocurra en vez de colar un duplicado por diferencia de mayúsculas.
        var excepcion = await accion.Should().ThrowAsync<DbUpdateException>();
        excepcion.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("23505");
    }
}
