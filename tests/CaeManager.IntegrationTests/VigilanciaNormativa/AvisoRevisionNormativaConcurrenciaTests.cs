using CaeManager.Domain.VigilanciaNormativa;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.VigilanciaNormativa;

/// <summary>
/// Hallazgo de la revisión adversaria de Codex sobre el commit de H-3/DEC-8:
/// sin token de concurrencia, dos Actores de Plataforma marcando el mismo
/// aviso a la vez se pisaban en silencio — el segundo <c>SaveChanges</c>
/// sobrescribía <see cref="AvisoRevisionNormativa.RevisadoPorUsuarioId"/> y
/// <see cref="AvisoRevisionNormativa.NotasRevision"/> del primero sin ningún
/// error. Cerrado dando a la entidad <c>IVersionable</c> — mismo mecanismo
/// que <c>AsignacionResponsabilidad</c>, ya wireado en
/// <c>ConcurrenciaOptimistaInterceptor</c> y <c>ConcurrenciaBehavior</c>.
/// </summary>
public class AvisoRevisionNormativaConcurrenciaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    [Fact]
    public async Task Dos_revisiones_simultaneas_del_mismo_aviso_la_segunda_choca_en_vez_de_pisar_la_primera()
    {
        var avisoId = Guid.NewGuid();
        await using (var contextoSeed = CrearContexto())
        {
            var aviso = new AvisoRevisionNormativa(
                "BOE-A-2026-CONCURRENCIA", new DateOnly(2026, 8, 13),
                "Real Decreto 171/2004, de coordinación de actividades empresariales.",
                "https://www.boe.es/diario_boe/txt.php?id=BOE-A-2026-CONCURRENCIA", "RD 171/2004", DateTime.UtcNow);
            contextoSeed.AvisosRevisionNormativa.Add(aviso);
            await contextoSeed.SaveChangesAsync();
            avisoId = aviso.Id;
        }

        // Dos Actores de Plataforma distintos cargan la MISMA fila antes de
        // que ninguno de los dos guarde — la condición de carrera real.
        await using var contextoActorA = CrearContexto();
        await using var contextoActorB = CrearContexto();

        var avisoParaA = await contextoActorA.AvisosRevisionNormativa.SingleAsync(a => a.Id == avisoId);
        var avisoParaB = await contextoActorB.AvisosRevisionNormativa.SingleAsync(a => a.Id == avisoId);

        var usuarioA = Guid.NewGuid();
        var usuarioB = Guid.NewGuid();
        avisoParaA.MarcarRevisado(usuarioA, "Revisión de A: no afecta al catálogo.", DateTime.UtcNow);
        avisoParaB.MarcarRevisado(usuarioB, "Revisión de B: sí afecta al catálogo.", DateTime.UtcNow);

        // A gana la carrera.
        await contextoActorA.SaveChangesAsync();

        // B choca: la propiedad exigida no es "no lanza nada", es que lo
        // segundo en guardar detecte que pisaría un cambio que no vio.
        var intentoDeB = async () => await contextoActorB.SaveChangesAsync();
        await intentoDeB.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "B cargó la fila antes de que A guardara: su Version en memoria ya no coincide con la de la base " +
            "de datos, y sin este choque B sobrescribiría en silencio la revisión de A");

        // Y el ganador real, leído de nuevo, es A — no una mezcla ni B.
        await using var contextoVerificacion = CrearContexto();
        var avisoFinal = await contextoVerificacion.AvisosRevisionNormativa.SingleAsync(a => a.Id == avisoId);
        avisoFinal.RevisadoPorUsuarioId.Should().Be(usuarioA);
        avisoFinal.NotasRevision.Should().Be("Revisión de A: no afecta al catálogo.");
    }
}
