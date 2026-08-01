using CaeManager.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        // AspNetUsers es la única tabla sin filtro global (ver ApplicationUser.TenantId
        // y DirectorioUsuariosTenant), pero todo listado por organización —incluida la
        // pantalla /usuarios y cualquier "dame de alta todos mis usuarios" que pida una
        // Consultora— filtra por esta columna. Sin índice, ese filtro barre la tabla
        // entera en cada carga.
        builder.HasIndex(u => u.TenantId);
    }
}
