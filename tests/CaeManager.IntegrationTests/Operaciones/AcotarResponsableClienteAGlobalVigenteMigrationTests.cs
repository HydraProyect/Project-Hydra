using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// Migración <c>AcotarResponsableClienteAGlobalVigente</c> — auditoría Módulo 5,
/// hallazgo crítico 3/9. El índice único de responsable de cliente pasaba de
/// estar acotado POR OPERACIÓN a ser GLOBAL por (PropietarioTenantId,
/// AmbitoRelacionClienteId): antes, una cartera interna y una externa vigentes
/// sobre el mismo cliente convivían sin chocar.
///
/// Se siembra ANTES de aplicar la migración objetivo (contra el esquema tal
/// como quedó justo antes, con el índice viejo) para reproducir el escenario
/// real: bases con el defecto ya presente, no datos creados después de que el
/// índice nuevo exista.
/// </summary>
public class AcotarResponsableClienteAGlobalVigenteMigrationTests : IAsyncLifetime
{
    private const string MigracionAntes = "RenombrarTiposDocumentoContaminadosT3";
    private const string MigracionObjetivo = "AcotarResponsableClienteAGlobalVigente";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _propietario = Guid.NewGuid();
    private readonly Guid _operador = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_propietario);
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionAntes);
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Cierra_los_duplicados_heredados_conservando_el_mas_reciente_antes_de_crear_el_indice_global()
    {
        var ahora = DateTime.UtcNow;
        Guid carteraAntigua, carteraReciente;

        await using (var contexto = CrearContexto(_propietario))
        {
            var cliente = Empresa.CrearComoCliente(
                "Cliente Duplicado S.A.", "B10380186", esCritico: false, notas: null, ejecutivoUsuarioId: null);
            contexto.Empresas.Add(cliente);
            await contexto.SaveChangesAsync();

            var raiz = AsignacionOperacion.Raiz(_propietario, ServicioCae.Outbound, ahora.AddDays(-10), ahora.AddDays(-10));
            var externa = AsignacionOperacion.Externa(
                _propietario, _operador, ServicioCae.Outbound, AmbitoAsignacion.Universal,
                ahora.AddDays(-10), null, ahora.AddDays(-10));
            contexto.AsignacionesOperacion.AddRange(raiz, externa);
            await contexto.SaveChangesAsync();

            // Dos carteras vigentes sobre el MISMO cliente, colgando de DOS
            // operaciones distintas: el índice viejo (por operación) lo permite.
            var antigua = AsignacionCartera.Interna(
                raiz, Guid.NewGuid(), AmbitoAsignacion.DeRelacionCliente(cliente.Id), ahora.AddDays(-5), null, ahora.AddDays(-5));
            var reciente = AsignacionCartera.Externa(
                externa, Guid.NewGuid(), "GestorCae", AmbitoAsignacion.DeRelacionCliente(cliente.Id), ahora.AddDays(-1), null, ahora.AddDays(-1));
            contexto.AsignacionesCartera.AddRange(antigua, reciente);
            await contexto.SaveChangesAsync();

            carteraAntigua = antigua.Id;
            carteraReciente = reciente.Id;
        }

        await using (var contexto = CrearContexto(_propietario))
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();

            // No debe fallar: si la migración creara el índice global sin cerrar
            // antes el duplicado heredado, este MigrateAsync lanzaría 23505.
            await migrador.MigrateAsync(MigracionObjetivo);
        }

        await using var verificacion = CrearContexto(_propietario);
        var antiguaDespues = await verificacion.AsignacionesCartera.SingleAsync(c => c.Id == carteraAntigua);
        var recienteDespues = await verificacion.AsignacionesCartera.SingleAsync(c => c.Id == carteraReciente);

        antiguaDespues.Estado.Should().Be(EstadoAsignacion.Cerrada, "la migración cierra los duplicados heredados");
        antiguaDespues.MotivoCierre.Should().Be(MotivoCierreAsignacion.Transferida);
        recienteDespues.Estado.Should().Be(EstadoAsignacion.Vigente, "se conserva la vigencia más reciente");
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
