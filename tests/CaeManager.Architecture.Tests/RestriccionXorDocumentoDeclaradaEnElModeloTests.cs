using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// DCR-19: <c>CK_Documentos_PropietarioXor</c> vivió solo en SQL crudo
/// (migración <c>RendimientoBusquedasYCheckXorDocumento</c>, 2026-08-01)
/// hasta que <c>DocumentoConfiguration</c> pasó a declararla. Este test fija
/// esa declaración en el modelo EF — nombre y expresión exactos, letra por
/// letra salvo espacios, contra la expresión de la constraint real — para
/// que una futura reconfiguración de la tabla <c>Documentos</c> no pueda
/// perderla en silencio: si alguien la quita de
/// <see cref="CaeManager.Infrastructure.Persistence.Configurations.DocumentoConfiguration"/>,
/// este test falla Y "dotnet ef migrations has-pending-model-changes"
/// también, porque el modelo dejaría de coincidir con el snapshot.
///
/// No necesita una base de datos real: <see cref="DbContext.Model"/> se
/// construye a partir de las <c>IEntityTypeConfiguration</c> registradas, sin
/// abrir conexión — <c>UseNpgsql</c> con una cadena inalcanzable basta.
/// </summary>
public class RestriccionXorDocumentoDeclaradaEnElModeloTests
{
    private const string ExpresionEsperada =
        "num_nonnulls(\"TrabajadorId\", \"ClienteId\", \"EmpresaId\", \"VehiculoId\", \"ProyectoId\") = 1";

    [Fact]
    public void El_modelo_EF_declara_CK_Documentos_PropietarioXor_con_la_expresion_de_la_constraint_real()
    {
        using var contexto = CrearContextoSinConexion();

        // Los check constraints son metadata solo de diseño: el modelo
        // "read-optimized" de tiempo de ejecución (DbContext.Model) no los
        // conserva porque nada en tiempo de ejecución los necesita —
        // PostgreSQL ya los aplica por su cuenta. IDesignTimeModel es el
        // mismo modelo que usa "dotnet ef migrations", así que es el sitio
        // correcto para comprobar esta declaración.
        var modelo = contexto.GetService<IDesignTimeModel>().Model;
        var entityType = modelo.FindEntityType(typeof(Documento));
        entityType.Should().NotBeNull("Documento debe seguir mapeado por CaeManagerDbContext");

        var constraint = entityType!.GetCheckConstraints()
            .SingleOrDefault(c => c.Name == "CK_Documentos_PropietarioXor");

        constraint.Should().NotBeNull(
            "CK_Documentos_PropietarioXor debe seguir declarada en DocumentoConfiguration — si se " +
            "pierde en una reconfiguración de la tabla, el modelo dejaría de saber que Documento tiene " +
            "exactamente un propietario, aunque PostgreSQL lo siga exigiendo por su cuenta");

        constraint!.Sql.Should().Be(ExpresionEsperada,
            "la expresión declarada en el modelo debe ser letra por letra la misma que " +
            "RendimientoBusquedasYCheckXorDocumento escribió en la base — dos constraints con el mismo " +
            "nombre y distinta expresión serían una divergencia silenciosa entre modelo y base real");
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
