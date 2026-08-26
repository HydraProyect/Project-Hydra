using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// F3b-Subcontrata, repunteo de FKs
/// (f3b-subcontrata-inventario-fresco-2026-08-26.md §2,
/// f3b-decision-d2-transicion-acotada-2026-08-25.md §0-2). Mismo patrón que
/// <see cref="F3bClienteRepunteoFksTests"/>: redirigir los 6 escritores de
/// Subcontrata a <c>Empresa</c> sin repuntar estas FKs en el mismo
/// despliegue rompería la creación de Trabajador/Vehiculo/etc. para
/// cualquier Subcontrata nueva con <c>23503</c> reales contra Postgres.
/// </summary>
public class F3bSubcontrataRepunteoFksTests : IAsyncLifetime
{
    private const string MigracionAnteriorAF3a = "EstadoBootstrapPlataforma";
    private const string MigracionF3a = "F3aEmpresasUnificadaPreparacion";
    private const string MigracionF3b = "F3bSubcontrataRepunteoFks";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionAnteriorAF3a);
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData("FK_SubcontratasClientes_Empresas_TenantId_SubcontrataId", "SubcontratasClientes")]
    [InlineData("FK_SubcontratasEmpresas_Empresas_TenantId_SubcontrataId", "SubcontratasEmpresas")]
    [InlineData("FK_Trabajadores_Empresas_TenantId_SubcontrataId", "Trabajadores")]
    [InlineData("FK_Vehiculos_Empresas_TenantId_SubcontrataId", "Vehiculos")]
    [InlineData("FK_VerificacionesExternaSubcontrata_Empresas_TenantId_Subcontr~", "VerificacionesExternaSubcontrata")]
    [InlineData("FK_ContactosAgenda_Empresas_SubcontrataId", "ContactosAgenda")]
    public async Task Las_6_FKs_del_inventario_apuntan_a_Empresas_tras_el_repunteo(string nombreConstraint, string tabla)
    {
        _ = tabla;
        var tenantId = Guid.NewGuid();
        await AplicarMigracionF3aAsync(tenantId);
        await AplicarMigracionF3bAsync(tenantId);

        var tablaReferenciada = await ObtenerTablaReferenciadaAsync(nombreConstraint);
        tablaReferenciada.Should().Be("\"Empresas\"",
            $"tras F3b-Subcontrata, {nombreConstraint} debe repuntar contra Empresas, no contra Subcontratas");
    }

    [Fact]
    public async Task Un_Trabajador_de_una_Subcontrata_creada_solo_en_Empresas_ya_funciona_tras_el_repunteo()
    {
        // Reproduce el hallazgo del inventario: una Subcontrata creada
        // DESPUÉS de que F3b-Subcontrata redirigiera los escritores solo
        // existe en Empresas — nunca se escribe en Subcontratas. Antes del
        // repunteo, esto fallaba con 23503.
        var tenantId = Guid.NewGuid();
        await AplicarMigracionF3aAsync(tenantId);
        await AplicarMigracionF3bAsync(tenantId);

        await using var contexto = CrearContexto(tenantId);
        var subcontrataNueva = Empresa.CrearComoSubcontrata(
            "Subcontrata Post-Freeze S.L.", "B10380194", NivelServicioSubcontrata.Gestionada.ToString());
        contexto.Empresas.Add(subcontrataNueva);
        await contexto.SaveChangesAsync();

        var trabajador = Trabajador.DeSubcontrata(subcontrataNueva.Id, "Ana", "Post Freeze", "12345678Z");
        contexto.Trabajadores.Add(trabajador);
        var accion = async () => await contexto.SaveChangesAsync();

        await accion.Should().NotThrowAsync(
            "tras el repunteo, SubcontrataId del Trabajador debe validarse contra Empresas, donde la subcontrata nueva sí existe");
    }

    [Fact]
    public async Task Un_Trabajador_de_una_Subcontrata_que_solo_existe_en_Subcontratas_legacy_ahora_falla()
    {
        // El reverso: si alguien (incorrectamente) insertara directamente en
        // la tabla legacy Subcontratas sin pasar por
        // Empresa.CrearComoSubcontrata, un Trabajador que la referencie debe
        // FALLAR tras el repunteo — confirma que la FK cambió de verdad de
        // tabla y no es un No-Op que "coincidentemente" sigue aceptando filas.
        var tenantId = Guid.NewGuid();
        await AplicarMigracionF3aAsync(tenantId);
        await AplicarMigracionF3bAsync(tenantId);

        await using var contexto = CrearContexto(tenantId);
        var subcontrataSoloLegacy = new Subcontrata("Solo en legacy S.L.", "B12345674");
        contexto.Subcontratas.Add(subcontrataSoloLegacy);
        await contexto.SaveChangesAsync();

        contexto.Trabajadores.Add(Trabajador.DeSubcontrata(subcontrataSoloLegacy.Id, "Imposible", "De Verdad", "87654321X"));
        var accion = async () => await contexto.SaveChangesAsync();

        await accion.Should().ThrowAsync<DbUpdateException>(
            "subcontrataSoloLegacy.Id no existe en Empresas — la FK repuntada debe rechazarlo aunque exista en Subcontratas");
    }

    [Fact]
    public async Task Una_Subcontrata_ya_backfillada_por_F3a_sigue_pudiendo_recibir_un_Trabajador_tras_el_repunteo()
    {
        // Regresión: una Subcontrata que YA existía antes de la congelación
        // (y que F3a copió a Empresas) no debe dejar de funcionar tras el
        // repunteo. Crítico: se siembra ANTES de aplicar F3a — el backfill
        // de F3a es un INSERT único que solo copia lo que exista en el
        // momento de su propio Up().
        var tenantId = Guid.NewGuid();
        Guid subcontrataId;

        await using (var contextoPrevio = CrearContexto(tenantId))
        {
            var subcontrata = new Subcontrata("Subcontrata Preexistente S.L.", "B10380186");
            contextoPrevio.Subcontratas.Add(subcontrata);
            await contextoPrevio.SaveChangesAsync();
            subcontrataId = subcontrata.Id;
        }

        await AplicarMigracionF3aAsync(tenantId);
        await AplicarMigracionF3bAsync(tenantId);

        await using var contexto = CrearContexto(tenantId);
        contexto.Trabajadores.Add(Trabajador.DeSubcontrata(subcontrataId, "Trabajador", "Preexistente", "11223344B"));
        var accion = async () => await contexto.SaveChangesAsync();

        await accion.Should().NotThrowAsync("F3a ya copió esta Subcontrata a Empresas antes del repunteo");
    }

    private async Task<string?> ObtenerTablaReferenciadaAsync(string nombreConstraint)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT confrelid::regclass::text
            FROM pg_constraint
            WHERE conname = @nombre;
            """;
        comando.Parameters.AddWithValue("nombre", nombreConstraint);
        return (string?)await comando.ExecuteScalarAsync();
    }

    private async Task AplicarMigracionF3aAsync(Guid tenantId)
    {
        await using var contexto = CrearContexto(tenantId);
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionF3a);
    }

    private async Task AplicarMigracionF3bAsync(Guid tenantId)
    {
        await using var contexto = CrearContexto(tenantId);
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionF3b);
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
