using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
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
/// DCR-19, riesgo 1 del handoff: <c>AlinearModeloConCheckXorDocumentoExistente</c>
/// tiene que aplicarse sin error sobre una base que YA tiene
/// <c>CK_Documentos_PropietarioXor</c> —el estado real de producción y
/// staging, creada por <c>RendimientoBusquedasYCheckXorDocumento</c> el
/// 2026-08-01— no solo sobre una base vacía. Los tests de
/// <see cref="CheckXorPropietarioDocumentoTests"/> cubren la base vacía
/// (aplican todas las migraciones desde cero); este cubre específicamente el
/// caso "constraint ya presente", migrando primero hasta la migración
/// inmediatamente anterior y aplicando el resto después — sin este test, esa
/// comprobación solo existía como verificación manual fuera del historial de
/// commits.
/// </summary>
public class AlinearModeloConCheckXorDocumentoExistenteTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Se_aplica_sin_error_sobre_una_base_que_ya_tiene_la_constraint_y_la_deja_funcionando()
    {
        // La migración inmediatamente anterior deja CK_Documentos_PropietarioXor
        // ya creada (por RendimientoBusquedasYCheckXorDocumento, 2026-08-01) —
        // el mismo punto de partida que producción y staging hoy.
        await using (var contexto = CrearContexto())
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync("AgregarVersionAAvisoRevisionNormativa");
        }

        Exception? excepcion;
        await using (var contexto = CrearContexto())
            excepcion = await Record.ExceptionAsync(() => contexto.Database.MigrateAsync());

        excepcion.Should().BeNull(
            "AlinearModeloConCheckXorDocumentoExistente debe ser idempotente sobre una base que ya tiene CK_Documentos_PropietarioXor");

        await using var verificacion = CrearContexto();
        var cliente = Empresa.CrearComoCliente("Verificación Migración Alineación S.L.", "B12345674", false, null, null);
        verificacion.Empresas.Add(cliente);
        var tipo = new TipoDocumento(
            "Certificado Verificación Migración", 12, aplicaVencimientoAutomatico: true, 1,
            AmbitoAplicacion.Cliente, requerido: RequisitoDocumental.Si);
        verificacion.TiposDocumento.Add(tipo);
        var documento = Documento.DeCliente(cliente.Id, tipo.Id, DateOnly.FromDateTime(DateTime.UtcNow), null);
        verificacion.Documentos.Add(documento);
        await verificacion.SaveChangesAsync();

        var excepcionUpdate = await Record.ExceptionAsync(() => verificacion.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Documentos\" SET \"ClienteId\" = NULL WHERE \"Id\" = {documento.Id}"));

        excepcionUpdate.Should().NotBeNull(
            "la constraint debe seguir rechazando un documento sin propietario después de aplicar la migración de alineación");
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
