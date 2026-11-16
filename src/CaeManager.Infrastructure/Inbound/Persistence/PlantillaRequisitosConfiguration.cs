using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Inbound.CatalogoBase;
using CaeManager.Domain.Inbound.Requisitos;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Inbound.Persistence;

/// <summary>
/// Plantillas de requisitos del Tenant TCE (B-14). Las invariantes de <see cref="PlantillaRequisitos"/> se repiten en
/// CHECK: una escritura que se salte el dominio tampoco deja una plantilla sin referencia o con dos.
/// </summary>
public class PlantillaRequisitosConfiguration : IEntityTypeConfiguration<PlantillaRequisitos>
{
    private const string Tipo = nameof(PlantillaRequisitos.Tipo);

    public void Configure(EntityTypeBuilder<PlantillaRequisitos> builder)
    {
        builder.ToTable("PlantillasRequisitos", t =>
        {
            t.HasCheckConstraint("CK_PlantillasRequisitos_Tipo", ContratoAccesoConfiguration.EnValoresDe<TipoPlantillaRequisitos>(Tipo));
            t.HasCheckConstraint(
                "CK_PlantillasRequisitos_UnaReferenciaSegunTipo",
                $"(\"{Tipo}\" = '{nameof(TipoPlantillaRequisitos.PorActividad)}') = (\"{nameof(PlantillaRequisitos.ActividadId)}\" IS NOT NULL) " +
                $"AND (\"{Tipo}\" = '{nameof(TipoPlantillaRequisitos.PorRiesgo)}') = (\"{nameof(PlantillaRequisitos.RiesgoId)}\" IS NOT NULL) " +
                $"AND (\"{Tipo}\" = '{nameof(TipoPlantillaRequisitos.DeCentro)}') = (\"{nameof(PlantillaRequisitos.CentroId)}\" IS NOT NULL) " +
                $"AND (\"{Tipo}\" = '{nameof(TipoPlantillaRequisitos.ExigenciasClienteEmpresarial)}') = (\"{nameof(PlantillaRequisitos.ClienteEmpresarialId)}\" IS NOT NULL)");
        });
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Tipo).HasConversion<string>().HasMaxLength(40);

