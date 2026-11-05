using System.Text.RegularExpressions;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
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
        TextoUp().Should().Contain($"\"{restriccion}\"").And.Contain(esperado.Replace("\"", "\\\""));
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
            .And.Contain("\"CK_ProductosContratados_PagadorNoVacio\"");
    }

    [Fact]
    public void Ningun_indice_unico_decide_cuantos_Productos_Contratados_tiene_un_Tenant()
    {
        Modelo.FindEntityType(typeof(ProductoContratado))!.GetIndexes().Should().OnlyContain(i => !i.IsUnique,
            "la cardinalidad por Tenant beneficiario no está decidida; añadirla después es aditivo");
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
            "ningún rol de la aplicación escribe el Producto Contratado hasta que A6 abra la vía");
        up.Should().NotContain("UpdateData").And.NotContain("InsertData").And.NotContain("AddColumn",
            "aditiva y compatible con la ranura anterior: solo una tabla nueva");
    }

    private static string TextoUp()
    {
        var ruta = Path.Combine(Raiz(), "src", "CaeManager.Migrations.PostgreSQL", "Migrations", Migracion);
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
