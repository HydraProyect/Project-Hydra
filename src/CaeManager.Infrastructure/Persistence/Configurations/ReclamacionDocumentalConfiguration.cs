using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Reclamaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ReclamacionDocumentalConfiguration : IEntityTypeConfiguration<ReclamacionDocumental>
{
    public void Configure(EntityTypeBuilder<ReclamacionDocumental> builder)
    {
        // Titular polimórfico excluyente, impuesto en la base y no solo en el
        // constructor del agregado: la invariante tiene que sobrevivir a una
        // siembra por SQL, a una migración de datos y a un seeder — cualquiera
        // de los tres puede escribir sin pasar por el dominio. Y el conteo debe
        // ser exactamente 1, no "como mucho 1": una reclamación sin titular no
        // se le podría enseñar a nadie, y los lectores por cartera la
        // esconderían para siempre sin decir por qué.
        builder.ToTable("ReclamacionesDocumentales", t => t.HasCheckConstraint(
            "CK_ReclamacionesDocumentales_TitularUnico",
            "num_nonnulls(\"ClienteId\", \"EmpresaId\") = 1"));
        builder.HasKey(r => r.Id);

        builder.Property(r => r.DestinatarioEmail).IsRequired().HasMaxLength(ReclamacionDocumental.LongitudMaximaDestinatarioEmail);
        builder.Property(r => r.ConversacionId);

        builder.HasIndex(r => new { r.TenantId, r.ClienteId });

        // Titular polimórfico (ver ReclamacionDocumental): las dos anclas
        // repuntan contra Empresas y se recorren por separado —
        // ObtenerLoteReclamacionEmpresaQuery agrupa por EmpresaId igual que
        // ObtenerLoteReclamacionQuery agrupa por ClienteId.
        builder.HasIndex(r => new { r.TenantId, r.EmpresaId });

        // FK real solo del ancla nueva: ClienteId nunca la tuvo (ver la
        // migración original, AgregarReclamacionDocumental) y añadírsela ahora
        // sería una corrección de integridad sobre datos existentes, ajena a
        // este incremento — queda registrada como hallazgo, no resuelta de
        // paso. Con EmpresaId en null Postgres no comprueba esta FK
        // (MATCH SIMPLE), igual que en DocumentoConfiguration.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.EmpresaId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict, como el resto de referencias entre agregados de este
        // repositorio: la reclamación es append-only (es el historial de qué se
        // reclamó y cuándo), así que perder su vínculo con la conversación en
        // cascada sería perder rastro. Con soft delete la cascada no llegaría a
        // dispararse casi nunca, pero la regla la fija el modelo, no el uso.
        builder.HasOne<Conversacion>()
            .WithMany()
            .HasForeignKey(r => r.ConversacionId)
            .OnDelete(DeleteBehavior.Restrict);

        // Mensajes/Participantes de Conversacion es el mismo patrón: colección
        // de solo lectura respaldada por campo privado, EF lee/escribe el
        // campo directamente.
        builder.HasMany(r => r.Documentos)
            .WithOne()
            .HasForeignKey(d => d.ReclamacionDocumentalId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(r => r.Documentos).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
