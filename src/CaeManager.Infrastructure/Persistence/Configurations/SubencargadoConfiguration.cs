using CaeManager.Domain.Cumplimiento.Subencargados;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

/// <summary>
/// Catálogo de Plataforma, sin Tenant (#2010, D-7). RLS forzada: lo lee todo el runtime y solo lo escribe un Actor de
/// Plataforma TALVEG con concesión AdminPlataforma global (migración AnadeSubencargadosAvisoD7).
/// </summary>
public class SubencargadoConfiguration : IEntityTypeConfiguration<Subencargado>
{
    public void Configure(EntityTypeBuilder<Subencargado> builder)
    {
        builder.ToTable("Subencargados");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Nombre).IsRequired().HasMaxLength(Subencargado.LongitudMaximaNombre);
        builder.Property(s => s.Finalidad).IsRequired().HasMaxLength(Subencargado.LongitudMaximaFinalidad);
        builder.Property(s => s.UbicacionYTransferencia).IsRequired().HasMaxLength(Subencargado.LongitudMaximaUbicacion);
        builder.Property(s => s.Conmutador).HasConversion<string>().HasMaxLength(ObjecionSubencargado.LongitudMaximaConmutador);
        builder.Property(s => s.Estado).IsRequired().HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.MotivoRetirada).HasMaxLength(Subencargado.LongitudMaximaMotivo);

        builder.HasIndex(s => s.Estado);
    }
}

public class AvisoCambioSubencargadoConfiguration : IEntityTypeConfiguration<AvisoCambioSubencargado>
{
    public void Configure(EntityTypeBuilder<AvisoCambioSubencargado> builder)
    {
        builder.ToTable("AvisosCambioSubencargado");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Fase).IsRequired().HasConversion<string>()
            .HasMaxLength(AvisoCambioSubencargado.LongitudMaximaFase);

        builder.HasOne<Subencargado>().WithMany().HasForeignKey(a => a.SubencargadoId).OnDelete(DeleteBehavior.Restrict);

        // La consulta de la activación: ¿tiene este Tenant un Enviado de este subencargado de hace al menos el plazo?
        builder.HasIndex(a => new { a.TenantId, a.SubencargadoId, a.Fase, a.RegistradoEnUtc });

        // Un envío deja como mucho una fila por destinatario y fase.
        builder.HasIndex(a => new { a.TenantId, a.EnvioId, a.UsuarioDestinatarioId, a.Fase }).IsUnique();

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

public class ObjecionSubencargadoConfiguration : IEntityTypeConfiguration<ObjecionSubencargado>
{
    public void Configure(EntityTypeBuilder<ObjecionSubencargado> builder)
    {
        builder.ToTable("ObjecionesSubencargado");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.Motivo).IsRequired().HasMaxLength(ObjecionSubencargado.LongitudMaximaMotivo);
        builder.Property(o => o.Efecto).IsRequired().HasConversion<string>()
            .HasMaxLength(ObjecionSubencargado.LongitudMaximaEfecto);
        builder.Property(o => o.Conmutador).HasConversion<string>().HasMaxLength(ObjecionSubencargado.LongitudMaximaConmutador);

        builder.HasOne<Subencargado>().WithMany().HasForeignKey(o => o.SubencargadoId).OnDelete(DeleteBehavior.Restrict);

        // Una objeción por Tenant propietario y subencargado: la segunda la rechaza el Command, y dos concurrentes
        // las separa este índice.
        builder.HasIndex(o => new { o.TenantId, o.SubencargadoId }).IsUnique();

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
