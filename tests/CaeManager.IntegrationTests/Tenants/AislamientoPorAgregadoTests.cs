using CaeManager.Domain.Alertas;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.RequisitosDocumentales;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Cierre de la Etapa 5 de PLAN-MIGRACION-MULTITENANT.md: test de
/// aislamiento por cada uno de los 25 tipos de entidad expuestos en
/// <c>IApplicationDbContext</c> (regla de docs/MULTITENANCY.md § 9 —
/// "los tests de aislamiento se escriben por agregado... toda entidad
/// nueva añade el suyo"). <see cref="AislamientoMultiTenantTests"/> ya
/// prueba con más profundidad de escenario (fallo cerrado, rechazo de
/// modificación cruzada, índice único) sobre dos entidades representativas
/// (Cliente/Alerta); este archivo cierra la cobertura completa de las 25,
/// con la misma verificación de visibilidad en cada una.
/// </summary>
public class AislamientoPorAgregadoTests : IAsyncLifetime
{
    private readonly string _rutaBaseDatos = Path.Combine(Path.GetTempPath(), $"caemanager-aislamiento-agregado-{Guid.NewGuid()}.db");
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto(_tenantA);
        await dbContext.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_rutaBaseDatos)) File.Delete(_rutaBaseDatos);
        return Task.CompletedTask;
    }

    private CaeManagerDbContext CrearContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseSqlite($"Data Source={_rutaBaseDatos}")
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private async Task VerificarAislamientoAsync<TEntidad>(Func<TEntidad> crear) where TEntidad : EntidadConTenant
    {
        Guid id;
        await using (var contextoA = CrearContexto(_tenantA))
        {
            var entidad = crear();
            contextoA.Set<TEntidad>().Add(entidad);
            await contextoA.SaveChangesAsync();
            id = entidad.Id;
        }

        await using var contextoB = CrearContexto(_tenantB);
        var visibleParaB = await contextoB.Set<TEntidad>().FirstOrDefaultAsync(e => e.Id == id);
        visibleParaB.Should().BeNull($"una fila de {typeof(TEntidad).Name} creada por otro tenant nunca debe ser visible");

        await using var contextoAOtraVez = CrearContexto(_tenantA);
        var visibleParaA = await contextoAOtraVez.Set<TEntidad>().FirstOrDefaultAsync(e => e.Id == id);
        visibleParaA.Should().NotBeNull($"el propio tenant que creó la fila de {typeof(TEntidad).Name} debe poder verla");
    }

    // --- 9 agregados con soft delete (EntidadBase) ---

    [Fact]
    public Task Aislamiento_Cliente() => VerificarAislamientoAsync(
        () => new Cliente("RENDELSUR", "B12345674", esCritico: false));

    [Fact]
    public Task Aislamiento_Centro() => VerificarAislamientoAsync(
        () => new Centro(Guid.NewGuid(), Guid.NewGuid(), "Planta Sevilla"));

    [Fact]
    public Task Aislamiento_Documento() => VerificarAislamientoAsync(
        () => Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), null));

    [Fact]
    public Task Aislamiento_Empresa() => VerificarAislamientoAsync(
        () => new Empresa("Ibertec S.A."));

    [Fact]
    public Task Aislamiento_RequisitoDocumental() => VerificarAislamientoAsync(
        () => new RequisitoDocumental(Guid.NewGuid(), "AEAT nominativo", null, bloqueaAcceso: false));

    [Fact]
    public Task Aislamiento_Subcontrata() => VerificarAislamientoAsync(
        () => new Subcontrata("Subcontrata de prueba"));

    [Fact]
    public Task Aislamiento_Trabajador() => VerificarAislamientoAsync(
        () => Trabajador.DeEmpresa(Guid.NewGuid(), "Juan", "Pérez", "12345678Z"));

    [Fact]
    public Task Aislamiento_Vehiculo() => VerificarAislamientoAsync(
        () => Vehiculo.DeEmpresa(Guid.NewGuid(), "Furgoneta 1", "Modelo X", "1234ABC"));

    [Fact]
    public Task Aislamiento_Visita() => VerificarAislamientoAsync(
        () => new Visita(Guid.NewGuid(), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), null));

    // --- 16 tablas de unión/satélite (EntidadConTenant directa, sin soft delete) ---

    [Fact]
    public Task Aislamiento_Alerta() => VerificarAislamientoAsync(
        () => new Alerta(Guid.NewGuid(), NivelAlerta.Urgente));

    [Fact]
    public Task Aislamiento_Asignacion() => VerificarAislamientoAsync(
        () => new Asignacion(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1)));

    [Fact]
    public Task Aislamiento_RegistroAuditoria() => VerificarAislamientoAsync(
        () => new RegistroAuditoria("Cliente", Guid.NewGuid(), "Creado", null, "{}", null));

    [Fact]
    public Task Aislamiento_PlataformaAcceso() => VerificarAislamientoAsync(
        () => new PlataformaAcceso(Guid.NewGuid(), "CTAIMA CAE", null, null, null));

    [Fact]
    public Task Aislamiento_ParametroSistema() => VerificarAislamientoAsync(
        () => new ParametroSistema(30, 15));

    [Fact]
    public Task Aislamiento_ConfiguracionIaDocumentoCliente() => VerificarAislamientoAsync(
        () => new ConfiguracionIaDocumentoCliente(Guid.NewGuid(), Guid.NewGuid(), activa: true));

    [Fact]
    public Task Aislamiento_TipoDocumento() => VerificarAislamientoAsync(
        () => new TipoDocumento("Tipo de prueba", 12, true, 1, AmbitoAplicacion.Trabajador));

    [Fact]
    public Task Aislamiento_TipoDocumentoCentro() => VerificarAislamientoAsync(
        () => new TipoDocumentoCentro(Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public Task Aislamiento_CredencialAccesoEmpresa() => VerificarAislamientoAsync(
        () => new CredencialAccesoEmpresa(Guid.NewGuid(), null, null, null, null));

    [Fact]
    public Task Aislamiento_EmpresaCliente() => VerificarAislamientoAsync(
        () => new EmpresaCliente(Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public Task Aislamiento_NotificacionUsuario() => VerificarAislamientoAsync(
        () => new NotificacionUsuario(Guid.NewGuid(), "Título", "Mensaje"));

    [Fact]
    public Task Aislamiento_CredencialAccesoSubcontrata() => VerificarAislamientoAsync(
        () => new CredencialAccesoSubcontrata(Guid.NewGuid(), null, null, null, null));

    [Fact]
    public Task Aislamiento_SubcontrataCliente() => VerificarAislamientoAsync(
        () => new SubcontrataCliente(Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public Task Aislamiento_SubcontrataEmpresa() => VerificarAislamientoAsync(
        () => new SubcontrataEmpresa(Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public Task Aislamiento_DeteccionTrabajador() => VerificarAislamientoAsync(
        () => DeteccionTrabajador.Nuevo(Guid.NewGuid(), Guid.NewGuid(), "Juan", "Pérez", "12345678Z"));

    [Fact]
    public Task Aislamiento_VisitaTrabajador() => VerificarAislamientoAsync(
        () => new VisitaTrabajador(Guid.NewGuid(), Guid.NewGuid()));
}
