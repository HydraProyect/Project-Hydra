using System.Text.RegularExpressions;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Configurations;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Producto Contratado mínimo (alta autónoma, A5b; RQ-30, D-6), medido sobre el modelo real de EF y sobre el
/// texto de la migración que lo crea. Lo que la base hace de verdad con él lo prueba
/// <c>ProductosContratadosBajoRuntimeTests</c> en integración, y la forma de su RLS,
/// <c>CoberturaRlsDelModeloTests</c>.
/// </summary>
public class ModeloProductoContratadoTests
{
    private const string Migracion = "20261109150000_AnadeProductosContratados.cs";
    private const string MigracionDeLaLineaInbound = "20261115130821_ContrataLineaInboundPorAprovisionamiento.cs";

    private static readonly IModel Modelo = CrearContextoSinConexion().Model;

    /// <summary>Los CHECK solo constan en el modelo de diseño, no en el optimizado para leer.</summary>
    private static readonly IModel ModeloDiseno = CrearContextoSinConexion().GetService<IDesignTimeModel>().Model;

    [Fact]
    public void Es_una_entidad_del_Tenant_beneficiario_con_filtro_de_Tenant()
    {
        typeof(ProductoContratado).IsAssignableTo(typeof(EntidadConTenant)).Should().BeTrue(
            "el Producto Contratado pertenece al Tenant beneficiario y lo aísla aislamiento_tenant");

        var entidad = Modelo.FindEntityType(typeof(ProductoContratado))!;
        entidad.GetTableName().Should().Be("ProductosContratados");
        entidad.GetDeclaredQueryFilters().Should().NotBeEmpty("el filtro global de Tenant se aplica por reflexión");
        entidad.FindProperty(nameof(EntidadConTenant.TenantId))!.IsNullable.Should().BeFalse();
    }

    [Fact]
    public void No_duplica_el_estado_de_cobro_ni_la_Subscription_del_Tenant()
    {
        var columnas = Modelo.FindEntityType(typeof(ProductoContratado))!.GetProperties().Select(p => p.Name).ToList();

        columnas.Should().NotContain(c => c.Contains("Subscription") || c.Contains("Suscripcion") || c.Contains("Comercial"),
            "Tenant.EstadoComercial sigue siendo la única fuente del estado de cobro (ADR-009)");
        typeof(ProductoContratado).GetProperties().Select(p => p.PropertyType)
            .Should().NotContain(typeof(EstadoComercialTenant)).And.NotContain(typeof(EstadoComercialTenant?));
    }

    [Fact]
    public void Lo_contratado_es_obligatorio_y_solo_el_fin_de_la_vigencia_admite_null()
    {
        var entidad = Modelo.FindEntityType(typeof(ProductoContratado))!;

        foreach (var obligatoria in new[]
                 {
                     nameof(ProductoContratado.Linea), nameof(ProductoContratado.PagadorStripeCustomerId),
                     nameof(ProductoContratado.Tramo), nameof(ProductoContratado.Modalidad),
                     nameof(ProductoContratado.VigenteDesdeUtc), nameof(ProductoContratado.Estado),
                 })
            entidad.FindProperty(obligatoria)!.IsNullable.Should().BeFalse(obligatoria);

        entidad.FindProperty(nameof(ProductoContratado.VigenteHastaUtc))!.IsNullable.Should().BeTrue();
        entidad.FindProperty(nameof(ProductoContratado.PagadorStripeCustomerId))!.GetMaxLength()
            .Should().Be(Modelo.FindEntityType(typeof(Tenant))!.FindProperty(nameof(Tenant.StripeCustomerId))!.GetMaxLength(),
                "el Pagador TALVEG es un Customer de Stripe, como Tenants.StripeCustomerId");
    }

