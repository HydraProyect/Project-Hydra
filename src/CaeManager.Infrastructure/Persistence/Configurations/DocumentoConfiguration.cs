using CaeManager.Domain.Clientes;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class DocumentoConfiguration : IEntityTypeConfiguration<Documento>
{
    public void Configure(EntityTypeBuilder<Documento> builder)
    {
        builder.ToTable("Documentos");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.ArchivoUrl).HasMaxLength(Documento.LongitudMaximaArchivoUrl);
        builder.Property(d => d.Comentarios).HasMaxLength(Documento.LongitudMaximaComentarios);

        builder.HasIndex(d => new { d.TrabajadorId, d.TipoDocumentoId });
        builder.HasIndex(d => new { d.ClienteId, d.TipoDocumentoId });
        builder.HasIndex(d => new { d.EmpresaId, d.TipoDocumentoId });
        builder.HasIndex(d => new { d.VehiculoId, d.TipoDocumentoId });
        builder.HasIndex(d => new { d.ProyectoId, d.TipoDocumentoId });
        builder.HasIndex(d => d.FechaVencimiento);

        // FKs reales del propietario polimórfico — ver P0-1 de
        // docs/business/MATURITY_REVIEW.md. Documento tiene exactamente un
        // propietario informado de los cinco (ver Documento.DeTrabajador/
        // DeCliente/DeEmpresa/DeVehiculo/DeProyecto); con las otras cuatro
        // columnas en null, Postgres no comprueba esas FKs (MATCH SIMPLE) —
        // solo se valida la del propietario real.
        builder.HasOne<Trabajador>().WithMany()
            .HasForeignKey(d => new { d.TenantId, d.TrabajadorId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Cliente>().WithMany()
            .HasForeignKey(d => new { d.TenantId, d.ClienteId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(d => new { d.TenantId, d.EmpresaId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Vehiculo>().WithMany()
            .HasForeignKey(d => new { d.TenantId, d.VehiculoId })
            .HasPrincipalKey(v => new { v.TenantId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Proyecto>().WithMany()
            .HasForeignKey(d => new { d.TenantId, d.ProyectoId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TipoDocumento>().WithMany()
            .HasForeignKey(d => new { d.TenantId, d.TipoDocumentoId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Prerequisito de la FK que Alerta declara hacia Documento.
        builder.HasIndex(d => new { d.TenantId, d.Id }).IsUnique();

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
