using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Incidente de despliegue a staging, 2026-08-30 (auditoría de colas, PR
/// #363). Causa raíz completa, en dos capas:
///
/// 1. <c>ReemplazarProcesadoPorEstadoEnEventoWebhook</c> (timestamp
///    20260830132641, mío) y <c>IndicesParametroSistemaYEventoWebhookPendientes</c>
///    (timestamp 20260830135800, Módulo 8) tocan la misma tabla y el mismo
///    índice viejo. Por NOMBRE de fichero, la mía debería aplicarse primero
///    — pero el PR de Módulo 8 se MERGEÓ a main antes que el mío
///    (<c>git merge-base --is-ancestor</c> lo confirma). Un despliegue
///    incremental como staging aplica las migraciones pendientes en cada
///    push, así que la de Módulo 8 corrió PRIMERO en el mundo real, con SU
///    contenido original (index_declara.CreateIndex(...) filtrado sobre
///    <c>Procesado</c>, que en ese momento todavía existía).
/// 2. Cuando mi migración por fin llegó (en el squash-merge del PR #363),
///    <c>__EFMigrationsHistory</c> ya tenía registrada la de Módulo 8 —
///    editar su fichero en la resolución de conflictos de main no la
///    revierte ni la reejecuta. Así que mi migración corrió contra un
///    esquema real con DOS restos de la de Módulo 8: el índice viejo ya
///    dropeado, y el índice parcial NUEVO ya creado y filtrado sobre
///    "Procesado" — la misma columna que mi migración necesita eliminar.
///
/// Sin ambos arreglos, el despliegue fallaba dos veces seguidas por dos
/// motivos distintos: primero "index ... does not exist" en el DropIndex
/// viejo (reproducido y confirmado con el mismo código 42704 del log real),
/// y — de arreglar solo eso — a continuación "cannot drop column procesado
/// because other objects depend on it" en el DropColumn, por el índice
/// parcial que todavía la referencia.
/// </summary>
public class MigracionDropIndexEventoWebhookTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task La_migracion_no_falla_cuando_el_indice_de_Modulo8_ya_corrio_primero_contra_Procesado()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = null };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        await using (var contexto = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual))
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync("20260830132011_AgregarSiguienteIntentoATrabajoAnalisisDocumento");
        }

        // Reproduce EXACTAMENTE lo que la migración original de Módulo 8
        // (20260830135800, tal como se desplegó de verdad, antes de que mi
        // resolución de conflictos editara su fichero) hizo sobre la
        // "Procesado" que todavía existía en ese momento: dropear el índice
        // viejo y crear el parcial nuevo filtrado sobre esa misma columna.
        await using (var conexionCruda = new NpgsqlConnection(_cadenaConexion))
        {
            await conexionCruda.OpenAsync();

            await using var dropIndiceViejo = new NpgsqlCommand(
                """DROP INDEX "IX_EventosWebhook_TenantId_Procesado";""", conexionCruda);
            await dropIndiceViejo.ExecuteNonQueryAsync();

            await using var crearIndiceParcial = new NpgsqlCommand(
                """
                CREATE INDEX "IX_EventosWebhook_TenantId_FechaRecepcionUtc_Pendientes"
                ON "EventosWebhook" ("TenantId", "FechaRecepcionUtc")
                WHERE NOT "Procesado";
                """, conexionCruda);
            await crearIndiceParcial.ExecuteNonQueryAsync();
        }

        await using (var contextoFinal = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual))
        {
            var migradorFinal = contextoFinal.GetInfrastructure().GetRequiredService<IMigrator>();

            // Antes del arreglo completo: 42704 en el DropIndex (ya cubierto
            // por el IF EXISTS) y, si solo se arregla eso, PostgreSQL
            // arrastra el índice parcial de arriba al eliminar la columna
            // "Procesado" que filtra — dejando el despliegue sin ese índice
            // para siempre, porque la migración que lo creaba ya está
            // registrada como aplicada y no vuelve a correr.
            await migradorFinal.MigrateAsync();
        }

        // No basta con que no lance — el índice tiene que quedar recreado
        // con el filtro correcto (sobre "Estado", no sobre la "Procesado"
        // ya desaparecida), o el despliegue queda "sano" pero silenciosamente
        // sin la optimización que Módulo 8 pretendía.
        await using var conexionVerificacion = new NpgsqlConnection(_cadenaConexion);
        await conexionVerificacion.OpenAsync();
        await using var verificarIndice = new NpgsqlCommand(
            """
            SELECT indexdef FROM pg_indexes
            WHERE indexname = 'IX_EventosWebhook_TenantId_FechaRecepcionUtc_Pendientes';
            """, conexionVerificacion);
        var definicion = (string?)await verificarIndice.ExecuteScalarAsync();

        definicion.Should().NotBeNull("el índice debe quedar recreado, no simplemente ausente");
        definicion!.Should().Contain("\"Estado\"").And.NotContain("\"Procesado\"");
    }
}
