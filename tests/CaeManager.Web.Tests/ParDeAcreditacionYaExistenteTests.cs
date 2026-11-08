using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Qué choque de unicidad trata <c>CaeManagerDbContext.SaveChangesAsync</c> como «el par ya existe»: solo el índice
/// único (TenantId, DocumentoId, CanalGestionDocumentalId) de las acreditaciones de plataforma, con el nombre que
/// PostgreSQL le da (truncado por EF a 63 caracteres). Cualquier otra violación sigue siendo un error. El reintento con
/// la base real lo prueba <c>AltaAcreditacionesBajoRuntimeTests</c> en la suite de integración.
/// </summary>
public class ParDeAcreditacionYaExistenteTests
{
    private const string IndiceDelPar = "IX_AcreditacionesDocumentoPlataforma_TenantId_DocumentoId_Cana~";

    private static DbUpdateException Choque(string sqlState, string? restriccion) =>
        new("Fallo al guardar", new PostgresException(
            "duplicate key value violates unique constraint", "ERROR", "ERROR", sqlState, constraintName: restriccion));

    [Fact]
    public void El_indice_unico_del_par_se_trata_como_ya_existente() =>
        CaeManagerDbContext.EsParDeAcreditacionYaExistente(Choque(PostgresErrorCodes.UniqueViolation, IndiceDelPar))
            .Should().BeTrue();

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation, "IX_Empresas_TenantId_Cif")]
    [InlineData(PostgresErrorCodes.UniqueViolation, "IX_AcreditacionesDocumentoPlataforma_TenantId_DocumentoId")]
    // Mismo prefijo que el índice del par, otra restricción.
    [InlineData(PostgresErrorCodes.UniqueViolation, "IX_AcreditacionesDocumentoPlataforma_TenantId_DocumentoId_Cana_Otro")]
    [InlineData(PostgresErrorCodes.UniqueViolation, "IX_AcreditacionesDocumentoPlataforma_TenantId_DocumentoId_Cana")]
    [InlineData(PostgresErrorCodes.UniqueViolation, null)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation, IndiceDelPar)]
    public void Otro_choque_sigue_siendo_un_error(string sqlState, string? restriccion) =>
        CaeManagerDbContext.EsParDeAcreditacionYaExistente(Choque(sqlState, restriccion)).Should().BeFalse();

    [Fact]
    public void Un_fallo_que_no_viene_de_PostgreSQL_sigue_siendo_un_error() =>
        CaeManagerDbContext.EsParDeAcreditacionYaExistente(new DbUpdateException("Fallo al guardar", new TimeoutException()))
            .Should().BeFalse();
}
