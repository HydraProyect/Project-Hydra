using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CaeManager.Web.Tests;

public class ConcurrenciaContextoTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Un_conflicto_descarta_la_edicion_y_su_auditoria_sin_reintentarlas(bool sincrono)
    {
        var interceptor = new ConflictoAlGuardar();
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=localhost;Database=no_se_abre;Username=no_se_usa")
            .AddInterceptors(interceptor).Options;
        await using var contexto = new CaeManagerDbContext(options,
            new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = Guid.NewGuid() });
        var empresa = Empresa.CrearComoCliente("Empresa de prueba", "B12345674", false, null, null);
        contexto.Attach(empresa);
        empresa.ActualizarComoCliente("Edición perdida", "B12345674", false, null);
        var auditoria = new RegistroAuditoria(nameof(Empresa), empresa.Id, "Modificado", null, null,
            null, TipoActorAuditoria.Desconocido);
        interceptor.Auditoria = auditoria;
        var relacion = RelacionEmpresarial.Crear(empresa.Id, Guid.NewGuid(), DateTime.UtcNow);
        contexto.RelacionesEmpresariales.Add(relacion);
        var conservada = Empresa.CrearComoCliente("Empresa sin cambios", "B12345674", false, null, null);
        contexto.Attach(conservada);

        Func<Task> guardar = async () =>
        {
            if (sincrono)
                contexto.SaveChanges();
            else
                await contexto.SaveChangesAsync();
        };
        var error = await guardar.Should().ThrowAsync<DbUpdateConcurrencyException>();
        error.Which.Should().BeSameAs(interceptor.Conflicto);
        contexto.Entry(empresa).State.Should().Be(EntityState.Detached);
        contexto.Entry(auditoria).State.Should().Be(EntityState.Detached);
        contexto.Entry(relacion).State.Should().Be(EntityState.Detached);
        contexto.Entry(conservada).State.Should().Be(EntityState.Unchanged);
        contexto.ChangeTracker.HasChanges().Should().BeFalse();

        // Sin entidades pendientes, EF devuelve cero sin abrir ninguna conexión. Si queda
        // la edición o su auditoría, este guardado intentaría acceder a la base inexistente.
        if (sincrono)
            contexto.SaveChanges().Should().Be(0);
        else
            (await contexto.SaveChangesAsync()).Should().Be(0);
    }

    private sealed class ConflictoAlGuardar : SaveChangesInterceptor
    {
        public RegistroAuditoria Auditoria { get; set; } = null!;
        public DbUpdateConcurrencyException? Conflicto { get; private set; }

        private void FallarUnaVez(DbContext contexto)
        {
            if (Conflicto is not null)
                return;
            // La auditoría nace durante SaveChanges, como la de los interceptores reales.
            contexto.Set<RegistroAuditoria>().Add(Auditoria);
            Conflicto = new DbUpdateConcurrencyException("Carrera simulada al guardar");
            throw Conflicto;
        }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            FallarUnaVez(eventData.Context!);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            FallarUnaVez(eventData.Context!);
            return ValueTask.FromResult(result);
        }
    }
}
