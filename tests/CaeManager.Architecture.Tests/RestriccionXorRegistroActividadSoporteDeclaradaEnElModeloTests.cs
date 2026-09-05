using CaeManager.Domain.Soporte;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// REC-208, mismo patrón que <see cref="RestriccionXorDocumentoDeclaradaEnElModeloTests"/>
/// (REC-101): <c>CK_RegistrosActividadSoporte_UnSoloAgrupador</c> tiene que
/// estar declarada en el modelo EF, no solo en la migración que la crea en
/// PostgreSQL — así una futura reconfiguración de
/// <see cref="CaeManager.Infrastructure.Persistence.Configurations.RegistroActividadSoporteConfiguration"/>
/// que la pierda hace fallar este test Y
/// "dotnet ef migrations has-pending-model-changes", en vez de perderse en
/// silencio con la base siguiendo la única que la conoce.
///
/// No necesita una base de datos real: <see cref="DbContext.Model"/> se
/// construye a partir de las <c>IEntityTypeConfiguration</c> registradas, sin
/// abrir conexión — <c>UseNpgsql</c> con una cadena inalcanzable basta.
/// </summary>
public class RestriccionXorRegistroActividadSoporteDeclaradaEnElModeloTests
{
    private const string ExpresionEsperada = "(\"DelegacionTenantId\" IS NULL) <> (\"SesionPrivilegiadaId\" IS NULL)";

    [Fact]
    public void El_modelo_EF_declara_CK_RegistrosActividadSoporte_UnSoloAgrupador_con_la_expresion_de_la_constraint_real()
    {
        using var contexto = CrearContextoSinConexion();

        var modelo = contexto.GetService<IDesignTimeModel>().Model;
        var entityType = modelo.FindEntityType(typeof(RegistroActividadSoporte));
        entityType.Should().NotBeNull("RegistroActividadSoporte debe seguir mapeado por CaeManagerDbContext");

        var constraint = entityType!.GetCheckConstraints()
            .SingleOrDefault(c => c.Name == "CK_RegistrosActividadSoporte_UnSoloAgrupador");

        constraint.Should().NotBeNull(
            "CK_RegistrosActividadSoporte_UnSoloAgrupador debe seguir declarada en " +
            "RegistroActividadSoporteConfiguration — si se pierde en una reconfiguración de la tabla, el " +
            "modelo dejaría de saber que el registro cuelga de exactamente uno de sus dos agrupadores, " +
            "aunque PostgreSQL lo siga exigiendo por su cuenta");

        constraint!.Sql.Should().Be(ExpresionEsperada,
            "la expresión declarada en el modelo debe ser letra por letra la misma que " +
            "AgrupadorXorRegistroActividadSoporte escribió en la base — dos constraints con el mismo " +
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
