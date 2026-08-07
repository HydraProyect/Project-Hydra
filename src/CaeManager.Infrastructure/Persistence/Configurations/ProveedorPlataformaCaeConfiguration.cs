using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ProveedorPlataformaCaeConfiguration : IEntityTypeConfiguration<ProveedorPlataformaCae>
{
    public void Configure(EntityTypeBuilder<ProveedorPlataformaCae> builder)
    {
        builder.ToTable("ProveedoresPlataformaCae");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Codigo).IsRequired().HasMaxLength(ProveedorPlataformaCae.LongitudMaximaCodigo);
        builder.Property(p => p.Nombre).IsRequired().HasMaxLength(ProveedorPlataformaCae.LongitudMaximaNombre);
        builder.Property(p => p.Grupo).HasMaxLength(ProveedorPlataformaCae.LongitudMaximaGrupo);

        builder.HasIndex(p => p.Codigo).IsUnique();

        // Sin HasQueryFilter: catálogo global del producto, no pertenece a
        // ningún tenant (mismo criterio que Tenant — ver ProveedorPlataformaCae.cs).
        builder.HasData(ProveedorPlataformaCaeSeedData.Datos
            .Select(d => new { Id = d.Id, Codigo = d.Codigo, Nombre = d.Nombre, Grupo = d.Grupo, Activo = d.Activo }));
    }
}

public class DominioProveedorPlataformaCaeConfiguration : IEntityTypeConfiguration<DominioProveedorPlataformaCae>
{
    public void Configure(EntityTypeBuilder<DominioProveedorPlataformaCae> builder)
    {
        builder.ToTable("DominiosProveedorPlataformaCae");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Dominio).IsRequired().HasMaxLength(DominioProveedorPlataformaCae.LongitudMaximaDominio);

        builder.HasIndex(d => d.Dominio);

        // Id determinista derivado del proveedor + índice del dominio dentro de
        // su fila de semilla — HasData exige valores fijos (no Guid.NewGuid())
        // y así se evita escribir a mano ~30 literales más en el seed.
        builder.HasData(ProveedorPlataformaCaeSeedData.Datos
            .SelectMany(p => p.Dominios.Select((dominio, indice) => new
            {
                Id = DerivarIdDominio(p.Id, indice),
                ProveedorPlataformaCaeId = p.Id,
                Dominio = dominio
            })));
    }

    /// <summary>
    /// Id determinista y legible: mismo Guid del proveedor con el primer
    /// dígito del primer grupo re-etiquetado "7…" (namespace de dominios,
    /// distinto del "6…" de proveedores — evita colisión de Id entre las dos
    /// tablas) y el segundo grupo (siempre "0000" en la semilla de
    /// proveedores, sin usar) sustituido por el índice del dominio dentro de
    /// esa fila. El último grupo —el que distingue a cada proveedor entre sí—
    /// no se toca: sustituir un sufijo corto en vez de esto colisionaba entre
    /// proveedores consecutivos (…001 y …002 acababan en el mismo Id de
    /// dominio para el mismo índice).
    /// </summary>
    private static Guid DerivarIdDominio(Guid proveedorId, int indice)
    {
        var grupos = proveedorId.ToString().Split('-');
        grupos[0] = "7" + grupos[0][1..];
        grupos[1] = indice.ToString("D4");
        return Guid.Parse(string.Join('-', grupos));
    }
}
