using CaeManager.Domain.CalendarioLaboral;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

/// <summary>
/// Calendario laboral de una persona: dato del Tenant de su cuenta, con el filtro de Tenant por reflexión
/// (<c>EntidadConTenant</c>) y la RLS estándar <c>aislamiento_tenant</c> con FORCE de la migración.
/// </summary>
public class CalendarioLaboralPersonaConfiguration : IEntityTypeConfiguration<CalendarioLaboralPersona>
{
    public void Configure(EntityTypeBuilder<CalendarioLaboralPersona> builder)
    {
        builder.ToTable("CalendariosLaboralesPersona", t =>
            t.HasCheckConstraint("CK_CalendariosLaboralesPersona_MunicipioConComunidad",
                "\"Municipio\" IS NULL OR \"Comunidad\" IS NOT NULL"));
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Municipio).HasMaxLength(FestivoOficial.LongitudMaximaMunicipio);
        builder.Ignore(c => c.Perfil);

        // Un calendario por cuenta y Tenant.
        builder.HasIndex(c => new { c.TenantId, c.UsuarioId }).IsUnique();
    }
}
