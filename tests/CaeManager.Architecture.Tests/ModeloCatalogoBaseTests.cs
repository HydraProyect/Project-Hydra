using System.Text.RegularExpressions;
using CaeManager.Domain.CatalogoBase;
using CaeManager.Domain.Common;
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
/// Catálogo base de Plataforma (B-13; CA-2, CA-4, CA-7), medido sobre el modelo real de EF y sobre el texto de la
/// migración que lo crea: tablas globales sin Tenant, sin RLS (la excepción documentada en
/// <c>CoberturaRlsDelModeloTests.CatalogoBaseDePlataformaSinRls</c>), sin contenido y con <c>cae_app_runtime</c>
/// solo con SELECT. Lo que la base hace de verdad con esos privilegios lo prueba <c>CatalogoBaseBajoRuntimeTests</c>
/// en integración.
/// </summary>
public class ModeloCatalogoBaseTests
{
    private const string Migracion = "20261114020222_AnadeCatalogoBasePlataforma.cs";

    /// <summary>Las tablas del catálogo base, por entidad. La lista es exacta: una tabla nueva del catálogo entra aquí.</summary>
    public static readonly IReadOnlyDictionary<Type, string> Tablas = new Dictionary<Type, string>
    {
        [typeof(ActividadCatalogo)] = "CatalogoActividades",
        [typeof(CodigoOficialActividad)] = "CatalogoActividadesCodigosOficiales",
        [typeof(RiesgoCatalogo)] = "CatalogoRiesgos",
        [typeof(RequisitoBase)] = "CatalogoRequisitosBase",
        [typeof(ReglaRiesgoRequisito)] = "CatalogoReglasRiesgoRequisito",
    };

    private static readonly IModel Modelo = CrearContextoSinConexion().Model;

    /// <summary>Los CHECK solo constan en el modelo de diseño, no en el optimizado para leer.</summary>
    private static readonly IModel ModeloDiseno = CrearContextoSinConexion().GetService<IDesignTimeModel>().Model;

    [Fact]
    public void El_catalogo_son_exactamente_estas_tablas_sin_Tenant_ni_filtro_de_Tenant()
    {
        Modelo.GetEntityTypes().Where(t => t.ClrType.Namespace == typeof(ActividadCatalogo).Namespace)
            .Select(t => t.ClrType).Should().BeEquivalentTo(Tablas.Keys);

        foreach (var (tipo, tabla) in Tablas)
        {
            var entidad = Modelo.FindEntityType(tipo)!;
            entidad.GetTableName().Should().Be(tabla);
            tipo.IsAssignableTo(typeof(EntidadConTenant)).Should().BeFalse(tabla);
            entidad.FindProperty(nameof(EntidadConTenant.TenantId)).Should().BeNull(tabla);
            entidad.GetDeclaredQueryFilters().Should().BeEmpty($"{tabla} es global: no hay Tenant por el que filtrar");
            // HasData solo consta en el modelo de diseño: en el de lectura estaría vacío siempre.
            ModeloDiseno.FindEntityType(tipo)!.GetSeedData().Should().BeEmpty($"{tabla} no lleva contenido hasta la revisión del técnico habilitado (CA-7)");
        }
    }

    [Fact]
    public void Los_enumerados_se_guardan_por_nombre_con_un_CHECK_que_la_migracion_crea()
    {
        foreach (var (tipo, restriccion, enumeracion, columna) in new[]
                 {
                     (typeof(CodigoOficialActividad), "CK_CatalogoActividadesCodigosOficiales_Sistema", typeof(SistemaCodigoOficial), "Sistema"),
                     (typeof(RequisitoBase), "CK_CatalogoRequisitosBase_Nivel", typeof(NivelExigencia), "Nivel"),
                 })
        {
            var check = ModeloDiseno.FindEntityType(tipo)!.GetCheckConstraints()
                .Should().ContainSingle(c => c.ModelName == restriccion).Subject;
            var esperado = $"\"{columna}\" IN ({string.Join(", ", Enum.GetNames(enumeracion).Select(n => $"'{n}'"))})";
            check.Sql.Should().Be(esperado);
            TextoUp().Should().Contain($"\"{restriccion}\"").And.Contain(esperado.Replace("\"", "\\\""));
        }
    }

    [Fact]
    public void Los_codigos_son_unicos_y_solo_hay_una_opcion_otro()
    {
        foreach (var tipo in new[] { typeof(ActividadCatalogo), typeof(RiesgoCatalogo), typeof(RequisitoBase) })
            Modelo.FindEntityType(tipo)!.GetIndexes()
                .Should().Contain(i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "Codigo" }), tipo.Name);

        var otro = Modelo.FindEntityType(typeof(RiesgoCatalogo))!.GetIndexes()
            .Should().ContainSingle(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(RiesgoCatalogo.EsOtro) })).Subject;
        otro.IsUnique.Should().BeTrue();
        otro.GetFilter().Should().Be("\"EsOtro\"", "el índice solo cuenta la fila «otro»: los demás riesgos llevan false");

        Modelo.FindEntityType(typeof(ReglaRiesgoRequisito))!.GetIndexes()
            .Should().Contain(i => i.IsUnique && i.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { nameof(ReglaRiesgoRequisito.RiesgoId), nameof(ReglaRiesgoRequisito.RequisitoId) }));
        Modelo.FindEntityType(typeof(CodigoOficialActividad))!.GetIndexes()
            .Should().Contain(i => i.IsUnique && i.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { nameof(CodigoOficialActividad.ActividadId), nameof(CodigoOficialActividad.Sistema), nameof(CodigoOficialActividad.Codigo) }));
    }

    [Fact]
    public void La_migracion_no_activa_RLS_y_solo_concede_SELECT_al_runtime()
    {
        var up = Regex.Replace(TextoUp(), @"\s+", " ");
        var listaTablas = string.Join(", ", Tablas.Values.Select(t => $"public.\"{t}\""));

        up.Should().Contain($"REVOKE ALL ON TABLE {listaTablas} FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;")
            .And.Contain($"GRANT SELECT ON TABLE {listaTablas} TO cae_app_runtime;");

        Regex.Matches(up, @"\bGRANT\b").Should().HaveCount(1,
            "ningún rol de la aplicación escribe el catálogo base: quién lo mantiene llega en otro incremento");
        Regex.Matches(up, @"\bREVOKE\b").Should().HaveCount(1);
        up.Should().NotContain("ROW LEVEL SECURITY", "es la excepción a RLS documentada: global, sin TenantId")
            .And.NotContain("CREATE POLICY")
            .And.NotContain("SECURITY DEFINER");
        up.Should().NotContain("InsertData").And.NotContain("UpdateData").And.NotContain("AddColumn")
            .And.NotContain("DropTable").And.NotContain("AlterColumn").And.NotContain("INSERT INTO",
                "aditiva y compatible con la ranura anterior: solo tablas nuevas, sin contenido (CA-7)");

        foreach (var tabla in Tablas.Values)
            up.Should().Contain($"name: \"{tabla}\"", "la migración crea cada tabla del catálogo");
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
