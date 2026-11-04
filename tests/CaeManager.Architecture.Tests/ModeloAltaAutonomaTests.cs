using System.Text.RegularExpressions;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Modelo del alta autónoma (incremento A1), medido sobre el modelo real de EF y sobre el texto de la
/// migración que lo crea. Lo que la base hace de verdad con él lo prueba
/// <c>SolicitudesAltaYCifPropietarioBajoRolesTests</c> en integración.
/// </summary>
public class ModeloAltaAutonomaTests
{
    private const string Migracion = "20261109120000_AnadeSolicitudesAltaYCifPropietario.cs";

    private static readonly IModel Modelo = CrearContextoSinConexion().Model;

    [Fact]
    public void El_CIF_propietario_tiene_un_indice_unico_global_parcial()
    {
        var tenant = Modelo.FindEntityType(typeof(Tenant))!;
        var indice = tenant.GetIndexes()
            .Should().ContainSingle(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(Tenant.CifPropietario))
            .Subject;

        indice.IsUnique.Should().BeTrue("DM-5: un solo Tenant propietario por CIF");
        indice.GetFilter().Should().Be("\"CifPropietario\" IS NOT NULL",
            "los Tenants sin CIF declarado no compiten por la unicidad");
        tenant.FindProperty(nameof(Tenant.CifPropietario))!.IsNullable.Should().BeTrue();
        tenant.FindProperty(nameof(Tenant.TipoTenant))!.IsNullable.Should().BeTrue();
        tenant.GetDeclaredQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public void La_migracion_crea_el_indice_unico_parcial()
    {
        var up = TextoUp();

        Regex.IsMatch(up,
                @"CreateIndex\(\s*name:\s*""IX_Tenants_CifPropietario"",\s*table:\s*""Tenants"",\s*column:\s*""CifPropietario"",\s*unique:\s*true,\s*filter:\s*""\\""CifPropietario\\"" IS NOT NULL""\)",
                RegexOptions.Singleline)
            .Should().BeTrue();
        up.Should().NotContain("migrationBuilder.UpdateData", "las filas existentes de Tenants no se rellenan");
    }

    [Theory]
    [InlineData(typeof(SolicitudAlta), "SolicitudesAlta")]
    [InlineData(typeof(AceptacionCondicionesAlta), "AceptacionesSolicitudAlta")]
    public void Las_solicitudes_de_alta_son_plano_de_Plataforma_sin_TenantId_ni_filtro(Type tipo, string tabla)
    {
        var entidad = Modelo.FindEntityType(tipo)!;

        entidad.GetTableName().Should().Be(tabla);
        entidad.GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("TenantId") && n != nameof(SolicitudAlta.TenantIdCreado));
        entidad.GetDeclaredQueryFilters().Should().BeEmpty();
    }

    [Theory]
    [InlineData("SolicitudesAlta")]
    [InlineData("AceptacionesSolicitudAlta")]
    public void La_migracion_cierra_las_tablas_a_los_roles_de_la_aplicacion(string tabla)
    {
        var up = TextoUp();

        up.Should().Contain($"ALTER TABLE public.\"{tabla}\" ENABLE ROW LEVEL SECURITY;");
        up.Should().Contain($"ALTER TABLE ONLY public.\"{tabla}\" FORCE ROW LEVEL SECURITY;");
        up.Should().Contain(
            $"REVOKE ALL ON TABLE public.\"{tabla}\" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;");
        Regex.IsMatch(up, $@"GRANT\s[^;]*public\.""{tabla}""", RegexOptions.IgnoreCase)
            .Should().BeFalse("ningún rol de la aplicación tiene permiso hasta que el registro abra su vía de acceso");
        Regex.IsMatch(up, $@"CREATE\s+POLICY\s+\w+\s+ON\s+public\.""{tabla}""", RegexOptions.IgnoreCase)
            .Should().BeFalse("sin políticas: con FORCE, ni el rol que tuviera un permiso vería una fila");
    }

    [Fact]
    public void El_Id_de_una_aceptacion_lo_asigna_el_dominio()
    {
        Modelo.FindEntityType(typeof(AceptacionCondicionesAlta))!.FindProperty("Id")!.ValueGenerated
            .Should().Be(ValueGenerated.Never,
                "con ValueGeneratedOnAdd, una aceptación añadida a una Solicitud de alta ya cargada se guardaría como UPDATE");
    }

    [Theory]
    [InlineData(typeof(SolicitudAlta), "CK_SolicitudesAlta_Estado", "Estado", typeof(EstadoSolicitudAlta))]
    [InlineData(typeof(SolicitudAlta), "CK_SolicitudesAlta_Tramo", "Tramo", typeof(TramoTrabajadoresActivos))]
    [InlineData(typeof(SolicitudAlta), "CK_SolicitudesAlta_Modalidad", "Modalidad", typeof(ModalidadSuscripcionAlta))]
    [InlineData(typeof(Tenant), "CK_Tenants_TipoTenant", "TipoTenant", typeof(TipoTenant))]
    public void Cada_enum_guardado_como_texto_tiene_un_CHECK_con_todos_sus_valores(
        Type entidad, string restriccion, string columna, Type tipoEnum)
    {
        var sql = Modelo.FindEntityType(entidad)!.GetCheckConstraints()
            .Should().ContainSingle(c => c.ModelName == restriccion).Subject.Sql;

        sql.Should().Be($"\"{columna}\" IN ({string.Join(", ", Enum.GetNames(tipoEnum).Select(n => $"'{n}'"))})");
        TextoUp().Should().Contain($"\"{restriccion}\"", "la migración crea la restricción")
            .And.Contain(sql.Replace("\"", "\\\""), "con el mismo SQL que el modelo");
    }

    [Fact]
    public void El_hash_del_token_y_la_caducidad_tienen_indice()
    {
        var solicitud = Modelo.FindEntityType(typeof(SolicitudAlta))!;

        var token = solicitud.GetIndexes().Should().ContainSingle(
            i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(SolicitudAlta.HashTokenVerificacion) })).Subject;
        token.IsUnique.Should().BeTrue("un hash de token identifica una sola Solicitud de alta");
        token.GetFilter().Should().Be("\"HashTokenVerificacion\" IS NOT NULL");

        solicitud.GetIndexes().Should().ContainSingle(i => i.Properties.Select(p => p.Name)
            .SequenceEqual(new[] { nameof(SolicitudAlta.Estado), nameof(SolicitudAlta.CaducaEnUtc) }));
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
