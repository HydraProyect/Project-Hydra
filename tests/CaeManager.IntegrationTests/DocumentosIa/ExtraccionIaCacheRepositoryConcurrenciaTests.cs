using CaeManager.Domain.DocumentosIa;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.DocumentosIa;

/// <summary>
/// Carrera de caché documental (ObtenerAsync + Agregar separados en
/// DocumentAIRouterService — ver el comentario de
/// <c>RegistrarAuditoriaAsync</c>): dos ejecuciones concurrentes procesando el
/// mismo archivo bajo el mismo tipo esperado pueden pagar las dos la misma
/// extracción y chocar las dos al guardar contra el índice único
/// (TenantId, HashSha256, TipoEsperado, VersionPipeline). Contra Postgres
/// real porque lo que hay que demostrar es justo lo que un falso en memoria
/// no puede: que el choque es de verdad un <see cref="DbUpdateException"/> (no
/// otro tipo que el catch del router no atraparía) y que
/// <see cref="ExtraccionIaCacheRepository.DescartarTrasConflicto"/> de verdad
/// limpia el tracker lo bastante para que un SaveChangesAsync posterior no
/// repita el mismo choque.
/// </summary>
public class ExtraccionIaCacheRepositoryConcurrenciaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TipoEsperado = "Póliza de seguro";

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantA };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    [Fact]
    public async Task El_segundo_guardado_de_la_misma_clave_choca_contra_el_indice_unico_como_DbUpdateException()
    {
        await using var contextoGanador = CrearContexto();
        new ExtraccionIaCacheRepository(contextoGanador).Agregar(ExtraccionIaCache.Crear(Hash, TipoEsperado, """{"gano":true}"""));
        await contextoGanador.SaveChangesAsync();

        await using var contextoPerdedor = CrearContexto();
        new ExtraccionIaCacheRepository(contextoPerdedor).Agregar(ExtraccionIaCache.Crear(Hash, TipoEsperado, """{"gano":false}"""));

        var intento = () => contextoPerdedor.SaveChangesAsync();

        await intento.Should().ThrowAsync<DbUpdateException>(
            "es el mismo tipo de excepción que DocumentAIRouterService.RegistrarAuditoriaAsync atrapa para reintentar");
    }

    [Fact]
    public async Task Descartar_la_entrada_perdedora_permite_guardar_de_nuevo_sin_repetir_el_choque()
    {
        await using var contextoGanador = CrearContexto();
        var repoGanador = new ExtraccionIaCacheRepository(contextoGanador);
        repoGanador.Agregar(ExtraccionIaCache.Crear(Hash, TipoEsperado, """{"gano":true}"""));
        await contextoGanador.SaveChangesAsync();

        await using var contextoPerdedor = CrearContexto();
        var repoPerdedor = new ExtraccionIaCacheRepository(contextoPerdedor);
        var entradaPerdedora = ExtraccionIaCache.Crear(Hash, TipoEsperado, """{"gano":false}""");
        repoPerdedor.Agregar(entradaPerdedora);

        await Assert.ThrowsAsync<DbUpdateException>(() => contextoPerdedor.SaveChangesAsync());

        // Sin descartarla, este segundo intento repetiría el mismo INSERT y
        // volvería a chocar contra el mismo índice único — es justo el fallo
        // que RegistrarAuditoriaAsync tiene que evitar.
        repoPerdedor.DescartarTrasConflicto(entradaPerdedora);
        var filasGuardadas = await contextoPerdedor.SaveChangesAsync();

        filasGuardadas.Should().Be(0, "no queda ningún cambio pendiente una vez descartada la entrada perdedora");

        var entradaPersistida = await repoGanador.ObtenerAsync(Hash, TipoEsperado);
        entradaPersistida.Should().NotBeNull();
        entradaPersistida!.ExtraccionJson.Should().Be(
            """{"gano":true}""", "el dato que sobrevive en base de datos es el de quien ganó la carrera de escritura");
    }
}
