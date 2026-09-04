using CaeManager.Domain.Cumplimiento;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class InstruccionTratamientoIaTenantPropietarioConfiguration : IEntityTypeConfiguration<InstruccionTratamientoIaTenantPropietario>
{
    public void Configure(EntityTypeBuilder<InstruccionTratamientoIaTenantPropietario> builder)
    {
        builder.ToTable("InstruccionesTratamientoIaTenantPropietario");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.VersionDpaAceptada)
            .IsRequired()
            .HasMaxLength(InstruccionTratamientoIaTenantPropietario.LongitudMaximaVersion);

        builder.Property(i => i.VersionAnexoSubencargadosAceptada)
            .IsRequired()
            .HasMaxLength(InstruccionTratamientoIaTenantPropietario.LongitudMaximaVersion);

        builder.Property(i => i.OrigenInstruccion).IsRequired().HasConversion<string>().HasMaxLength(30);

        builder.Property(i => i.MotivoRevocacion).HasMaxLength(InstruccionTratamientoIaTenantPropietario.LongitudMaximaMotivoRevocacion);

        // Consulta del gate (Nivel 0): "¿tiene este tenant una fila vigente?"
        // — la única consulta caliente de las cinco rutas de IA que este
        // incremento gatea. Filtro parcial: solo indexa lo que la consulta
        // realmente busca (RevocadaEnUtc IS NULL), igual criterio que otros
        // índices de "vigente" del repositorio.
        //
        // IsUnique() no es solo rendimiento: es la única garantía real de "a
        // lo sumo una fila vigente por tenant", el invariante del que
        // depende ObtenerVigenteAsync (FirstOrDefaultAsync sin ORDER BY, así
        // que si hubiera dos filas vigentes cuál gobierna el gate de Nivel 0
        // sería indeterminado). El check-then-insert de
        // RegistrarInstruccionTratamientoIaTenantPropietarioCommandHandler
        // es TOCTOU por construcción — dos altas concurrentes para el mismo
        // tenant podrían pasar la comprobación las dos. Mismo defecto y
        // misma corrección que ya tienen AsignacionConfiguration (el
        // incidente que la motivó) y ProyectoTecnicoConfiguration.
        builder.HasIndex(i => i.TenantId)
            .IsUnique()
            .HasFilter("\"RevocadaEnUtc\" IS NULL")
            .HasDatabaseName("IX_InstruccionesTratamientoIaTenantPropietario_TenantId_Vigente");

        // Histórico completo (demostrabilidad, criterio de aceptación §15.3):
        // sin el filtro de arriba, para no excluir filas ya revocadas.
        builder.HasIndex(i => new { i.TenantId, i.FechaAceptacionUtc });
    }
}
