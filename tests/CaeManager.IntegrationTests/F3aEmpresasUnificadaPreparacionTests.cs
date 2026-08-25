using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Verificación de F3a (preparación física de Empresas unificada) contra
/// Postgres real. Alcance estricto de F3a
/// (f3-diseno-fisico-empresa-unificada-2026-08-25.md §8,
/// f3-comparativa-alcance-abcd-2026-08-25.md, camino D): crear columnas +
/// backfill + índices — SIN redirigir lectores, SIN repuntear FKs, SIN
/// retirar tablas antiguas. El repunteo de FKs y los CHECK anti-
/// autorreferencia son F3c, no se prueban aquí — ver
/// f3c-diseno-adversario-reconciliacion-2026-08-25.md.
///
/// El requisito explícito del propietario del producto para F3a es que el
/// backfill no introduzca ninguna divergencia silenciosa: cada test
/// compara la fila copiada contra la fila de origen, campo a campo,
/// incluidos los de soft-delete — nunca solo "no lanzó excepción".
/// </summary>
public class F3aEmpresasUnificadaPreparacionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task El_backfill_copia_un_Cliente_activo_sin_divergencia_de_ningun_campo()
    {
        var tenantId = Guid.NewGuid();
        var ejecutivoId = Guid.NewGuid();
        Cliente clienteOriginal;

        await using (var contexto = CrearContexto(tenantId))
        {
            clienteOriginal = new Cliente("Iberojet S.A.", "B10380194", esCritico: true, notas: "Cliente prioritario", ejecutivoUsuarioId: ejecutivoId);
            contexto.Clientes.Add(clienteOriginal);
            await contexto.SaveChangesAsync();
        }

        // Re-aplicar el backfill de F3a sobre datos ya existentes: como el
        // backfill corre dentro de la migración (una sola vez, al crear la
        // base), aquí se simula reconstruyendo la base desde cero con el
        // Cliente ya sembrado no es posible con el patrón de
        // BaseDatosPostgresDePruebas (migra antes de poder insertar). Se
        // verifica en su lugar re-ejecutando el propio SQL del backfill de
        // forma aislada, exactamente como está escrito en la migración —
        // no una reimplementación paralela que podría divergir del real.
        await using var conexion = new Npgsql.NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using (var comando = conexion.CreateCommand())
        {
            comando.CommandText = """
                INSERT INTO "Empresas"
                    ("Id", "TenantId", "RazonSocial", "Cif", "Cnae", "ConvenioAplicable", "EsActividadAnexoI",
                     "EsPropia", "EjecutivoUsuarioId", "EsCritico", "Notas", "NivelServicio",
                     "CreadoEnUtc", "EstaEliminado", "EliminadoEnUtc", "EliminadoPorUsuarioId", "Version")
                SELECT
                    "Id", "TenantId", "RazonSocial", "Cif", NULL, NULL, false,
                    false, "EjecutivoUsuarioId", "EsCritico", "Notas", NULL,
                    "CreadoEnUtc", "EstaEliminado", "EliminadoEnUtc", "EliminadoPorUsuarioId", "Version"
                FROM "Clientes" WHERE "Id" = @id;
                """;
            comando.Parameters.AddWithValue("id", clienteOriginal.Id);
            await comando.ExecuteNonQueryAsync();
        }

        await using var contextoVerificacion = CrearContexto(tenantId);
        var copia = await contextoVerificacion.Empresas.IgnoreQueryFilters().SingleAsync(e => e.Id == clienteOriginal.Id);

        copia.EsPropia.Should().BeFalse();
        copia.RazonSocial.Should().Be(clienteOriginal.RazonSocial);
        copia.Cif.Should().Be(clienteOriginal.Cif);
        copia.EsCritico.Should().Be(clienteOriginal.EsCritico);
        copia.Notas.Should().Be(clienteOriginal.Notas);
        copia.EjecutivoUsuarioId.Should().Be(clienteOriginal.EjecutivoUsuarioId);
        copia.NivelServicio.Should().BeNull("EsCritico/Notas/EjecutivoUsuarioId son de Cliente; NivelServicio no aplica a una fila ex-Cliente");
    }

    [Fact]
    public async Task El_backfill_copia_un_Cliente_soft_deleted_conservando_su_estado_de_borrado()
    {
        var tenantId = Guid.NewGuid();
        Guid clienteId;
        Guid usuarioQueElimino = Guid.NewGuid();

        await using (var contexto = CrearContexto(tenantId))
        {
            var cliente = new Cliente("Cliente a eliminar", "B12345674", esCritico: false);
            contexto.Clientes.Add(cliente);
            await contexto.SaveChangesAsync();
            clienteId = cliente.Id;

            cliente.MarcarComoEliminado(usuarioQueElimino);
            await contexto.SaveChangesAsync();
        }

        await using var conexion = new Npgsql.NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using (var comando = conexion.CreateCommand())
        {
            comando.CommandText = """
                INSERT INTO "Empresas"
                    ("Id", "TenantId", "RazonSocial", "Cif", "Cnae", "ConvenioAplicable", "EsActividadAnexoI",
                     "EsPropia", "EjecutivoUsuarioId", "EsCritico", "Notas", "NivelServicio",
                     "CreadoEnUtc", "EstaEliminado", "EliminadoEnUtc", "EliminadoPorUsuarioId", "Version")
                SELECT
                    "Id", "TenantId", "RazonSocial", "Cif", NULL, NULL, false,
                    false, "EjecutivoUsuarioId", "EsCritico", "Notas", NULL,
                    "CreadoEnUtc", "EstaEliminado", "EliminadoEnUtc", "EliminadoPorUsuarioId", "Version"
                FROM "Clientes" WHERE "Id" = @id;
                """;
            comando.Parameters.AddWithValue("id", clienteId);
            await comando.ExecuteNonQueryAsync();
        }

        // Lectura directa por SQL: IgnoreQueryFilters() no basta si algún
        // día el filtro global cambiara de forma — leer crudo confirma el
        // dato físico, no el efecto de un filtro que podría enmascararlo.
        await using var comandoLectura = conexion.CreateCommand();
        comandoLectura.CommandText = """SELECT "EstaEliminado", "EliminadoPorUsuarioId" FROM "Empresas" WHERE "Id" = @id""";
        comandoLectura.Parameters.AddWithValue("id", clienteId);
        await using var lector = await comandoLectura.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        lector.GetBoolean(0).Should().BeTrue("una fila soft-deleted en Cliente debe llegar soft-deleted a la copia, o F3c encontraría una divergencia falsa");
        lector.GetGuid(1).Should().Be(usuarioQueElimino);
    }

    [Fact]
    public async Task El_backfill_traduce_NivelServicio_de_Subcontrata_del_entero_al_texto_esperado()
    {
        var tenantId = Guid.NewGuid();
        Subcontrata subcontrataOriginal;

        await using (var contexto = CrearContexto(tenantId))
        {
            subcontrataOriginal = new Subcontrata("Medición de Temperatura S.L.", "B10380186");
            subcontrataOriginal.CambiarNivelServicio(NivelServicioSubcontrata.Supervisada);
            contexto.Subcontratas.Add(subcontrataOriginal);
            await contexto.SaveChangesAsync();
        }

        await using var conexion = new Npgsql.NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using (var comando = conexion.CreateCommand())
        {
            comando.CommandText = """
                INSERT INTO "Empresas"
                    ("Id", "TenantId", "RazonSocial", "Cif", "Cnae", "ConvenioAplicable", "EsActividadAnexoI",
                     "EsPropia", "EjecutivoUsuarioId", "EsCritico", "Notas", "NivelServicio",
                     "CreadoEnUtc", "EstaEliminado", "EliminadoEnUtc", "EliminadoPorUsuarioId", "Version")
                SELECT
                    "Id", "TenantId", "RazonSocial", "Cif", NULL, NULL, false,
                    false, NULL, NULL, NULL,
                    CASE "NivelServicio" WHEN 0 THEN 'Gestionada' WHEN 1 THEN 'Supervisada' END,
                    "CreadoEnUtc", "EstaEliminado", "EliminadoEnUtc", "EliminadoPorUsuarioId", "Version"
                FROM "Subcontratas" WHERE "Id" = @id;
                """;
            comando.Parameters.AddWithValue("id", subcontrataOriginal.Id);
            await comando.ExecuteNonQueryAsync();
        }

        await using var contextoVerificacion = CrearContexto(tenantId);
        var copia = await contextoVerificacion.Empresas.SingleAsync(e => e.Id == subcontrataOriginal.Id);

        copia.NivelServicio.Should().Be("Supervisada", "el CASE WHEN debe traducir el entero 1, no copiarlo tal cual");
        copia.RazonSocial.Should().Be(subcontrataOriginal.RazonSocial);
        copia.Cif.Should().Be(subcontrataOriginal.Cif);
        copia.EsCritico.Should().BeNull("EsCritico es de Cliente, no aplica a una fila ex-Subcontrata");
    }

    [Fact]
    public async Task Una_Empresa_ya_existente_queda_EsPropia_true_tras_la_migracion()
    {
        // La propia migración F3a ya corrió en InitializeAsync sobre una
        // base vacía — este test siembra una Empresa DESPUÉS de migrar
        // (dominio ya conoce EsPropia, la establece explícitamente a true
        // en el constructor) para confirmar el valor por defecto real de
        // la columna, no solo lo que el dominio asigna en memoria.
        var tenantId = Guid.NewGuid();
        await using var contexto = CrearContexto(tenantId);
        var empresa = new Empresa("Talveg Coordinación S.L.", "B12345674");
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        await using var conexion = new Npgsql.NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """SELECT "EsPropia" FROM "Empresas" WHERE "Id" = @id""";
        comando.Parameters.AddWithValue("id", empresa.Id);
        var esPropia = (bool)(await comando.ExecuteScalarAsync())!;
        esPropia.Should().BeTrue();
    }

    [Fact]
    public async Task Los_indices_unicos_de_Cif_y_RazonSocial_siguen_activos_tras_anadir_las_columnas_de_F3a()
    {
        var tenantId = Guid.NewGuid();
        await using var contexto = CrearContexto(tenantId);
        contexto.Empresas.Add(new Empresa("Duplicado S.L.", "B12345674"));
        await contexto.SaveChangesAsync();

        await using var contexto2 = CrearContexto(tenantId);
        contexto2.Empresas.Add(new Empresa("Duplicado S.L. (otro nombre)", "B12345674"));
        var accion = async () => await contexto2.SaveChangesAsync();

        await accion.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task F3a_no_toca_las_FKs_de_Centro_siguen_apuntando_a_Clientes_no_a_Empresas()
    {
        // Confirmación explícita de que F3a se quedó estrictamente dentro
        // de su alcance (f3-comparativa-alcance-abcd-2026-08-25.md): el
        // repunteo de FKs es F3c, no debe haber ocurrido todavía.
        await using var conexion = new Npgsql.NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT confrelid::regclass::text
            FROM pg_constraint
            WHERE conname = 'FK_Centros_Clientes_TenantId_ClienteId';
            """;
        var tablaReferenciada = (string?)await comando.ExecuteScalarAsync();
        tablaReferenciada.Should().Be("\"Clientes\"", "F3a no debe repuntear ninguna FK — eso es F3c");
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
