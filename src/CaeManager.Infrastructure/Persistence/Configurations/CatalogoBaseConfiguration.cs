using CaeManager.Domain.CatalogoBase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

// Catálogo base de Plataforma (B-13; CA-2, CA-4, CA-7): Actividades con sus códigos oficiales, riesgos, requisitos de
// base y reglas «riesgo → requisito». Cinco tablas globales, sin TenantId ni filtro de Tenant (sus entidades extienden
// Entity), y sin RLS: excepción documentada en CoberturaRlsDelModeloTests.CatalogoBaseDePlataformaSinRls. La migración
// que las crea deja a cae_app_runtime solo con SELECT y a los demás roles de la aplicación sin nada. Sin HasData: el
// contenido espera a la revisión de un técnico habilitado (CA-7).

/// <summary>Actividades del catálogo base: código único, sin borrado (se retiran con <c>Activa</c>).</summary>
public class ActividadCatalogoConfiguration : IEntityTypeConfiguration<ActividadCatalogo>
{
    /// <summary>
    /// CHECK del slug de código en las tres tablas que lo llevan: la base rechaza lo que el dominio rechazaría aunque
    /// la escritura no pase por <see cref="ReglasCatalogoBase.ValidarCodigo"/>.
    /// </summary>
    internal const string FormatoDeCodigo = "\"Codigo\" ~ '" + ReglasCatalogoBase.PatronCodigo + "'";

    public void Configure(EntityTypeBuilder<ActividadCatalogo> builder)
    {
        builder.ToTable("CatalogoActividades", t =>
            t.HasCheckConstraint("CK_CatalogoActividades_CodigoFormato", FormatoDeCodigo));
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Codigo).IsRequired().HasMaxLength(ReglasCatalogoBase.LongitudMaximaCodigo);
        builder.Property(a => a.Nombre).IsRequired().HasMaxLength(ReglasCatalogoBase.LongitudMaximaNombre);
        builder.HasIndex(a => a.Codigo).IsUnique();

        builder.HasMany(a => a.CodigosOficiales).WithOne().HasForeignKey(c => c.ActividadId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(a => a.CodigosOficiales).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Códigos oficiales (CNAE-2025, REGCON, CNO-11) de cada Actividad: cada uno una sola vez por Actividad.</summary>
public class CodigoOficialActividadConfiguration : IEntityTypeConfiguration<CodigoOficialActividad>
{
    public void Configure(EntityTypeBuilder<CodigoOficialActividad> builder)
    {
        builder.ToTable("CatalogoActividadesCodigosOficiales", t =>
        {
            t.HasCheckConstraint("CK_CatalogoActividadesCodigosOficiales_Sistema",
                SolicitudAltaConfiguration.EnValoresDe<SistemaCodigoOficial>("Sistema"));
            t.HasCheckConstraint("CK_CatalogoActividadesCodigosOficiales_CodigoFormato",
                $"\"Codigo\" ~ '{ReglasCatalogoBase.PatronCodigoOficial}'");
        });
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Sistema).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(c => c.Codigo).IsRequired().HasMaxLength(ReglasCatalogoBase.LongitudMaximaCodigoOficial);

        builder.HasIndex(c => new { c.ActividadId, c.Sistema, c.Codigo }).IsUnique();
        // Proponer la Actividad a partir del código oficial de la Empresa o del Trabajador busca por código.
        builder.HasIndex(c => new { c.Sistema, c.Codigo });
    }
}

/// <summary>Riesgos del catálogo cerrado, con como mucho una opción «otro» (índice único parcial).</summary>
public class RiesgoCatalogoConfiguration : IEntityTypeConfiguration<RiesgoCatalogo>
{
    public void Configure(EntityTypeBuilder<RiesgoCatalogo> builder)
    {
        builder.ToTable("CatalogoRiesgos", t =>
            t.HasCheckConstraint("CK_CatalogoRiesgos_CodigoFormato", ActividadCatalogoConfiguration.FormatoDeCodigo));
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Codigo).IsRequired().HasMaxLength(ReglasCatalogoBase.LongitudMaximaCodigo);
        builder.Property(r => r.Nombre).IsRequired().HasMaxLength(ReglasCatalogoBase.LongitudMaximaNombre);
        builder.HasIndex(r => r.Codigo).IsUnique();
        builder.HasIndex(r => r.EsOtro).IsUnique().HasFilter("\"EsOtro\"")
            .HasDatabaseName("IX_CatalogoRiesgos_UnaSolaOpcionOtro");
    }
}

/// <summary>Requisitos de base con su fundamento y su nivel N1/N2/N3.</summary>
public class RequisitoBaseConfiguration : IEntityTypeConfiguration<RequisitoBase>
{
    public void Configure(EntityTypeBuilder<RequisitoBase> builder)
    {
        builder.ToTable("CatalogoRequisitosBase", t =>
        {
            t.HasCheckConstraint("CK_CatalogoRequisitosBase_Nivel", SolicitudAltaConfiguration.EnValoresDe<NivelExigencia>("Nivel"));
            t.HasCheckConstraint("CK_CatalogoRequisitosBase_CodigoFormato", ActividadCatalogoConfiguration.FormatoDeCodigo);
        });
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Codigo).IsRequired().HasMaxLength(ReglasCatalogoBase.LongitudMaximaCodigo);
        builder.Property(r => r.Nombre).IsRequired().HasMaxLength(ReglasCatalogoBase.LongitudMaximaNombre);
        builder.Property(r => r.Fundamento).IsRequired().HasMaxLength(ReglasCatalogoBase.LongitudMaximaFundamento);
        builder.Property(r => r.Nivel).IsRequired().HasConversion<string>().HasMaxLength(2);
        builder.HasIndex(r => r.Codigo).IsUnique();
    }
}

/// <summary>Reglas «riesgo → requisito de base»: de muchos a muchos, cada par una sola vez.</summary>
public class ReglaRiesgoRequisitoConfiguration : IEntityTypeConfiguration<ReglaRiesgoRequisito>
{
    public void Configure(EntityTypeBuilder<ReglaRiesgoRequisito> builder)
    {
        builder.ToTable("CatalogoReglasRiesgoRequisito");
        builder.HasKey(r => r.Id);

        builder.HasOne<RiesgoCatalogo>().WithMany().HasForeignKey(r => r.RiesgoId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RequisitoBase>().WithMany().HasForeignKey(r => r.RequisitoId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.RiesgoId, r.RequisitoId }).IsUnique();
        builder.HasIndex(r => r.RequisitoId);
    }
}
