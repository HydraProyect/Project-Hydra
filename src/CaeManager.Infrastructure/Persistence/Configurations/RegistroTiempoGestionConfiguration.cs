using CaeManager.Domain.Telemetria;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class RegistroTiempoGestionConfiguration : IEntityTypeConfiguration<RegistroTiempoGestion>
{
    public void Configure(EntityTypeBuilder<RegistroTiempoGestion> builder)
    {
        builder.ToTable("RegistrosTiempoGestion");
        builder.HasKey(r => r.Id);

        // Índices por usuario y por cliente, ambos acotados por período. El del usuario
        // servía al KPI de ocupación por Gestor CAE, ya retirado: hoy ningún agregado lee
        // por (TenantId, UsuarioId, FinUtc) — ObtenerTiempoGestionConversacionQuery filtra
        // por ConversacionId y usuario. Las horas por Cliente usan el segundo. Sin FK a
        // Conversacion/Cliente ni navigation properties, mismo criterio que
        // EventoConversacion: viven en otros agregados.
        builder.HasIndex(r => new { r.TenantId, r.UsuarioId, r.FinUtc });
        builder.HasIndex(r => new { r.TenantId, r.ClienteId, r.FinUtc });
        builder.HasIndex(r => r.ConversacionId);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