    [Theory]
    [InlineData("CK_ProductosContratados_Linea", typeof(LineaProductoContratado), "Linea")]
    [InlineData("CK_ProductosContratados_Tramo", typeof(TramoTrabajadoresActivos), "Tramo")]
    [InlineData("CK_ProductosContratados_Modalidad", typeof(ModalidadSuscripcionAlta), "Modalidad")]
    [InlineData("CK_ProductosContratados_Estado", typeof(EstadoProductoContratado), "Estado")]
    public void Cada_enumeracion_tiene_un_CHECK_con_todos_sus_nombres_y_la_migracion_lo_crea(string restriccion, Type enumeracion, string columna)
    {
        var check = ModeloDiseno.FindEntityType(typeof(ProductoContratado))!.GetCheckConstraints()
            .Should().ContainSingle(c => c.ModelName == restriccion).Subject;

        var esperado = $"\"{columna}\" IN ({string.Join(", ", Enum.GetNames(enumeracion).Select(n => $"'{n}'"))})";
        check.Sql.Should().Be(esperado);
        TextoUpDeLaUltimaQueCrea(restriccion).Should().Contain(esperado.Replace("\"", "\\\""));
    }

    [Fact]
    public void La_base_ata_el_estado_a_la_vigencia()
    {
        var check = ModeloDiseno.FindEntityType(typeof(ProductoContratado))!.GetCheckConstraints()
            .Should().ContainSingle(c => c.ModelName == "CK_ProductosContratados_EstadoYVigencia").Subject;

        check.Sql.Should().Contain("'Vigente' AND \"VigenteHastaUtc\" IS NULL")
            .And.Contain("'Finalizado' AND \"VigenteHastaUtc\" IS NOT NULL")
            .And.Contain("\"VigenteHastaUtc\" >= \"VigenteDesdeUtc\"");
        TextoUp().Should().Contain("\"CK_ProductosContratados_EstadoYVigencia\"")
            .And.Contain("\"CK_ProductosContratados_PagadorFormato\"");

        ModeloDiseno.FindEntityType(typeof(ProductoContratado))!.GetCheckConstraints()
            .Should().ContainSingle(c => c.ModelName == "CK_ProductosContratados_PagadorFormato").Which.Sql
            .Should().Be("\"PagadorStripeCustomerId\" ~ '^[A-Za-z0-9_]+$'", "el mismo alfabeto que valida el dominio");
    }

    /// <summary>
    /// D-B (2026-11-15): un solo Producto Contratado de la línea Inbound vigente por Tenant beneficiario, y nada más. Las
    /// demás líneas siguen sin cardinalidad decidida, así que el único índice único es este, parcial.
    /// </summary>
    [Fact]
    public void El_unico_indice_unico_es_el_de_un_Producto_Contratado_Inbound_vigente_por_Tenant()
    {
        var unico = Modelo.FindEntityType(typeof(ProductoContratado))!.GetIndexes()
            .Should().ContainSingle(i => i.IsUnique, "la cardinalidad de las demás líneas no está decidida").Subject;

        unico.GetDatabaseName().Should().Be(ProductoContratadoConfiguration.IndiceInboundVigentePorTenant);
        unico.Properties.Select(p => p.Name).Should().Equal(nameof(EntidadConTenant.TenantId));
        unico.GetFilter().Should().Be("\"Linea\" = 'Inbound' AND \"Estado\" = 'Vigente' AND NOT \"EstaEliminado\"",
            "una fila finalizada o eliminada no cuenta, como en el gate DN-7");

        var up = TextoUp(MigracionDeLaLineaInbound);
        up.Should().Contain("\"UX_ProductosContratados_InboundVigentePorTenant\"").And.Contain("unique: true");
    }

