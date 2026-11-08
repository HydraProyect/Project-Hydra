using CaeManager.Domain.Centros;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// Las pruebas de una migración antigua deshacen el esquema hasta ella y siembran con el modelo
/// actual, que ya pide columnas de migraciones posteriores. Este modelo prescinde de las que añadió
/// AnadeConexionesPlataformaCae y de las que añadieron CortesTenantYVentanaClaveApi,
/// AnadeClavesIntegracionTenant y AnadeSolicitudesAltaYCifPropietario, porque ninguna de esas pruebas las necesita y sin esto no podrían
/// insertar un Tenant ni leer un proveedor sobre el esquema antiguo. Por lo mismo prescinde del ancla del papel por Centro
/// (<c>Centros.RelacionEmpresarialEntradaId</c>, AnadeAnclaPapelEntradaCentro), con su índice y su FK compuesta: sin esto
/// no podrían insertar un Centro.
/// </summary>
internal static class ModeloSinColumnasDeConectores
{
    public static DbContextOptionsBuilder<CaeManagerDbContext> Opciones() =>
        new DbContextOptionsBuilder<CaeManagerDbContext>()
            .ReplaceService<IModelCustomizer, Personalizador>()
            .ConfigureWarnings(avisos => avisos.Ignore(RelationalEventId.PendingModelChangesWarning));

    private sealed class Personalizador(ModelCustomizerDependencies dependencias) : ModelCustomizer(dependencias)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<Tenant>().Ignore(t => t.AddOnSubidaAutomaticaActivo);
            modelBuilder.Entity<Tenant>().Ignore(t => t.ClavesApiValidasDesdeUtc);
            modelBuilder.Entity<Tenant>().Ignore(t => t.IntegracionHabilitada);
            modelBuilder.Entity<Tenant>().Ignore(t => t.TipoTenant);
            modelBuilder.Entity<ProveedorPlataformaCae>().Ignore(p => p.ConectorApiActivo);
            modelBuilder.Entity<Centro>().Ignore(c => c.RelacionEmpresarialEntradaId);
        }
    }
}
