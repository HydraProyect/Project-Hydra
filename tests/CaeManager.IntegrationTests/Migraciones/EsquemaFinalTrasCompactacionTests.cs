using FluentAssertions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// Propiedades del esquema final que, hasta la compactación de migraciones
/// (P1-M3), solo afirmaban los tests de migraciones intermedias —el repunteo de
/// FKs a Empresas (F3b), la retirada de tablas legacy y puente (F3c, F4), el
/// índice parcial de webhooks pendientes y el particionado de auditoría—. Esos
/// tests medían la transformación de datos de migraciones que ya no existen y
/// se retiraron con ellas; lo que afirmaban del resultado se conserva aquí,
/// contra una base recién migrada con la línea base.
/// </summary>
public class EsquemaFinalTrasCompactacionTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync() => await BaseDatosPostgresDePruebas.MigrarAsync(_cadena);

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Theory]
    [InlineData("FK_Centros_Empresas_TenantId_ClienteId", "Centros")]
    [InlineData("FK_Documentos_Empresas_TenantId_ClienteId", "Documentos")]
    [InlineData("FK_Proyectos_Empresas_TenantId_ClienteId", "Proyectos")]
    [InlineData("FK_TarifasCliente_Empresas_TenantId_ClienteId", "TarifasCliente")]
    [InlineData("FK_ContactosAgenda_Empresas_ClienteId", "ContactosAgenda")]
    [InlineData("FK_Trabajadores_Empresas_TenantId_SubcontrataId", "Trabajadores")]
    [InlineData("FK_Vehiculos_Empresas_TenantId_SubcontrataId", "Vehiculos")]
    [InlineData("FK_VerificacionesExternaSubcontrata_Empresas_TenantId_Subcontr~", "VerificacionesExternaSubcontrata")]
    [InlineData("FK_ContactosAgenda_Empresas_SubcontrataId", "ContactosAgenda")]
    [InlineData("FK_AsignacionesOperacion_Empresas_PropietarioTenantId_AmbitoRe~", "AsignacionesOperacion")]
    [InlineData("FK_AsignacionesCartera_Empresas_PropietarioTenantId_AmbitoRela~", "AsignacionesCartera")]
    public async Task Las_FKs_de_cliente_y_subcontrata_apuntan_a_Empresas(string restriccion, string tabla)
    {
        var referenciada = await EscalarAsync<string?>(
            """
            SELECT c.confrelid::regclass::text FROM pg_constraint c
             WHERE c.conname = @nombre AND c.conrelid = format('public.%I', @tabla)::regclass AND c.contype = 'f'
            """,
            ("nombre", restriccion), ("tabla", tabla));

        referenciada.Should().Be("\"Empresas\"");
    }

    [Theory]
    [InlineData("Clientes")]
    [InlineData("Subcontratas")]
    [InlineData("EmpresasClientes")]
    [InlineData("SubcontratasClientes")]
    [InlineData("SubcontratasEmpresas")]
    public async Task Las_tablas_legacy_y_puente_no_existen(string tabla)
    {
        (await EscalarAsync<bool>("SELECT to_regclass(format('public.%I', @tabla)) IS NULL", ("tabla", tabla)))
            .Should().BeTrue();
    }

    [Fact]
    public async Task El_indice_de_webhooks_pendientes_filtra_por_Estado_y_no_por_Procesado()
    {
        var definicion = await EscalarAsync<string?>(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'IX_EventosWebhook_TenantId_FechaRecepcionUtc_Pendientes'");

        definicion.Should().NotBeNull().And.Contain("WHERE").And.Contain("\"Estado\"").And.NotContain("Procesado");
    }

    [Theory]
    [InlineData("RegistrosAuditoria", "FechaUtc")]
    [InlineData("RegistrosAccesoDocumentoSensible", "OcurridoEnUtc")]
    public async Task Los_registros_de_auditoria_estan_particionados_con_la_fecha_en_la_PK(string tabla, string columna)
    {
        (await EscalarAsync<string>("SELECT relkind::text FROM pg_class WHERE oid = format('public.%I', @tabla)::regclass",
            ("tabla", tabla))).Should().Be("p");

        (await EscalarAsync<string>(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = format('public.%I', @tabla)::regclass AND contype = 'p'",
            ("tabla", tabla))).Should().Be($"PRIMARY KEY (\"Id\", \"{columna}\")");
    }

    [Theory]
    [InlineData("RegistrosAuditoria")]
    [InlineData("RegistrosAccesoDocumentoSensible")]
    public async Task Cada_particion_tiene_los_mismos_indices_que_su_madre(string tabla)
    {
        // Lo afirmaba MigracionParticionarAuditoriaPorMesTests: los índices de
        // la madre (PK incluida) se propagan a cada partición mensual y a la
        // de por defecto.
        var descuadres = await EscalarAsync<string?>(
            """
            WITH madre AS (SELECT format('public.%I', @tabla)::regclass AS oid),
                 esperados AS (SELECT count(*) AS n FROM pg_index WHERE indrelid = (SELECT oid FROM madre))
            SELECT string_agg(format('%s=%s', c.relname, (SELECT count(*) FROM pg_index i WHERE i.indrelid = c.oid)), ', ')
              FROM pg_inherits h JOIN pg_class c ON c.oid = h.inhrelid
             WHERE h.inhparent = (SELECT oid FROM madre)
               AND (SELECT count(*) FROM pg_index i WHERE i.indrelid = c.oid) <> (SELECT n FROM esperados)
            """,
            ("tabla", tabla));
        var particiones = await EscalarAsync<long>(
            "SELECT count(*) FROM pg_inherits WHERE inhparent = format('public.%I', @tabla)::regclass", ("tabla", tabla));
        var indicesMadre = await EscalarAsync<long>(
            "SELECT count(*) FROM pg_index WHERE indrelid = format('public.%I', @tabla)::regclass", ("tabla", tabla));

        particiones.Should().BeGreaterThanOrEqualTo(2, "control positivo: al menos el mes en curso y la partición por defecto");
        indicesMadre.Should().BeGreaterThan(1, "control positivo: la PK y algún índice secundario");
        descuadres.Should().BeNull("toda partición lleva los índices de la madre");
    }

    [Fact]
    public async Task Las_politicas_de_AspNetUsers_leen_los_registros_vivos_y_no_tablas_de_paso()
    {
        var expresiones = await EscalarAsync<string>(
            "SELECT string_agg(coalesce(pg_get_expr(polqual, polrelid), '') || ' ' || coalesce(pg_get_expr(polwithcheck, polrelid), ''), ' ') " +
            "FROM pg_policy WHERE polrelid = 'public.\"AspNetUsers\"'::regclass");

        // Tras un RENAME una política sigue el OID de la tabla apartada: tiene
        // que nombrar la tabla viva, nunca la apartada por el particionado.
        foreach (var (tabla, _) in CaeManager.Migrations.PostgreSQL.ParticionadoMensualEventos.Tablas)
            expresiones.Should().Contain($"\"{tabla}\"", $"control positivo: las políticas de AspNetUsers leen {tabla}");
        expresiones.Should().NotContain("_previa").And.NotContain("_particionada");
    }

    private async Task<T> EscalarAsync<T>(string sql, params (string Nombre, object Valor)[] parametros)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        foreach (var (nombre, valor) in parametros)
            comando.Parameters.AddWithValue(nombre, valor);
        var resultado = await comando.ExecuteScalarAsync();
        return resultado is DBNull or null ? default! : (T)resultado;
    }
}
