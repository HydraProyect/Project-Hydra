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

        // Perfilado con datos sintéticos a escala (20 tenants, ~24k Documentos,
        // EXPLAIN (ANALYZE, BUFFERS) real — auditoría Módulo 8, 2026-08-31):
        // estos cinco SÍ se usan, y solo para el punto de escritura "¿ya existe
        // un Documento de este tipo para este propietario?" (ver
        // ActualizarDocumentoDesdeAdjuntoCommand) — sin el índice, esa consulta
        // cae a (TenantId, OwnerId) y filtra TipoDocumentoId como residual:
        // ~7x más buffers y ~7x más lenta, medido. El listado paginado
        // (ObtenerDocumentosQuery) NUNCA los usa — solo filtra por el
        // propietario, nunca por TipoDocumentoId, así que ese índice no le
        // aporta nada.
        //
        // Deliberadamente SIN TenantId de prefijo, y no por descuido: el
        // propietario (Guid aleatorio, ya casi único por sí solo) no gana
        // selectividad real al anteponerle el tenant, y el EXPLAIN con y sin
        // TenantId da el mismo plan y coste — anteponerlo aquí solo aumentaría
        // el tamaño del índice sin beneficio medido. La recomendación genérica
        // "todo índice debe empezar por TenantId" no es universal; aquí la
        // evidencia real la contradice.
        builder.HasIndex(d => new { d.TrabajadorId, d.TipoDocumentoId });
        builder.HasIndex(d => new { d.ClienteId, d.TipoDocumentoId });
        builder.HasIndex(d => new { d.EmpresaId, d.TipoDocumentoId });
        builder.HasIndex(d => new { d.VehiculoId, d.TipoDocumentoId });
        builder.HasIndex(d => new { d.ProyectoId, d.TipoDocumentoId });

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

        // F3b — ClienteId repunta contra Empresas (ver CentroConfiguration).
        builder.HasOne<Empresa>().WithMany()
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
