using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Migrations.PostgreSQL;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// M-27 (ASVS 4.0.3 V4.1.3, V4.2.1): la base impide que una fila hija apunte a un padre de otro Tenant. Toda FK entre
/// dos entidades con <c>TenantId</c> empareja el <c>TenantId</c> del hijo con el del padre, salvo las de
/// <see cref="Excepciones"/>, una lista cerrada en la que cada entrada lleva su motivo. La aplicación y la RLS ya
/// filtran, pero las comprobaciones de FK de PostgreSQL no pasan por la RLS: sin esto, un Id ajeno colado en un comando
/// creaba una fila cruzada sin error.
/// </summary>
public class ClavesForaneasCompuestasConTenantTests
{
    private const string Tenant = "TenantId";

    /// <summary>
    /// Relaciones en las que el padre puede ser legítimamente de otro Tenant: (dependiente, columna FK, principal) y
    /// motivo. Hoy ninguna: las 34 que quedaban simples son de un mismo Tenant (el catálogo de tipos de documento también,
    /// porque cada Tenant recibe su copia, ver <c>TipoDocumentoSeedData.CrearCopiasParaTenant</c>). Añadir una entrada es
    /// una decisión de aislamiento: exige el motivo y la revisión de esa superficie.
    /// </summary>
    private static readonly Dictionary<(string Dependiente, string Columna, string Principal), string> Excepciones = [];

    [Fact]
    public void Toda_FK_entre_entidades_con_TenantId_empareja_el_TenantId_del_hijo_con_el_del_padre()
    {
        var relaciones = RelacionesEntreTenants().ToList();

        relaciones.Count.Should().BeGreaterThan(100,
            "si el recorrido del modelo no encuentra las relaciones, el invariante no comprobaría nada");

        var simples = relaciones
            .Where(fk => !EmparejaTenant(fk))
            .Select(Clave)
            .Where(clave => !Excepciones.ContainsKey(clave))
            .Select(clave => $"{clave.Dependiente}.{clave.Columna} -> {clave.Principal}")
            .Order()
            .ToList();

        simples.Should().BeEmpty(
            "la FK debe llevar TenantId con HasPrincipalKey(x => new { x.TenantId, x.Id }), o entrar en Excepciones con su motivo");
    }

    [Fact]
    public void Una_excepcion_que_ya_no_hace_falta_se_retira_de_la_lista()
    {
        var simples = RelacionesEntreTenants().Where(fk => !EmparejaTenant(fk)).Select(Clave).ToHashSet();

        Excepciones.Keys.Where(clave => !simples.Contains(clave)).Should().BeEmpty(
            "una excepción sin FK simple detrás deja la puerta abierta para la siguiente relación que se llame igual");
        Excepciones.Values.Where(string.IsNullOrWhiteSpace).Should().BeEmpty("toda excepción lleva su motivo");
    }

    /// <summary>
    /// La trampa medida al componer las FKs: una entidad descubierta por una navegación de colección que además es
    /// principal de una FK compuesta lleva <c>TenantId</c> en una clave alternativa. Si su Id cuenta como generado, EF la
    /// clasifica como Modified, el fixup le copia el <c>TenantId</c> del padre y DetectChanges lanza «is part of a key»
    /// antes de que <c>TenantSelladoInterceptor</c> pueda reclasificarla. Solo sale en ejecución, con el segundo hijo de un
    /// padre ya cargado: por eso se vigila aquí, sobre el modelo.
    /// </summary>
    [Fact]
    public void Un_hijo_por_navegacion_que_es_principal_de_una_FK_compuesta_declara_su_Id_como_no_generado()
    {
        var modelo = CrearContextoSinConexion().Model;

        var candidatos = modelo.GetEntityTypes()
            .Where(e => e.GetForeignKeys().Any(fk => fk.PrincipalToDependent is not null && EmparejaTenant(fk)))
            .Where(e => e.GetKeys().Any(k => !k.IsPrimaryKey() && k.Properties.Any(p => p.Name == Tenant)))
            .ToList();

        candidatos.Select(e => e.ClrType.Name).Should().Contain("Mensaje",
            "Mensaje es hoy el caso real: si deja de serlo, este test no está mirando lo que cree");

        candidatos
            .Where(e => e.FindPrimaryKey()!.Properties.Any(p => p.ValueGenerated != ValueGenerated.Never))
            .Select(e => e.ClrType.Name)
            .Should().BeEmpty("hace falta builder.Property(x => x.Id).ValueGeneratedNever(), ver MensajeConfiguration");
    }

    /// <summary>La lista de la comprobación previa de la migración nombra relaciones reales y ya compuestas.</summary>
    [Fact]
    public void La_comprobacion_previa_de_la_migracion_cubre_relaciones_compuestas_del_modelo()
    {
        var compuestas = RelacionesEntreTenants()
            .Where(EmparejaTenant)
            .SelectMany(fk => fk.Properties.Where(p => p.Name != Tenant).Select(p => (
                Hija: fk.DeclaringEntityType.GetTableName(),
                Columna: p.GetColumnName(),
                Padre: fk.PrincipalEntityType.GetTableName())))
            .ToHashSet();

        ComprobacionTenantClavesForaneas.Relaciones.Should().HaveCount(34);
        ComprobacionTenantClavesForaneas.Relaciones
            .Where(r => !compuestas.Contains((r.Hija, r.Columna, r.Padre)))
            .Should().BeEmpty();
    }

    private static IEnumerable<IForeignKey> RelacionesEntreTenants() =>
        CrearContextoSinConexion().Model.GetEntityTypes()
            .Where(e => e.FindProperty(Tenant) is not null)
            .SelectMany(e => e.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType.FindProperty(Tenant) is not null);

    private static (string Dependiente, string Columna, string Principal) Clave(IForeignKey fk) => (
        fk.DeclaringEntityType.ClrType.Name,
        string.Join(",", fk.Properties.Where(p => p.Name != Tenant).Select(p => p.Name)),
        fk.PrincipalEntityType.ClrType.Name);

    /// <summary>Compuesta de verdad: el <c>TenantId</c> del hijo en la misma posición que el del padre, no solo presente.</summary>
    private static bool EmparejaTenant(IForeignKey fk) =>
        fk.Properties.Select(p => p.Name)
            .Zip(fk.PrincipalKey.Properties.Select(p => p.Name))
            .Any(par => par is { First: Tenant, Second: Tenant });

    private static CaeManagerDbContext CrearContextoSinConexion()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=localhost;Database=solo-para-construir-el-modelo;Username=x;Password=x")
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
