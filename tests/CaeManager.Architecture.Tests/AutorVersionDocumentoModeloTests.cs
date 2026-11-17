using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Preparación de D-12: cada versión de un documento es una fila de Documentos, y la fila guarda quién la subió en
/// dos columnas opcionales sin FK. Las rellena el nacimiento de la versión (ver <see cref="AutorVersionDocumentoEscritoresTests"/>).
/// </summary>
public class AutorVersionDocumentoModeloTests
{
    private static readonly string[] Columnas =
        [nameof(Documento.SubidoPorUsuarioId), nameof(Documento.SubidoPorActorRealUsuarioId)];

    [Theory]
    [InlineData(nameof(Documento.SubidoPorUsuarioId))]
    [InlineData(nameof(Documento.SubidoPorActorRealUsuarioId))]
    public void El_autor_de_la_version_se_guarda_en_Documentos_como_uuid_que_admite_nulo_y_sin_FK(string nombre)
    {
        using var contexto = CrearContexto();
        var tipo = contexto.Model.FindEntityType(typeof(Documento))!;
        tipo.GetTableName().Should().Be("Documentos");

        var propiedad = tipo.FindProperty(nombre)!;
        propiedad.ClrType.Should().Be(typeof(Guid?));
        propiedad.IsNullable.Should().BeTrue();
        propiedad.GetColumnType().Should().Be("uuid");
        propiedad.GetContainingForeignKeys().Should().BeEmpty(
            "el usuario puede ser de otro Tenant y su cuenta se anonimiza: el identificador es un dato, no una referencia");
        propiedad.GetContainingIndexes().Should().BeEmpty("esta fase no añade índices");

        var setter = typeof(Documento).GetProperty(nombre)!.SetMethod!;
        setter.IsPrivate.Should().BeTrue("solo el propio Documento lo asigna, al nacer la versión");
    }

    [Fact]
    public void La_migracion_solo_anade_las_dos_columnas_opcionales_sin_valor_por_defecto()
    {
        var operaciones = new AnadeAutorVersionDocumento().UpOperations;

        operaciones.Should().HaveCount(Columnas.Length).And.AllBeOfType<AddColumnOperation>(
            "sin backfill, sin FK, sin índices, sin SQL crudo y sin tocar RLS ni funciones");
        operaciones.Cast<AddColumnOperation>().Should().OnlyContain(c =>
            c.Table == "Documentos" && c.IsNullable && c.DefaultValue == null && c.DefaultValueSql == null
            && c.ClrType == typeof(Guid) && c.ColumnType == "uuid");
        operaciones.Cast<AddColumnOperation>().Select(c => c.Name).Should().BeEquivalentTo(Columnas);

        var bajada = new AnadeAutorVersionDocumento().DownOperations;
        bajada.Should().HaveCount(Columnas.Length).And.AllBeOfType<DropColumnOperation>();
        bajada.Cast<DropColumnOperation>().Should().OnlyContain(c => c.Table == "Documentos");
        bajada.Cast<DropColumnOperation>().Select(c => c.Name).Should().BeEquivalentTo(Columnas);
    }

    private static CaeManagerDbContext CrearContexto() => new(
        new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=localhost;Database=solo-para-construir-el-modelo;Username=x;Password=x").Options,
        new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = Guid.NewGuid() });
}
