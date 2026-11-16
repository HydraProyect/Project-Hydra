using CaeManager.Domain.Empresas;
using CaeManager.Domain.Importacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Importacion;

/// <summary>
/// #1992: la perdedora de dos confirmaciones simultáneas del mismo plan recibe «ya ejecutada» aunque PostgreSQL detecte
/// antes la violación de otro índice único (aquí, el CIF de una Empresa que la ganadora creó) que la de la marca de la
/// operación. EF inserta las Empresas antes que las marcas (orden de tabla), así que el estado de la base tras el
/// COMMIT de la ganadora reproduce, sin hilos, el orden de detección de la carrera real. Antes de la corrección, este
/// guardado lanzaba la DbUpdateException del CIF.
/// </summary>
public class GuardarSiOperacionNuevaTrasOtraViolacionTests : IAsyncLifetime
{
    private const string Cif = "B12345674";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task La_violacion_de_otro_indice_con_la_operacion_ya_guardada_es_ya_ejecutada()
    {
        var operacionId = Guid.NewGuid();
        await GuardarGanadoraAsync(operacionId);

        await using var contexto = CrearContexto();
        var repositorio = new OperacionImportacionRepository(contexto);
        repositorio.Agregar(OperacionImportacion.Registrar(operacionId));
        contexto.Empresas.Add(new Empresa("Perdedora S.L.", Cif));

        var guardada = await repositorio.GuardarSiOperacionNuevaAsync();

        guardada.Should().BeFalse("la operación ya la guardó la ganadora: la perdedora no escribe y responde «ya ejecutada»");
        contexto.ChangeTracker.Entries().Should().BeEmpty("lo pendiente de la perdedora no puede quedar para otro guardado del circuito");
        await using var verificacion = CrearContexto();
        (await verificacion.Empresas.CountAsync()).Should().Be(1, "solo la Empresa de la ganadora");
        (await verificacion.OperacionesImportacion.CountAsync(o => o.OperacionId == operacionId)).Should().Be(1);
    }

    [Fact]
    public async Task La_violacion_de_otro_indice_sin_la_operacion_guardada_se_propaga()
    {
        await GuardarGanadoraAsync(operacionId: null);

        await using var contexto = CrearContexto();
        var repositorio = new OperacionImportacionRepository(contexto);
        repositorio.Agregar(OperacionImportacion.Registrar(Guid.NewGuid()));
        contexto.Empresas.Add(new Empresa("Perdedora S.L.", Cif));

        var guardar = () => repositorio.GuardarSiOperacionNuevaAsync();

        (await guardar.Should().ThrowAsync<DbUpdateException>("un CIF repetido sin carrera es un error real del plan"))
            .Which.InnerException.Should().BeOfType<Npgsql.PostgresException>()
            .Which.SqlState.Should().Be("23505");
    }

    [Fact]
    public async Task La_violacion_de_la_propia_marca_sigue_siendo_ya_ejecutada()
    {
        var operacionId = Guid.NewGuid();
        await GuardarGanadoraAsync(operacionId, conEmpresa: false);

        await using var contexto = CrearContexto();
        var repositorio = new OperacionImportacionRepository(contexto);
        repositorio.Agregar(OperacionImportacion.Registrar(operacionId));
        contexto.Empresas.Add(new Empresa("Perdedora S.L.", Cif));

        (await repositorio.GuardarSiOperacionNuevaAsync()).Should().BeFalse();
        await using var verificacion = CrearContexto();
        (await verificacion.Empresas.CountAsync()).Should().Be(0, "la transacción entera de la perdedora se descarta");
    }

    /// <summary>Lo que la confirmación ganadora dejó confirmado: su Empresa con el CIF del plan y, si se da, su marca.</summary>
    private async Task GuardarGanadoraAsync(Guid? operacionId, bool conEmpresa = true)
    {
        await using var contexto = CrearContexto();
        if (conEmpresa) contexto.Empresas.Add(new Empresa("Ganadora S.L.", Cif));
        if (operacionId is { } id) contexto.OperacionesImportacion.Add(OperacionImportacion.Registrar(id));
        await contexto.SaveChangesAsync();
    }

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql =>
            {
                npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL");
                npgsql.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(30), errorCodesToAdd: null);
            })
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }
}
