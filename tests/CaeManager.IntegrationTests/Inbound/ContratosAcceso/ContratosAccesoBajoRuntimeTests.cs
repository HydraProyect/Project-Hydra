using CaeManager.Application.Common;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerContratosAcceso;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Inbound.ContratosAcceso;

/// <summary>
/// Contrato de acceso (Inbound M1) bajo <c>cae_app_runtime</c> con contexto firmado: el aislamiento por Tenant lo da RLS
/// y no solo el filtro de EF, el runtime no puede borrar filas (solo soft delete), la FK compuesta contra el Centro
/// rechaza un ClienteEmpresarialId que no sea el del Centro, y el listado traduce a SQL el criterio de vigencia.
/// </summary>
public class ContratosAccesoBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _propietario = new("Tenant propietario del Cliente empresarial");
    private readonly Tenant _ajeno = new("Tenant propietario ajeno");
    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Guid _administradorAjeno = Guid.NewGuid();
    private Guid _cliente;
    private Guid _otroCliente;
    private Guid _centro;
    private Guid _contratista;
    private Guid _contratoVigente;
    private Guid _contratoCaducado;
    private Guid _contratoAjeno;
    private Guid _contratistaAjena;

    public async Task InitializeAsync()
    {
        var hoy = DiaDeNegocio.Hoy();
        await using (var contexto = Contexto(_propietario.Id))
        {
            await contexto.Database.MigrateAsync();
            contexto.Tenants.AddRange(_propietario, _ajeno);
            var roles = await contexto.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id);
            Cuenta(contexto, _administrador, _propietario.Id, roles[Roles.Administrador]);
            Cuenta(contexto, _administradorAjeno, _ajeno.Id, roles[Roles.Administrador]);
            var cliente = Empresa.CrearComoCliente("Cliente empresarial titular", "B10380186", false, null, null);
            var otroCliente = Empresa.CrearComoCliente("Otro Cliente empresarial", "B10380202", false, null, null);
            var contratista = new Empresa("Empresa contratista", "B10380194");
            contexto.Empresas.AddRange(cliente, otroCliente, contratista);
            var centro = new Centro(cliente.Id, cliente.Id, "Centro de trabajo del titular");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();
            var vigente = ContratoAcceso.Crear(cliente.Id, centro.Id, contratista.Id, hoy.AddDays(-10), hoy.AddDays(10), null);
            var caducado = ContratoAcceso.Crear(cliente.Id, centro.Id, contratista.Id, hoy.AddDays(-60), hoy.AddDays(-1), null);
            contexto.ContratosAcceso.AddRange(vigente, caducado);
            await contexto.SaveChangesAsync();
            _cliente = cliente.Id; _otroCliente = otroCliente.Id; _centro = centro.Id; _contratista = contratista.Id;
            _contratoVigente = vigente.Id; _contratoCaducado = caducado.Id;
        }
        await using (var contexto = Contexto(_ajeno.Id))
        {
            var cliente = Empresa.CrearComoCliente("Cliente empresarial ajeno", "B10380186", false, null, null);
            var contratista = new Empresa("Empresa contratista ajena", "B10380194");
            contexto.Empresas.AddRange(cliente, contratista);
            var centro = new Centro(cliente.Id, cliente.Id, "Centro de trabajo ajeno");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();
            var ajeno = ContratoAcceso.Crear(cliente.Id, centro.Id, contratista.Id, hoy.AddDays(-10), null, null);
            contexto.ContratosAcceso.Add(ajeno);
            await contexto.SaveChangesAsync();
            _contratoAjeno = ajeno.Id;
            _contratistaAjena = contratista.Id;
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task El_contrato_de_otro_Tenant_es_invisible_bajo_runtime_aunque_se_quite_el_filtro_de_EF()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);
        (await contexto.Database.SqlQueryRaw<string>("SELECT current_user AS \"Value\"").SingleAsync()).Should().Be("cae_app_runtime");

        (await contexto.ContratosAcceso.IgnoreQueryFilters().AnyAsync(c => c.Id == _contratoVigente))
            .Should().BeTrue("control positivo: el contrato del propio Tenant se ve");
        (await contexto.ContratosAcceso.IgnoreQueryFilters().AnyAsync(c => c.Id == _contratoAjeno))
            .Should().BeFalse("RLS oculta la fila del Tenant ajeno sin ayuda del filtro de EF");

        await using var contextoAjeno = Contexto(_ajeno.Id, _administradorAjeno);
        (await contextoAjeno.ContratosAcceso.IgnoreQueryFilters().Select(c => c.Id).ToListAsync())
            .Should().Equal([_contratoAjeno], "y al revés: desde el Tenant ajeno solo se ve el suyo");
    }

    [Fact]
    public async Task El_runtime_no_puede_borrar_contratos_solo_darlos_de_baja()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);

        var borrado = () => contexto.Database.ExecuteSqlRawAsync("DELETE FROM \"ContratosAcceso\";");
        (await borrado.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "la migración no concede DELETE a cae_app_runtime");

        // Control positivo: la baja lógica (UPDATE) sí pasa.
        var contrato = await contexto.ContratosAcceso.SingleAsync(c => c.Id == _contratoCaducado);
        contrato.MarcarComoEliminado(_administrador);
        await contexto.SaveChangesAsync();
        (await contexto.ContratosAcceso.IgnoreQueryFilters().SingleAsync(c => c.Id == _contratoCaducado)).EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task La_base_rechaza_un_contrato_cuyo_Cliente_empresarial_no_es_el_del_Centro()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);
        var hoy = DiaDeNegocio.Hoy();

        // Control positivo: con el Cliente empresarial del Centro, el mismo alta pasa.
        contexto.ContratosAcceso.Add(ContratoAcceso.Crear(_cliente, _centro, _contratista, hoy, null, null));
        await contexto.SaveChangesAsync();

        contexto.ContratosAcceso.Add(ContratoAcceso.Crear(_otroCliente, _centro, _contratista, hoy, null, null));
        var guardar = () => contexto.SaveChangesAsync();
        (await guardar.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation,
                "la FK compuesta (TenantId, CentroId, ClienteEmpresarialId) contra Centros lo hace irrepresentable");
    }

    [Fact]
    public async Task Un_INSERT_por_SQL_con_el_TenantId_de_otro_Tenant_lo_rechaza_el_WITH_CHECK()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);
        var hoy = DiaDeNegocio.Hoy();
        Task<int> Insertar(Guid tenantId) => contexto.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ContratosAcceso" ("Id", "TenantId", "ClienteEmpresarialId", "CentroId", "EmpresaContratistaId",
                "FechaInicio", "Version", "CreadoEnUtc", "EstaEliminado")
            VALUES ({Guid.NewGuid()}, {tenantId}, {_cliente}, {_centro}, {_contratista}, {hoy}, {Guid.NewGuid()}, now(), false);
            """);

        var ajeno = () => Insertar(_ajeno.Id);
        (await ajeno.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "la política aislamiento_tenant no deja escribir una fila con otro TenantId (new row violates row-level security)");

        // Control positivo: la misma fila con el TenantId del contexto entra.
        (await Insertar(_propietario.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Soporte_lee_los_contratos_de_su_Tenant_y_no_puede_insertar()
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        var contextoFirmado = await ContextoRlsDePruebas.FirmarAsync(conexion, tenantId: _propietario.Id);
        await using (var preparar = conexion.CreateCommand())
        {
            preparar.CommandText = "SELECT set_config('app.contexto', @contexto, true); SET LOCAL ROLE cae_app_soporte;";
            preparar.Parameters.AddWithValue("contexto", contextoFirmado);
            await preparar.ExecuteNonQueryAsync();
        }

        await using (var leer = conexion.CreateCommand())
        {
            leer.CommandText = "SELECT count(*) FROM \"ContratosAcceso\";";
            ((long)(await leer.ExecuteScalarAsync())!).Should().Be(2, "Soporte lee los dos contratos del Tenant propietario y no el ajeno");
        }

        await using var escribir = conexion.CreateCommand();
        escribir.CommandText = """
            INSERT INTO "ContratosAcceso" ("Id", "TenantId", "ClienteEmpresarialId", "CentroId", "EmpresaContratistaId",
                "FechaInicio", "Version", "CreadoEnUtc", "EstaEliminado")
            VALUES (@id, @tenant, @cliente, @centro, @contratista, @inicio, @version, now(), false);
            """;
        escribir.Parameters.AddWithValue("id", Guid.NewGuid());
        escribir.Parameters.AddWithValue("tenant", _propietario.Id);
        escribir.Parameters.AddWithValue("cliente", _cliente);
        escribir.Parameters.AddWithValue("centro", _centro);
        escribir.Parameters.AddWithValue("contratista", _contratista);
        escribir.Parameters.AddWithValue("inicio", DiaDeNegocio.Hoy());
        escribir.Parameters.AddWithValue("version", Guid.NewGuid());
        (await FluentActions.Awaiting(() => escribir.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "la migración solo concede SELECT a cae_app_soporte");
    }

    [Fact]
    public async Task La_base_rechaza_una_Empresa_contratista_de_otro_Tenant()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);
        var hoy = DiaDeNegocio.Hoy();

        // Control positivo: con la Empresa contratista del propio Tenant, el mismo alta pasa.
        contexto.ContratosAcceso.Add(ContratoAcceso.Crear(_cliente, _centro, _contratista, hoy, null, null));
        await contexto.SaveChangesAsync();

        contexto.ContratosAcceso.Add(ContratoAcceso.Crear(_cliente, _centro, _contratistaAjena, hoy, null, null));
        var guardar = () => contexto.SaveChangesAsync();
        (await guardar.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation,
                "la FK (TenantId, EmpresaContratistaId) contra Empresas no casa con una Empresa de otro Tenant");
    }

    [Theory]
    [InlineData("CK_ContratosAcceso_FinNoAnteriorAlInicio")]
    [InlineData("CK_ContratosAcceso_CierreNoAnteriorAlInicio")]
    [InlineData("CK_ContratosAcceso_ContratistaDistintaDelCliente")]
    public async Task Un_UPDATE_por_SQL_que_rompe_una_invariante_lo_rechaza_su_CHECK(string restriccion)
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);

        // Control positivo: el borde permitido del fin (el mismo día del inicio) pasa. El del cierre va al final: escrito el
        // cierre, TR_ContratosAcceso_CierreIrreversible rechazaría cualquier cambio posterior antes de que llegue el CHECK.
        (await contexto.Database.ExecuteSqlAsync(
            $"UPDATE \"ContratosAcceso\" SET \"FechaFin\" = \"FechaInicio\" WHERE \"Id\" = {_contratoVigente};"))
            .Should().Be(1);

        var sql = restriccion switch
        {
            "CK_ContratosAcceso_FinNoAnteriorAlInicio" =>
                "UPDATE \"ContratosAcceso\" SET \"FechaFin\" = \"FechaInicio\" - 1 WHERE \"Id\" = {0};",
            "CK_ContratosAcceso_CierreNoAnteriorAlInicio" =>
                "UPDATE \"ContratosAcceso\" SET \"FechaCierreReal\" = \"FechaInicio\" - 1 WHERE \"Id\" = {0};",
            _ => "UPDATE \"ContratosAcceso\" SET \"EmpresaContratistaId\" = \"ClienteEmpresarialId\" WHERE \"Id\" = {0};",
        };
        var romper = () => contexto.Database.ExecuteSqlRawAsync(sql, _contratoVigente);
        var error = (await romper.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.Should().Be(restriccion);

        // Control positivo del cierre: el mismo día del inicio pasa.
        (await contexto.Database.ExecuteSqlAsync(
            $"UPDATE \"ContratosAcceso\" SET \"FechaCierreReal\" = \"FechaInicio\" WHERE \"Id\" = {_contratoVigente};"))
            .Should().Be(1);
    }

    [Fact]
    public async Task El_listado_traduce_a_SQL_el_filtro_de_vigencia_y_respeta_el_Tenant()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);
        var usuario = new CurrentUserServiceFalso(_administrador, Roles.Administrador, tenantOrigenId: _propietario.Id);
        var tenant = new TenantActualAmbiental { TenantId = _propietario.Id };
        var alcance = new AlcanceDatosService(contexto, usuario, tenant, new SesionPrivilegiadaAusente());
        var handler = new ObtenerContratosAccesoQueryHandler(contexto, alcance, new PoliticaNotaInterna(usuario, new SesionPrivilegiadaAusente(), tenant));

        (await handler.Handle(new ObtenerContratosAccesoQuery(CentroId: _centro), CancellationToken.None))
            .Select(c => c.Id).Should().BeEquivalentTo([_contratoVigente, _contratoCaducado]);
        var vigentes = await handler.Handle(
            new ObtenerContratosAccesoQuery(_centro, _contratista, DiaDeNegocio.Hoy()), CancellationToken.None);
        vigentes.Select(c => c.Id).Should().Equal([_contratoVigente]);
        vigentes.Single().VigenteHoy.Should().BeTrue();
        (await handler.Handle(new ObtenerContratosAccesoQuery(), CancellationToken.None))
            .Select(c => c.Id).Should().NotContain(_contratoAjeno);
    }

    // ── B-05: estado, condiciones e historial de vigencia ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Un_alta_de_la_ranura_anterior_sin_Estado_nace_Activo_y_el_dominio_lo_lee()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);
        var id = Guid.NewGuid();

        // El INSERT de la versión anterior, que no conoce las columnas de B-05.
        (await contexto.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ContratosAcceso" ("Id", "TenantId", "ClienteEmpresarialId", "CentroId", "EmpresaContratistaId",
                "FechaInicio", "Version", "CreadoEnUtc", "EstaEliminado")
            VALUES ({id}, {_propietario.Id}, {_cliente}, {_centro}, {_contratista}, {DiaDeNegocio.Hoy()}, {Guid.NewGuid()}, now(), false);
            """)).Should().Be(1);

        var leido = await contexto.ContratosAcceso.SingleAsync(c => c.Id == id);
        leido.Estado.Should().Be(EstadoContratoAcceso.Activo, "el DEFAULT de la columna es lo que daba de alta la versión anterior");
        leido.Condiciones.Should().Be(CondicionesContratoAcceso.SinFijar);
        leido.VigenteEn(DiaDeNegocio.Hoy()).Should().BeTrue();
    }

    [Fact]
    public async Task Un_Reabrir_de_la_ranura_anterior_sobre_un_contrato_finalizado_lo_rechaza_la_base()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);
        var contrato = await contexto.ContratosAcceso.SingleAsync(c => c.Id == _contratoVigente);
        contrato.Finalizar(DiaDeNegocio.Hoy());
        await contexto.SaveChangesAsync();

        var reabrir = () => contexto.Database.ExecuteSqlAsync(
            $"UPDATE \"ContratosAcceso\" SET \"FechaCierreReal\" = NULL WHERE \"Id\" = {_contratoVigente};");
        var error = (await reabrir.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.Should().Be("TR_ContratosAcceso_CierreIrreversible", "D-21 retira Reabrir, también por la puerta de atrás");
    }

    [Theory]
    [InlineData("NULL")]                          // el Reabrir de la ranura anterior
    [InlineData("\"FechaCierreReal\" + 1")]       // retrasar el cierre amplía el acceso
    [InlineData("\"FechaCierreReal\" - 1")]       // tampoco adelantarlo: el cierre escrito no cambia
    public async Task Un_cierre_que_escribio_la_ranura_anterior_tampoco_se_quita_ni_cambia_bajo_runtime(string nuevoCierre)
    {
        // Lo que Codex vio: tras la migración, el Cerrar de la ranura anterior deja Estado = 'Activo' con cierre real, y el
        // CHECK de Finalizado con cierre no lo cubre. El disparador sí, sea cual sea la columna Estado.
        var hoy = DiaDeNegocio.Hoy();
        await using var contexto = Contexto(_propietario.Id, _administrador);
        (await contexto.Database.ExecuteSqlAsync(
            $"UPDATE \"ContratosAcceso\" SET \"FechaCierreReal\" = {hoy} WHERE \"Id\" = {_contratoVigente};"))
            .Should().Be(1, "control: escribir el cierre sobre un contrato sin cierre (Cerrar) sigue valiendo");
        (await contexto.Database.ExecuteSqlAsync(
            $"UPDATE \"ContratosAcceso\" SET \"FechaCierreReal\" = {hoy}, \"Notas\" = 'Otra nota' WHERE \"Id\" = {_contratoVigente};"))
            .Should().Be(1, "control: reescribir el mismo cierre al editar otra columna no lo cambia");

        var sql = nuevoCierre switch
        {
            "NULL" => "UPDATE \"ContratosAcceso\" SET \"FechaCierreReal\" = NULL WHERE \"Id\" = {0};",
            "\"FechaCierreReal\" + 1" => "UPDATE \"ContratosAcceso\" SET \"FechaCierreReal\" = \"FechaCierreReal\" + 1 WHERE \"Id\" = {0};",
            _ => "UPDATE \"ContratosAcceso\" SET \"FechaCierreReal\" = \"FechaCierreReal\" - 1 WHERE \"Id\" = {0};",
        };
        var cambiar = () => contexto.Database.ExecuteSqlRawAsync(sql, _contratoVigente);
        var error = (await cambiar.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.Should().Be("TR_ContratosAcceso_CierreIrreversible");

        await using var lectura = Contexto(_propietario.Id, _administrador);
        var contrato = await lectura.ContratosAcceso.SingleAsync(c => c.Id == _contratoVigente);
        contrato.FechaCierreReal.Should().Be(hoy);
        contrato.Estado.Should().Be(EstadoContratoAcceso.Activo, "la ranura anterior no toca la columna Estado");
    }

    [Theory]
    [InlineData("FechaFin")]             // el Actualizar de la ranura anterior amplía el fin, que ya pasó
    [InlineData("FechaFinNula")]         // o lo quita: indefinido hasta el cierre
    [InlineData("FechaInicio")]          // adelantar el inicio amplía el acceso hacia atrás
    [InlineData("Estado")]               // un cerrado no cambia de estado (salvo Activo → Finalizado)
    public async Task Un_contrato_cerrado_no_cambia_sus_fechas_de_vigencia_ni_su_estado_bajo_runtime(string columna)
    {
        // Lo que vio Codex en la ronda 3: un contrato cuyo fin fue ayer se finaliza con cierre futuro (la ranura nueva lo
        // admite) y después el Actualizar de la ranura anterior, que no mira el cierre, amplía FechaFin. Sin el disparador,
        // la auxiliar volvería a dar lectura entre Tenants hasta el cierre.
        var hoy = DiaDeNegocio.Hoy();
        await using (var contexto = Contexto(_propietario.Id, _administrador))
        {
            var caducado = await contexto.ContratosAcceso.SingleAsync(c => c.Id == _contratoCaducado);
            caducado.FechaFin.Should().Be(hoy.AddDays(-1), "precondición: el fin contratado fue ayer");
            caducado.Finalizar(hoy.AddDays(10));
            await contexto.SaveChangesAsync();
        }

        await using var runtime = Contexto(_propietario.Id, _administrador);
        (await runtime.Database.ExecuteSqlAsync(
            $"UPDATE \"ContratosAcceso\" SET \"Notas\" = 'Nota tras el cierre', \"FechaFin\" = {hoy.AddDays(-1)} WHERE \"Id\" = {_contratoCaducado};"))
            .Should().Be(1, "control: editar otra columna y reescribir el mismo fin sigue valiendo");

        var sql = columna switch
        {
            "FechaFin" => "UPDATE \"ContratosAcceso\" SET \"FechaFin\" = \"FechaFin\" + 30 WHERE \"Id\" = {0};",
            "FechaFinNula" => "UPDATE \"ContratosAcceso\" SET \"FechaFin\" = NULL WHERE \"Id\" = {0};",
            "FechaInicio" => "UPDATE \"ContratosAcceso\" SET \"FechaInicio\" = \"FechaInicio\" - 30 WHERE \"Id\" = {0};",
            _ => "UPDATE \"ContratosAcceso\" SET \"Estado\" = 'Activo' WHERE \"Id\" = {0};",
        };
        var cambiar = () => runtime.Database.ExecuteSqlRawAsync(sql, _contratoCaducado);
        var error = (await cambiar.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.Should().Be("TR_ContratosAcceso_CierreIrreversible");

        await using var lectura = Contexto(_propietario.Id, _administrador);
        var contrato = await lectura.ContratosAcceso.SingleAsync(c => c.Id == _contratoCaducado);
        contrato.FechaInicio.Should().Be(hoy.AddDays(-60));
        contrato.FechaFin.Should().Be(hoy.AddDays(-1));
        contrato.FechaCierreReal.Should().Be(hoy.AddDays(10));
        contrato.Estado.Should().Be(EstadoContratoAcceso.Finalizado);
        contrato.VigenteEn(hoy).Should().BeFalse("el fin contratado ya pasó, aunque el cierre sea futuro");
    }

    [Fact]
    public async Task Un_cierre_de_la_ranura_anterior_se_puede_normalizar_de_Activo_a_Finalizado_y_no_volver()
    {
        // La única transición de estado que el disparador deja a un contrato cerrado: la normalización de los cierres que
        // escribió la ranura anterior con Estado = 'Activo'. No amplía nada: los dos dan acceso hasta el cierre.
        var hoy = DiaDeNegocio.Hoy();
        await using var contexto = Contexto(_propietario.Id, _administrador);
        (await contexto.Database.ExecuteSqlAsync(
            $"UPDATE \"ContratosAcceso\" SET \"FechaCierreReal\" = {hoy} WHERE \"Id\" = {_contratoVigente};")).Should().Be(1);
        (await contexto.Database.ExecuteSqlAsync(
            $"UPDATE \"ContratosAcceso\" SET \"Estado\" = 'Finalizado' WHERE \"Id\" = {_contratoVigente};"))
            .Should().Be(1, "Activo → Finalizado sobre un cerrado sigue valiendo");

        var volver = () => contexto.Database.ExecuteSqlAsync(
            $"UPDATE \"ContratosAcceso\" SET \"Estado\" = 'Activo' WHERE \"Id\" = {_contratoVigente};");
        (await volver.Should().ThrowAsync<PostgresException>()).Which.ConstraintName
            .Should().Be("TR_ContratosAcceso_CierreIrreversible", "Finalizado → Activo no");
    }

    [Theory]
    [InlineData("Suspendido", "Finalizado")]           // finalizar un suspendido le devolvería el acceso hasta el cierre
    [InlineData("Cancelado", "Finalizado")]            // un Cerrar de la ranura anterior sobre un Cancelado no lo hace Finalizado
    [InlineData("PendienteDeConfirmar", "Finalizado")]
    [InlineData("Rechazado", "Activo")]
    [InlineData("Activo", "Suspendido")]               // la excepción es Activo → Finalizado, no cualquier salida de Activo
    public async Task Un_contrato_cerrado_solo_pasa_de_Activo_a_Finalizado(string estado, string nuevoEstado)
    {
        // Precondición en una sola sentencia, sobre un contrato sin cierre: el disparador no actúa (OLD sin cierre), como el
        // Cerrar de la ranura anterior sobre un contrato que ella ve abierto.
        await using var contexto = Contexto(_propietario.Id, _administrador);
        var precondicion = estado switch
        {
            "Suspendido" => "UPDATE \"ContratosAcceso\" SET \"Estado\" = 'Suspendido', \"FechaCierreReal\" = \"FechaInicio\" + 20 WHERE \"Id\" = {0};",
            "Cancelado" => "UPDATE \"ContratosAcceso\" SET \"Estado\" = 'Cancelado', \"FechaCierreReal\" = \"FechaInicio\" + 20 WHERE \"Id\" = {0};",
            "PendienteDeConfirmar" => "UPDATE \"ContratosAcceso\" SET \"Estado\" = 'PendienteDeConfirmar', \"FechaCierreReal\" = \"FechaInicio\" + 20 WHERE \"Id\" = {0};",
            "Rechazado" => "UPDATE \"ContratosAcceso\" SET \"Estado\" = 'Rechazado', \"FechaCierreReal\" = \"FechaInicio\" + 20 WHERE \"Id\" = {0};",
            _ => "UPDATE \"ContratosAcceso\" SET \"Estado\" = 'Activo', \"FechaCierreReal\" = \"FechaInicio\" + 20 WHERE \"Id\" = {0};",
        };
        (await contexto.Database.ExecuteSqlRawAsync(precondicion, _contratoVigente)).Should().Be(1);

        var sql = nuevoEstado switch
        {
            "Finalizado" => "UPDATE \"ContratosAcceso\" SET \"Estado\" = 'Finalizado' WHERE \"Id\" = {0};",
            "Activo" => "UPDATE \"ContratosAcceso\" SET \"Estado\" = 'Activo' WHERE \"Id\" = {0};",
            _ => "UPDATE \"ContratosAcceso\" SET \"Estado\" = 'Suspendido' WHERE \"Id\" = {0};",
        };
        var cambiar = () => contexto.Database.ExecuteSqlRawAsync(sql, _contratoVigente);
        var error = (await cambiar.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.Should().Be("TR_ContratosAcceso_CierreIrreversible", $"{estado} → {nuevoEstado} sobre un cerrado");

        await using var lectura = Contexto(_propietario.Id, _administrador);
        (await lectura.ContratosAcceso.SingleAsync(c => c.Id == _contratoVigente)).Estado.ToString().Should().Be(estado);
    }

    [Fact]
    public async Task Un_Cerrar_de_la_ranura_anterior_deja_Activo_en_la_columna_y_el_dominio_lo_trata_como_Finalizado()
    {
        var hoy = DiaDeNegocio.Hoy();
        await using (var contexto = Contexto(_propietario.Id, _administrador))
        {
            // El Cerrar de la versión anterior, que no conoce la columna Estado.
            (await contexto.Database.ExecuteSqlAsync(
                $"UPDATE \"ContratosAcceso\" SET \"FechaCierreReal\" = {hoy} WHERE \"Id\" = {_contratoVigente};")).Should().Be(1);
        }

        await using var lectura = Contexto(_propietario.Id, _administrador);
        var contrato = await lectura.ContratosAcceso.SingleAsync(c => c.Id == _contratoVigente);
        contrato.Estado.Should().Be(EstadoContratoAcceso.Activo, "la columna sigue en el DEFAULT");
        contrato.EstadoEfectivo.Should().Be(EstadoContratoAcceso.Finalizado);
        contrato.Invoking(c => c.Suspender()).Should().Throw<InvalidOperationException>();
        contrato.Invoking(c => c.Renovar(hoy.AddDays(40), _administrador, DateTime.UtcNow)).Should().Throw<InvalidOperationException>();
        (await lectura.ContratosAcceso.Where(ContratoAcceso.EstaVigenteEn(hoy)).AnyAsync(c => c.Id == _contratoVigente))
            .Should().BeTrue("da acceso hasta el día del cierre, como un Finalizado");
        (await lectura.ContratosAcceso.Where(ContratoAcceso.EstaVigenteEn(hoy.AddDays(1))).AnyAsync(c => c.Id == _contratoVigente))
            .Should().BeFalse("y no después");
    }

    [Theory]
    [InlineData("CK_ContratosAcceso_Estado", "UPDATE \"ContratosAcceso\" SET \"Estado\" = 'Prorrogado' WHERE \"Id\" = {0};")]
    [InlineData("CK_ContratosAcceso_Tipo", "UPDATE \"ContratosAcceso\" SET \"Tipo\" = 'Indefinido' WHERE \"Id\" = {0};")]
    [InlineData("CK_ContratosAcceso_PropiaActividad", "UPDATE \"ContratosAcceso\" SET \"PropiaActividad\" = 'Quiza' WHERE \"Id\" = {0};")]
    [InlineData("CK_ContratosAcceso_NivelMaxSubcontrata", "UPDATE \"ContratosAcceso\" SET \"NivelMaxSubcontrata\" = 4 WHERE \"Id\" = {0};")]
    [InlineData("CK_ContratosAcceso_CuposNoNegativos", "UPDATE \"ContratosAcceso\" SET \"CupoTrabajadores\" = -1, \"MotivoCupo\" = 'x' WHERE \"Id\" = {0};")]
    [InlineData("CK_ContratosAcceso_MotivoSiHayCupo", "UPDATE \"ContratosAcceso\" SET \"CupoSubcontratas\" = 2 WHERE \"Id\" = {0};")]
    [InlineData("CK_ContratosAcceso_MotivoSiHayCupo", "UPDATE \"ContratosAcceso\" SET \"MotivoCupo\" = 'Sin cupo' WHERE \"Id\" = {0};")]
    [InlineData("CK_ContratosAcceso_MotivoSiHayCupo", "UPDATE \"ContratosAcceso\" SET \"CupoSubcontratas\" = 2, \"MotivoCupo\" = E' \\t ' WHERE \"Id\" = {0};")]
    [InlineData("CK_ContratosAcceso_FinalizadoConCierre", "UPDATE \"ContratosAcceso\" SET \"Estado\" = 'Finalizado' WHERE \"Id\" = {0};")]
    public async Task Un_UPDATE_por_SQL_que_rompe_una_condicion_de_B05_lo_rechaza_su_CHECK(string restriccion, string sql)
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);

        // Control positivo: unas condiciones válidas con cupo y su motivo pasan.
        (await contexto.Database.ExecuteSqlAsync($"""
            UPDATE "ContratosAcceso" SET "Tipo" = 'Obra', "PropiaActividad" = 'NoLoSe', "NivelMaxSubcontrata" = 3,
                "CupoTrabajadores" = 0, "MotivoCupo" = 'Aforo' WHERE "Id" = {_contratoVigente};
            """)).Should().Be(1);
        (await contexto.Database.ExecuteSqlAsync(
            $"UPDATE \"ContratosAcceso\" SET \"CupoTrabajadores\" = NULL, \"MotivoCupo\" = NULL WHERE \"Id\" = {_contratoVigente};")).Should().Be(1);

        var romper = () => contexto.Database.ExecuteSqlRawAsync(sql, _contratoVigente);
        var error = (await romper.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.Should().Be(restriccion);
    }

    [Fact]
    public async Task El_listado_de_vigentes_excluye_en_SQL_un_contrato_suspendido()
    {
        await using (var contexto = Contexto(_propietario.Id, _administrador))
        {
            var contrato = await contexto.ContratosAcceso.SingleAsync(c => c.Id == _contratoVigente);
            contrato.Suspender();
            await contexto.SaveChangesAsync();
        }

        await using var lectura = Contexto(_propietario.Id, _administrador);
        (await lectura.ContratosAcceso.Where(ContratoAcceso.EstaVigenteEn(DiaDeNegocio.Hoy())).Select(c => c.Id).ToListAsync())
            .Should().NotContain(_contratoVigente, "EstaVigenteEn traduce el estado a SQL igual que VigenteEn");
        (await lectura.ContratosAcceso.SingleAsync(c => c.Id == _contratoVigente)).Estado.Should().Be(EstadoContratoAcceso.Suspendido);
    }

    [Fact]
    public async Task Renovar_bajo_runtime_guarda_el_historial_y_otro_Tenant_no_lo_ve_ni_lo_escribe()
    {
        var hoy = DiaDeNegocio.Hoy();
        await using (var contexto = Contexto(_propietario.Id, _administrador))
        {
            var contrato = await contexto.ContratosAcceso.SingleAsync(c => c.Id == _contratoVigente);
            contrato.Renovar(hoy.AddDays(40), _administrador, DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = Contexto(_propietario.Id, _administrador))
        {
            var cambio = await contexto.HistorialVigenciaContratos.IgnoreQueryFilters().SingleAsync();
            cambio.TenantId.Should().Be(_propietario.Id, "el interceptor sella el Tenant del contrato");
            cambio.ContratoAccesoId.Should().Be(_contratoVigente);
            cambio.FechaFinAnterior.Should().Be(hoy.AddDays(10));
            cambio.FechaFinNueva.Should().Be(hoy.AddDays(40));
            cambio.Origen.Should().Be(OrigenCambioVigencia.Renovacion);
            (await contexto.ContratosAcceso.Include(c => c.HistorialVigencia).SingleAsync(c => c.Id == _contratoVigente))
                .HistorialVigencia.Should().ContainSingle();
        }

        await using var ajeno = Contexto(_ajeno.Id, _administradorAjeno);
        (await ajeno.HistorialVigenciaContratos.IgnoreQueryFilters().CountAsync())
            .Should().Be(0, "RLS oculta el historial del Tenant propietario sin ayuda del filtro de EF");

        // Escritura desde el Tenant ajeno: con su propio TenantId, la FK compuesta no encuentra el contrato (que tampoco ve);
        // con el TenantId del propietario, el WITH CHECK la rechaza.
        Task<int> Insertar(Guid tenantId) => ajeno.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "HistorialVigenciaContratos" ("Id", "TenantId", "ContratoAccesoId", "FechaFinAnterior", "FechaFinNueva",
                "Origen", "RegistradoPorUsuarioId", "RegistradoEnUtc")
            VALUES ({Guid.NewGuid()}, {tenantId}, {_contratoVigente}, {hoy}, {hoy.AddDays(1)}, 'Edicion', {_administradorAjeno}, now());
            """);
        var conTenantAjeno = () => Insertar(_propietario.Id);
        (await conTenantAjeno.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "aislamiento_tenant no deja escribir otro TenantId");
        var conSuTenant = () => Insertar(_ajeno.Id);
        (await conSuTenant.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation, "la FK (TenantId, ContratoAccesoId) no casa en el Tenant ajeno");
    }

    [Fact]
    public async Task El_historial_es_solo_de_insercion_para_el_runtime_y_de_lectura_para_soporte()
    {
        var hoy = DiaDeNegocio.Hoy();
        await using var contexto = Contexto(_propietario.Id, _administrador);
        var id = Guid.NewGuid();
        (await contexto.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "HistorialVigenciaContratos" ("Id", "TenantId", "ContratoAccesoId", "FechaFinAnterior", "FechaFinNueva",
                "Origen", "RegistradoPorUsuarioId", "RegistradoEnUtc")
            VALUES ({id}, {_propietario.Id}, {_contratoVigente}, {hoy.AddDays(10)}, {hoy.AddDays(20)}, 'Edicion', {_administrador}, now());
            """)).Should().Be(1, "control positivo: el runtime inserta en su Tenant");

        foreach (var (escribir, que) in new (Func<Task<int>>, string)[]
                 {
                     (() => contexto.Database.ExecuteSqlAsync($"UPDATE \"HistorialVigenciaContratos\" SET \"FechaFinNueva\" = {hoy} WHERE \"Id\" = {id};"), "UPDATE"),
                     (() => contexto.Database.ExecuteSqlAsync($"DELETE FROM \"HistorialVigenciaContratos\" WHERE \"Id\" = {id};"), "DELETE"),
                 })
        {
            (await escribir.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, $"cae_app_runtime no tiene {que} sobre el historial");
        }

        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using (var catalogo = new NpgsqlCommand(
                         "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = 'public.\"HistorialVigenciaContratos\"'::regclass",
                         conexion))
        {
            ((bool)(await catalogo.ExecuteScalarAsync())!).Should().BeTrue("RLS activa y forzada también para el propietario");
        }

        await using var transaccion = await conexion.BeginTransactionAsync();
        var contextoFirmado = await ContextoRlsDePruebas.FirmarAsync(conexion, tenantId: _propietario.Id);
        await using (var preparar = conexion.CreateCommand())
        {
            preparar.CommandText = "SELECT set_config('app.contexto', @contexto, true); SET LOCAL ROLE cae_app_soporte;";
            preparar.Parameters.AddWithValue("contexto", contextoFirmado);
            await preparar.ExecuteNonQueryAsync();
        }
        await using (var leer = conexion.CreateCommand())
        {
            leer.CommandText = "SELECT count(*) FROM \"HistorialVigenciaContratos\";";
            ((long)(await leer.ExecuteScalarAsync())!).Should().Be(1, "Soporte lee el historial de su Tenant");
        }
        await using var escribirSoporte = conexion.CreateCommand();
        escribirSoporte.CommandText = $"DELETE FROM \"HistorialVigenciaContratos\" WHERE \"Id\" = '{id}';";
        (await FluentActions.Awaiting(() => escribirSoporte.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "la migración solo concede SELECT a cae_app_soporte");
    }

    [Fact]
    public async Task Borrar_un_contrato_con_historial_con_la_identidad_de_la_retirada_borra_su_historial()
    {
        // La retirada de un Tenant de demostración (RetiradaTenantDemoService) corre con la identidad de bootstrap y borra
        // los contratos; la ranura anterior no conoce el historial y emite solo el DELETE del contrato, que es lo que hace
        // este test por SQL. Con la FK en RESTRICT fallaría y dejaría la retirada a medias.
        var hoy = DiaDeNegocio.Hoy();
        await using (var contexto = Contexto(_propietario.Id, _administrador))
        {
            var contrato = await contexto.ContratosAcceso.SingleAsync(c => c.Id == _contratoVigente);
            contrato.Renovar(hoy.AddDays(40), _administrador, DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        await using var bootstrap = Contexto(_propietario.Id);
        (await bootstrap.Database.SqlQueryRaw<bool>(
                "SELECT (rolsuper OR rolbypassrls) AS \"Value\" FROM pg_roles WHERE rolname = current_user").SingleAsync())
            .Should().BeTrue("la retirada usa la identidad de bootstrap, que salta RLS");
        (await bootstrap.Database.SqlQueryRaw<string>(
                "SELECT confdeltype::text AS \"Value\" FROM pg_constraint WHERE conname = 'FK_HistorialVigenciaContratos_ContratosAcceso_TenantId_Contrat~'")
            .SingleAsync()).Should().Be("c", "la FK del historial hacia el contrato es ON DELETE CASCADE");
        (await bootstrap.HistorialVigenciaContratos.IgnoreQueryFilters().CountAsync(h => h.ContratoAccesoId == _contratoVigente))
            .Should().Be(1, "control positivo: el contrato tiene historial antes de borrarlo");

        (await bootstrap.Database.ExecuteSqlAsync($"DELETE FROM \"ContratosAcceso\" WHERE \"Id\" = {_contratoVigente};"))
            .Should().Be(1, "el borrado del contrato no lo bloquea su historial");

        (await bootstrap.HistorialVigenciaContratos.IgnoreQueryFilters().CountAsync(h => h.ContratoAccesoId == _contratoVigente))
            .Should().Be(0, "el historial se va con su contrato");
        (await bootstrap.ContratosAcceso.IgnoreQueryFilters().CountAsync(c => c.Id == _contratoCaducado))
            .Should().Be(1, "solo se borra el contrato pedido");
    }

    private static void Cuenta(CaeManagerDbContext contexto, Guid id, Guid tenantId, Guid rolId)
    {
        contexto.Users.Add(new ApplicationUser
        {
            Id = id,
            TenantId = tenantId,
            UserName = $"{id:N}@caemanager.local",
            NormalizedUserName = $"{id:N}@CAEMANAGER.LOCAL",
            Email = $"{id:N}@caemanager.local",
            NormalizedEmail = $"{id:N}@CAEMANAGER.LOCAL",
            NombreCompleto = "Administrador",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString()
        });
        contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = rolId });
    }

    private CaeManagerDbContext Contexto(Guid tenantId, Guid? usuario = null)
    {
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(
            usuario is null ? _cadena : BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena),
            n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        opciones.AddInterceptors(new TenantSelladoInterceptor(tenant));
        if (usuario is not null)
            opciones.AddInterceptors(new TenantRlsConnectionInterceptor(tenant, new SinTenantSeleccionado(),
                new CurrentUserServiceFalso(usuario.Value, Roles.Administrador, tenantOrigenId: tenantId), BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor());
        return new(opciones.Options, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
