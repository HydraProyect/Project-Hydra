using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Empresas;
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
/// F3b-Cliente, mitad de repunteo de FKs
/// (f3b-inventario-fks-dependientes-2026-08-25.md,
/// f3b-decision-d2-transicion-acotada-2026-08-25.md §0-2).
///
/// <para>
/// Existe por un hallazgo real, no por precaución: redirigir los 9
/// escritores de Cliente a <c>Empresa</c> sin repuntar estas FKs en el
/// mismo despliegue rompía la creación de Centro/Documento/etc. para
/// cualquier Cliente nuevo con <c>23503</c> reales contra Postgres — un
/// Cliente creado después del redirect solo existe en <c>Empresas</c>, y
/// las FKs todavía exigían que existiera en <c>Clientes</c>. El test
/// central de este fichero (<see cref="Un_Centro_para_un_Cliente_creado_solo_en_Empresas_ya_funciona_tras_el_repunteo"/>)
/// reproduce exactamente ese escenario y demuestra que ya no falla.
/// </para>
/// </summary>
public class F3bClienteRepunteoFksTests : IAsyncLifetime
{
    private const string MigracionAnteriorAF3a = "EstadoBootstrapPlataforma";
    private const string MigracionF3a = "F3aEmpresasUnificadaPreparacion";
    private const string MigracionF3b = "F3bClienteRepunteoFks";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    // Se detiene JUSTO ANTES de F3a — a diferencia de F3aEmpresasUnificadaPreparacionTests,
    // aquí algunos tests necesitan sembrar un Cliente ANTES de que el
    // backfill de F3a se ejecute, para demostrar que sigue siendo válido
    // después del repunteo (F3a solo copia lo que exista en el momento de
    // su propio Up(), nunca lo que se inserte después).
    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionAnteriorAF3a);
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData("FK_Centros_Empresas_TenantId_ClienteId", "Centros")]
    [InlineData("FK_Documentos_Empresas_TenantId_ClienteId", "Documentos")]
    [InlineData("FK_EmpresasClientes_Empresas_TenantId_ClienteId", "EmpresasClientes")]
    [InlineData("FK_Proyectos_Empresas_TenantId_ClienteId", "Proyectos")]
    [InlineData("FK_SubcontratasClientes_Empresas_TenantId_ClienteId", "SubcontratasClientes")]
    [InlineData("FK_TarifasCliente_Empresas_TenantId_ClienteId", "TarifasCliente")]
    [InlineData("FK_ContactosAgenda_Empresas_ClienteId", "ContactosAgenda")]
    public async Task Las_9_FKs_del_inventario_apuntan_a_Empresas_tras_el_repunteo(string nombreConstraint, string tabla)
    {
        _ = tabla;
        var tenantId = Guid.NewGuid();
        await AplicarMigracionF3aAsync(tenantId);
        await AplicarMigracionF3bAsync(tenantId);

        var tablaReferenciada = await ObtenerTablaReferenciadaAsync(nombreConstraint);
        tablaReferenciada.Should().Be("\"Empresas\"",
            $"tras F3b-Cliente, {nombreConstraint} debe repuntar contra Empresas, no contra Clientes");
    }

    [Fact]
    public async Task Las_2_FKs_de_ambito_de_Asignaciones_apuntan_a_Empresas_tras_el_repunteo()
    {
        var tenantId = Guid.NewGuid();
        await AplicarMigracionF3aAsync(tenantId);
        await AplicarMigracionF3bAsync(tenantId);

        (await ObtenerTablaReferenciadaAsync("FK_AsignacionesOperacion_Empresas_PropietarioTenantId_AmbitoRe~"))
            .Should().Be("\"Empresas\"");
        (await ObtenerTablaReferenciadaAsync("FK_AsignacionesCartera_Empresas_PropietarioTenantId_AmbitoRela~"))
            .Should().Be("\"Empresas\"");
    }

    [Fact]
    public async Task Un_Centro_para_un_Cliente_creado_solo_en_Empresas_ya_funciona_tras_el_repunteo()
    {
        // Reproduce el hallazgo real: un Cliente creado DESPUÉS de que F3b
        // redirigiera los escritores solo existe en Empresas — nunca se
        // escribe en Clientes. Antes del repunteo, esto fallaba con 23503
        // (ver f3b-inventario-fks-dependientes-2026-08-25.md).
        var tenantId = Guid.NewGuid();
        await AplicarMigracionF3aAsync(tenantId);
        await AplicarMigracionF3bAsync(tenantId);

        await using var contexto = CrearContexto(tenantId);
        var clienteNuevo = Empresa.CrearComoCliente("Cliente Post-Freeze S.L.", "B10380194", false, null, null);
        contexto.Empresas.Add(clienteNuevo);
        await contexto.SaveChangesAsync();

        var empresaTitular = new Empresa("Empresa Titular S.L.", "B87654323");
        contexto.Empresas.Add(empresaTitular);
        await contexto.SaveChangesAsync();

        var centroReal = new Centro(clienteNuevo.Id, empresaTitular.Id, "Centro de prueba");
        contexto.Centros.Add(centroReal);
        var accion = async () => await contexto.SaveChangesAsync();

        await accion.Should().NotThrowAsync(
            "tras el repunteo, ClienteId del Centro debe validarse contra Empresas, donde el cliente nuevo sí existe");
    }

    [Fact]
    public async Task Un_Centro_para_un_Cliente_que_solo_existe_en_Clientes_legacy_ahora_falla()
    {
        // El reverso de la prueba anterior: si alguien (incorrectamente)
        // insertara directamente en la tabla legacy Clientes sin pasar por
        // Empresa.CrearComoCliente, un Centro que lo referencie debe FALLAR
        // tras el repunteo — confirma que la FK cambió de verdad de tabla y
        // no es un No-Op que "coincidentemente" sigue aceptando filas.
        var tenantId = Guid.NewGuid();
        await AplicarMigracionF3aAsync(tenantId);
        await AplicarMigracionF3bAsync(tenantId);

        await using var contexto = CrearContexto(tenantId);
        var clienteSoloLegacy = new Cliente("Solo en legacy S.L.", "B12345674", esCritico: false);
        contexto.Clientes.Add(clienteSoloLegacy);
        await contexto.SaveChangesAsync();

        var empresaTitular = new Empresa("Empresa Titular S.L.", "B87654323");
        contexto.Empresas.Add(empresaTitular);
        await contexto.SaveChangesAsync();

        contexto.Centros.Add(new Centro(clienteSoloLegacy.Id, empresaTitular.Id, "Centro imposible"));
        var accion = async () => await contexto.SaveChangesAsync();

        await accion.Should().ThrowAsync<DbUpdateException>(
            "clienteSoloLegacy.Id no existe en Empresas — la FK repuntada debe rechazarlo aunque exista en Clientes");
    }

    [Fact]
    public async Task Un_Cliente_ya_backfillado_por_F3a_sigue_pudiendo_recibir_un_Centro_tras_el_repunteo()
    {
        // Regresión: un Cliente que YA existía antes de la congelación (y
        // que F3a copió a Empresas) no debe dejar de funcionar tras el
        // repunteo — sigue existiendo en ambas tablas, y el Centro nuevo
        // debe validarse contra la copia en Empresas sin problema.
        //
        // Crítico: el Cliente se siembra ANTES de aplicar F3a (no después,
        // y no usando el helper de los demás tests) — el backfill de F3a es
        // un INSERT único que solo copia lo que exista en el momento de su
        // propio Up(); un Cliente insertado después de F3a nunca llegaría a
        // Empresas, y este test estaría demostrando otra cosa.
        var tenantId = Guid.NewGuid();
        Guid clienteId;

        await using (var contextoPrevio = CrearContexto(tenantId))
        {
            var cliente = new Cliente("Cliente Preexistente S.L.", "B10380186", esCritico: false);
            contextoPrevio.Clientes.Add(cliente);
            await contextoPrevio.SaveChangesAsync();
            clienteId = cliente.Id;
        }

        await AplicarMigracionF3aAsync(tenantId);
        await AplicarMigracionF3bAsync(tenantId);

        await using var contexto = CrearContexto(tenantId);
        var empresaTitular = new Empresa("Empresa Titular S.L.", "B87654323");
        contexto.Empresas.Add(empresaTitular);
        await contexto.SaveChangesAsync();

        contexto.Centros.Add(new Centro(clienteId, empresaTitular.Id, "Centro de cliente preexistente"));
        var accion = async () => await contexto.SaveChangesAsync();

        await accion.Should().NotThrowAsync("F3a ya copió este Cliente a Empresas antes del repunteo");
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
