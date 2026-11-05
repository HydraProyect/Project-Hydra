using System.Text.RegularExpressions;
using CaeManager.Domain.AltaAutonoma;
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
/// Modelo del alta autónoma (incremento A1), medido sobre el modelo real de EF y sobre el texto de la
/// migración que lo crea. Lo que la base hace de verdad con él lo prueba
/// <c>SolicitudesAltaYCifPropietarioBajoRolesTests</c> en integración.
/// </summary>
public class ModeloAltaAutonomaTests
{
    private const string Migracion = "20261109120000_AnadeSolicitudesAltaYCifPropietario.cs";

    private static readonly IModel Modelo = CrearContextoSinConexion().Model;

    [Fact]
    public void El_CIF_propietario_vive_fuera_de_Tenants_con_un_indice_unico_global()
    {
        var tenant = Modelo.FindEntityType(typeof(Tenant))!;
        tenant.GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Cif"),
            "Tenants la leen los tres roles de la aplicación sin restricción por Tenant (DA-11)");
        tenant.FindProperty(nameof(Tenant.TipoTenant))!.IsNullable.Should().BeTrue();

        var cif = Modelo.FindEntityType(typeof(CifPropietarioTenant))!;
        cif.GetTableName().Should().Be("CifsPropietarioTenant");
        cif.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal(nameof(CifPropietarioTenant.TenantId));
        cif.FindProperty(nameof(CifPropietarioTenant.Cif))!.IsNullable.Should().BeFalse();
        var indice = cif.GetIndexes()
            .Should().ContainSingle(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(CifPropietarioTenant.Cif))
            .Subject;
        indice.IsUnique.Should().BeTrue("DM-5: un solo Tenant propietario por CIF entre todos los Tenants");
        indice.GetFilter().Should().BeNull("un Tenant sin CIF declarado no tiene fila, así que no hace falta filtro");
        cif.GetForeignKeys().Should().ContainSingle(f => f.PrincipalEntityType.ClrType == typeof(Tenant)
            && f.DeleteBehavior == DeleteBehavior.Cascade, "borrar un Tenant no deja su NIF huérfano");
        typeof(CifPropietarioTenant).IsAssignableTo(typeof(CaeManager.Domain.Common.EntidadConTenant)).Should().BeFalse(
            "sin filtro de Tenant ni política de aislamiento: la tabla está cerrada a todos los roles de la aplicación");
        cif.GetDeclaredQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public void La_migracion_crea_el_indice_unico_y_no_rellena_los_Tenants_existentes()
    {
        var up = TextoUp();

        Regex.IsMatch(up,
                @"CreateIndex\(\s*name:\s*""IX_CifsPropietarioTenant_Cif"",\s*table:\s*""CifsPropietarioTenant"",\s*column:\s*""Cif"",\s*unique:\s*true\)",
                RegexOptions.Singleline)
            .Should().BeTrue();
        Regex.IsMatch(up, @"AddColumn<[^>]+>\(\s*name:\s*""Cif", RegexOptions.Singleline)
            .Should().BeFalse("el CIF no entra en Tenants");
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
    [InlineData("CifsPropietarioTenant")]
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

    [Theory]
    [InlineData(" Ana@Ejemplo.es ")]
    [InlineData("josé@ejemplo.es")]
    [InlineData("JOSÉ@ejemplo.es")]
    [InlineData("straße@ejemplo.de")]
    public void El_correo_normalizado_coincide_con_el_de_Identity(string correo)
    {
        var identity = new Microsoft.AspNetCore.Identity.UpperInvariantLookupNormalizer();

        SolicitudAlta.NormalizarCorreo(correo.Trim()).Should().Be(identity.NormalizeEmail(correo.Trim()),
            "la Solicitud de alta y la cuenta del Administrador tienen que comparar igual el mismo correo");
    }

    [Fact]
    public void VersionFila_es_token_de_concurrencia_y_las_aceptaciones_se_cargan_siempre()
    {
        var solicitud = Modelo.FindEntityType(typeof(SolicitudAlta))!;

        solicitud.FindProperty(nameof(SolicitudAlta.VersionFila))!.IsConcurrencyToken.Should().BeTrue(
            "el webhook de pago y el trabajo de caducidad no pueden pisarse");
        solicitud.FindNavigation(nameof(SolicitudAlta.Aceptaciones))!.IsEagerLoaded.Should().BeTrue(
            "sin las aceptaciones cargadas, BorrarDatosPersonales dejaría sus filas con la IP");
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
        // Los CHECK solo viven en el modelo de diseño: el de ejecución no los guarda.
        using var contexto = CrearContextoSinConexion();
        var modeloDiseno = contexto.GetService<IDesignTimeModel>().Model;
        var sql = modeloDiseno.FindEntityType(entidad)!.GetCheckConstraints()
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