    /// <summary>
    /// D-C (2026-11-15): la vía de escritura de la línea Inbound concede INSERT, y nada más, a
    /// <c>cae_app_aprovisionamiento</c>. El runtime y el soporte no ganan nada: con INSERT bajo
    /// <c>aislamiento_tenant</c>, un Tenant podría concederse su propia línea Inbound.
    /// </summary>
    [Fact]
    public void La_via_de_la_linea_Inbound_solo_concede_INSERT_a_aprovisionamiento()
    {
        var up = Regex.Replace(TextoUp(MigracionDeLaLineaInbound), @"\s+", " ");

        Regex.Matches(up, @"\bGRANT\b").Should().ContainSingle();
        up.Should().Contain("GRANT INSERT ON TABLE public.\"ProductosContratados\" TO cae_app_aprovisionamiento;");
        up.Should().NotContain("cae_app_runtime").And.NotContain("cae_app_soporte").And.NotContain("REVOKE",
            "aditiva: no quita nada a la ranura anterior");
        up.Should().NotContain("UpdateData").And.NotContain("InsertData").And.NotContain("AddColumn");
    }

    [Fact]
    public void La_migracion_pone_RLS_forzada_con_aislamiento_tenant_y_solo_concede_lectura()
    {
        var up = Regex.Replace(TextoUp(), @"\s+", " ");

        up.Should().Contain("ALTER TABLE public.\"ProductosContratados\" ENABLE ROW LEVEL SECURITY;")
            .And.Contain("ALTER TABLE ONLY public.\"ProductosContratados\" FORCE ROW LEVEL SECURITY;")
            .And.Contain("CREATE POLICY aislamiento_tenant ON public.\"ProductosContratados\" " +
                         "USING (\"TenantId\" = (SELECT public.app_ctx_tenant_id())) " +
                         "WITH CHECK (\"TenantId\" = (SELECT public.app_ctx_tenant_id()));")
            .And.Contain("REVOKE ALL ON TABLE public.\"ProductosContratados\" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;")
            .And.Contain("GRANT SELECT ON TABLE public.\"ProductosContratados\" TO cae_app_runtime, cae_app_soporte;");

        Regex.Matches(up, @"\bGRANT\b").Should().HaveCount(1,
            "la migración que crea la tabla solo concede lectura; la única escritura la abre ContrataLineaInboundPorAprovisionamiento");
        up.Should().NotContain("UpdateData").And.NotContain("InsertData").And.NotContain("AddColumn",
            "aditiva y compatible con la ranura anterior: solo una tabla nueva");
    }

    /// <summary>
    /// El Up de la última migración que crea el CHECK <paramref name="restriccion"/>: la que lo creó o la que lo amplió
    /// después (AnadeLineaInboundProductoContratado, la línea Inbound). Un CHECK cambiado en el modelo sin migración nueva
    /// sigue fallando, porque el Up más reciente no contiene la expresión nueva.
    /// </summary>
    private static string TextoUpDeLaUltimaQueCrea(string restriccion)
    {
        var carpeta = Path.Combine(Raiz(), "src", "CaeManager.Migrations.PostgreSQL", "Migrations");
        var ultima = Directory.GetFiles(carpeta, "2*.cs")
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Where(f => string.CompareOrdinal(Path.GetFileName(f), Migracion) >= 0)
            .Where(f => File.ReadAllText(f).Contains($"\"{restriccion}\"", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .Last();
        return TextoUp(ultima);
    }

    private static string TextoUp(string migracion = Migracion)
    {
        var ruta = Path.Combine(Raiz(), "src", "CaeManager.Migrations.PostgreSQL", "Migrations", migracion);
        var texto = File.ReadAllText(ruta);
        var inicio = texto.IndexOf("protected override void Up(", StringComparison.Ordinal);
        var fin = texto.IndexOf("protected override void Down(", StringComparison.Ordinal);
        inicio.Should().BePositive();
        fin.Should().BeGreaterThan(inicio);
        return texto[inicio..fin];
    }

    private static string Raiz()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;
        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }

    private static CaeManagerDbContext CrearContextoSinConexion()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=localhost;Database=solo-para-construir-el-modelo;Username=x;Password=x")
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