        builder.HasMany(p => p.Requisitos)
            .WithOne()
            .HasForeignKey(r => new { r.TenantId, r.PlantillaId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Requisitos).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Una plantilla activa por referencia y Tenant. La retirada no cuenta: deja crear la que la sustituye.
        builder.HasIndex(p => new { p.TenantId, p.ActividadId }).IsUnique()
            .HasFilter($"\"{nameof(PlantillaRequisitos.ActividadId)}\" IS NOT NULL AND \"{nameof(PlantillaRequisitos.Activa)}\"");
        builder.HasIndex(p => new { p.TenantId, p.RiesgoId }).IsUnique()
            .HasFilter($"\"{nameof(PlantillaRequisitos.RiesgoId)}\" IS NOT NULL AND \"{nameof(PlantillaRequisitos.Activa)}\"");
        builder.HasIndex(p => new { p.TenantId, p.CentroId }).IsUnique()
            .HasFilter($"\"{nameof(PlantillaRequisitos.CentroId)}\" IS NOT NULL AND \"{nameof(PlantillaRequisitos.Activa)}\"");
        builder.HasIndex(p => new { p.TenantId, p.ClienteEmpresarialId }).IsUnique()
            .HasFilter($"\"{nameof(PlantillaRequisitos.ClienteEmpresarialId)}\" IS NOT NULL AND \"{nameof(PlantillaRequisitos.Activa)}\"");

        // El catálogo base es global (sin Tenant): FK simple. El Centro y el Cliente empresarial, del mismo Tenant.
        builder.HasOne<ActividadCatalogo>().WithMany().HasForeignKey(p => p.ActividadId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RiesgoCatalogo>().WithMany().HasForeignKey(p => p.RiesgoId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.CentroId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.ClienteEmpresarialId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

public class RequisitoPlantillaConfiguration : IEntityTypeConfiguration<RequisitoPlantilla>
{
    public void Configure(EntityTypeBuilder<RequisitoPlantilla> builder)
    {
        var vigencia = nameof(RequisitoPlantilla.VigenciaMeses);
        var tolerancia = nameof(RequisitoPlantilla.ToleranciaDias);
        var modo = nameof(RequisitoPlantilla.ModoVigencia);
        var contratacion = nameof(RequisitoPlantilla.TiposContratacion);

        builder.ToTable("RequisitosPlantilla", t =>
        {
            t.HasCheckConstraint("CK_RequisitosPlantilla_Entidad", ContratoAccesoConfiguration.EnValoresDe<EntidadRequisito>(nameof(RequisitoPlantilla.Entidad)));
            t.HasCheckConstraint("CK_RequisitosPlantilla_Origen", ContratoAccesoConfiguration.EnValoresDe<OrigenRequisito>(nameof(RequisitoPlantilla.Origen)));
            t.HasCheckConstraint("CK_RequisitosPlantilla_ModoVigencia", ContratoAccesoConfiguration.EnValoresDe<ModoVigenciaRequisito>(modo));
            t.HasCheckConstraint("CK_RequisitosPlantilla_SePideA", ContratoAccesoConfiguration.EnValoresDe<DestinatarioRequisito>(nameof(RequisitoPlantilla.SePideA)));
            t.HasCheckConstraint(
                "CK_RequisitosPlantilla_AmbitoDocumento",
                ContratoAccesoConfiguration.EnValoresDe<AmbitoDocumentoRequisito>(nameof(RequisitoPlantilla.AmbitoDocumento)));
            t.HasCheckConstraint(
                "CK_RequisitosPlantilla_VigenciaSiFija",
                $"(\"{modo}\" = '{nameof(ModoVigenciaRequisito.Fija)}') = (\"{vigencia}\" IS NOT NULL) " +
                $"AND (\"{vigencia}\" IS NULL OR \"{vigencia}\" BETWEEN 1 AND {AtributosRequisito.VigenciaMaximaMeses})");
            t.HasCheckConstraint(
                "CK_RequisitosPlantilla_Tolerancia",
                $"\"{tolerancia}\" IS NULL OR (\"{tolerancia}\" BETWEEN 1 AND {AtributosRequisito.ToleranciaMaximaDias} " +
                $"AND \"{modo}\" <> '{nameof(ModoVigenciaRequisito.SinVencimiento)}')");
            t.HasCheckConstraint(
                "CK_RequisitosPlantilla_TiposContratacion",
                $"\"{contratacion}\" > 0 AND (\"{contratacion}\" & ~{(int)TiposContratacion.Todos}) = 0");
            t.HasCheckConstraint(
                "CK_RequisitosPlantilla_PropioSinBase",
                $"\"{nameof(RequisitoPlantilla.Origen)}\" <> '{nameof(OrigenRequisito.PropioTenant)}' " +
                $"OR \"{nameof(RequisitoPlantilla.RequisitoBaseId)}\" IS NULL");
            t.HasCheckConstraint(
                "CK_RequisitosPlantilla_Equivalencias",
                $"cardinality(\"{nameof(RequisitoPlantilla.Equivalencias)}\") <= {AtributosRequisito.MaximoEquivalencias}");
        });
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Entidad).HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.Origen).HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.ModoVigencia).HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.SePideA).HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.AmbitoDocumento).HasConversion<string>().HasMaxLength(30);
        // Máscara de bits, entera como TipoDocumento.TiposContratacionDeSerie.
        builder.Property(r => r.TiposContratacion).HasConversion<int>().IsRequired();
        builder.Property(r => r.Criterio).HasMaxLength(AtributosRequisito.LongitudMaximaCriterio);
        builder.Property<List<string>>("_equivalencias")
            .HasColumnName(nameof(RequisitoPlantilla.Equivalencias))
            .HasColumnType("text[]")
            .IsRequired();
        builder.Ignore(r => r.Equivalencias);

        // Un tipo de documento, una vez por plantilla mientras su requisito esté activo.
        builder.HasIndex(r => new { r.TenantId, r.PlantillaId, r.TipoDocumentoId }).IsUnique()
            .HasFilter($"\"{nameof(RequisitoPlantilla.Activo)}\"");
        builder.HasIndex(r => new { r.TenantId, r.TipoDocumentoId });

        builder.HasOne<TipoDocumento>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.TipoDocumentoId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RequisitoBase>().WithMany().HasForeignKey(r => r.RequisitoBaseId).OnDelete(DeleteBehavior.Restrict);
    }
}
