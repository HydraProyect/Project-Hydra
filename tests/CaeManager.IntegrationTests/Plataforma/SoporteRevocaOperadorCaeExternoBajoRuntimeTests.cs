using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using PlataformaWriter = CaeManager.Infrastructure.Plataforma.PlataformaWriter;
using RevocacionOperadorCaeExternoPorSoporte = CaeManager.Infrastructure.Plataforma.RevocacionOperadorCaeExternoPorSoporte;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Enmienda de ADR-004 § 12.2 (2026-10-26): Soporte TALVEG revoca la Delegación de un Operador
/// CAE externo a petición del Tenant propietario, desde una Sesión Privilegiada con la capacidad
/// <c>RevocacionOperadorCaeExterno</c>, contra PostgreSQL real y con la identidad de conexión de
/// producción, <c>cae_app_runtime</c>, que con la sesión adopta <c>cae_app_soporte</c>. El
/// propietario solo siembra y lee lo que quedó escrito.
///
/// <para>
/// Lo que se ataca: la conexión de la sesión sigue siendo de solo lectura y la RLS no se
/// ensancha. La única escritura posible es <c>app_revocar_operador_cae_externo_por_soporte</c>, y
/// las precondiciones de sesión, concesión, Tenant, actor, motivo y Delegación tienen aquí un caso
/// que las incumple y deja la Delegación, la operación y las carteras intactas y sin auditoría
/// (el actor de un Tenant de plataforma no tiene caso propio). Las reglas de Application están en
/// <c>RevocarOperadorCaeExternoPorSoporteCommandTests</c>.
/// </para>
/// </summary>
public class SoporteRevocaOperadorCaeExternoBajoRuntimeTests : IAsyncLifetime
{
    private const string Motivo = "Petición del Tenant propietario, ticket SOP-0042";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tenantOperador = Guid.NewGuid();
    private readonly Guid _tecnico = Guid.NewGuid();
    private readonly Guid _adminPlataforma = Guid.NewGuid();
    private readonly Guid _gestora = Guid.NewGuid();
    private readonly Guid _administradorDeA = Guid.NewGuid();

    private Guid _delegacion;
    private Guid _operacion;
    private Guid _cartera;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            contexto.Tenants.Add(CrearTenant(_tenantPlataforma, "Plataforma", esPlataforma: true));
            contexto.Tenants.Add(CrearTenant(_tenantA, "Tenant propietario A S.L."));
            contexto.Tenants.Add(CrearTenant(_tenantB, "Tenant propietario B S.L."));
            contexto.Tenants.Add(CrearTenant(_tenantOperador, "Operador CAE externo S.L."));
            SembrarCuenta(contexto, "tecnico", _tenantPlataforma, _tecnico);
            SembrarCuenta(contexto, "admin-plataforma", _tenantPlataforma, _adminPlataforma);
            SembrarCuenta(contexto, "gestora", _tenantOperador, _gestora);
            SembrarCuenta(contexto, "admin-a", _tenantA, _administradorDeA);
            await contexto.SaveChangesAsync();
        }

        // La Delegación, su operación externa y la cartera principal de la Gestora CAE, como las
        // deja CrearDelegacionTenantCommand. Como propietario: aquí solo se prepara el estado.
        await using (var contexto = CrearContexto(_tenantA, cadena: _cadenaConexion))
        {
            var ahora = DateTime.UtcNow.AddMinutes(-30);
            var delegacion = new DelegacionTenant(_tenantOperador, _tenantA);
            var operacion = AsignacionOperacion.Externa(
                _tenantA, _tenantOperador, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora, null, ahora, _gestora);
            var cartera = AsignacionCartera.Externa(
                operacion, _gestora, "GestorCae", AmbitoAsignacion.Universal, ahora, vigenciaHasta: null, ahora, _gestora);
            cartera.DesignarPrincipal();
            contexto.DelegacionesTenant.Add(delegacion);
            contexto.AsignacionesOperacion.Add(operacion);
            contexto.AsignacionesCartera.Add(cartera);
            await contexto.SaveChangesAsync();
            (_delegacion, _operacion, _cartera) = (delegacion.Id, operacion.Id, cartera.Id);
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Camino positivo ──────────────────────────────────────────────────────

    [Fact]
    public async Task Con_la_capacidad_revoca_cierra_operacion_y_carteras_y_deja_la_auditoria_de_la_sesion()
    {
        var sesion = await AbrirSesionConCapacidadAsync();

        await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion))
        {
            var resultado = await new RevocacionOperadorCaeExternoPorSoporte(contexto, new PuertaAccesoDatos())
                .RevocarAsync(sesion, _delegacion, Motivo, CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        }

        (await EscalarAsync<bool>(@"SELECT ""Activa"" FROM ""DelegacionesTenant"" WHERE ""Id"" = @u;", _delegacion))
            .Should().BeFalse();
        (await EscalarAsync<string>(
            @"SELECT ""Estado"" || '/' || ""MotivoCierre"" FROM ""AsignacionesOperacion"" WHERE ""Id"" = @u;", _operacion))
            .Should().Be("Cerrada/Revocada");
        (await EscalarAsync<string>(
            @"SELECT ""Estado"" || '/' || ""MotivoCierre"" || '/' || ""EsPrincipal"" || '/' || ""EraPrincipalAlCerrarsePorCascada""
              FROM ""AsignacionesCartera"" WHERE ""Id"" = @u;", _cartera))
            .Should().Be("Cerrada/Revocada/false/true",
                "la cascada apaga la marca y recuerda que la llevaba: es lo que la reactivación normal devuelve (D-9)");
        (await EscalarAsync<bool>(
            @"SELECT c.""VigenciaHasta"" = o.""VigenciaHasta"" FROM ""AsignacionesCartera"" c
              JOIN ""AsignacionesOperacion"" o ON o.""Id"" = c.""AsignacionOperacionId"" WHERE c.""Id"" = @u;", _cartera))
            .Should().BeTrue("operación y carteras se cierran con el mismo instante, como CerrarOperacionDelegadaAsync");

        var filas = await LeerAuditoriaDeSesionAsync(sesion);
        filas.Should().HaveCount(3, "una fila por la Delegación, otra por la operación y otra por la cartera");
        filas.Should().OnlyContain(f =>
            f.TenantId == _tenantA && f.UsuarioId == _tecnico && f.ActorRealUsuarioId == _tecnico
            && f.TipoActor == "Persona" && f.Accion == "Modificado",
            "el Actor real es el técnico de Soporte TALVEG y la fila queda en el Tenant propietario");
        filas.Select(f => (f.EntidadTipo, f.EntidadId)).Should().BeEquivalentTo(
            [("DelegacionTenant", _delegacion), ("AsignacionOperacion", _operacion), ("AsignacionCartera", _cartera)]);
        var deLaDelegacion = filas.Single(f => f.EntidadTipo == "DelegacionTenant").DatosDespues;
        deLaDelegacion.Should().Contain(Motivo).And.Contain(_tenantOperador.ToString(),
            "el motivo de la petición y el Operador CAE externo quedan en la auditoría");
    }

    /// <summary>
    /// Decisión de Chris del 2026-11-14 (hallazgo de Codex en la revisión de la PR #2215): la vía de
    /// Soporte TALVEG cierra lo mismo que <c>CerrarOperacionDelegadaAsync</c>, toda Asignación de
    /// Operación Outbound del par, del Tenant entero o acotada a un Cliente empresarial, vigente o
    /// programada, y sus carteras. Inbound tiene su propio ciclo y queda intacta.
    /// </summary>
    [Fact]
    public async Task Con_la_capacidad_revoca_cierra_tambien_las_operaciones_Outbound_acotadas_y_sus_carteras()
    {
        Guid acotadaVigente, acotadaProgramada, inbound, carteraAcotada, carteraProgramada, carteraInbound;
        await using (var contexto = CrearContexto(_tenantA, cadena: _cadenaConexion))
        {
            var ahora = DateTime.UtcNow.AddMinutes(-30);
            var uno = CaeManager.Domain.Empresas.Empresa.CrearComoCliente("Cliente acotado uno S.A.", "B10380202", false, null, null);
            var dos = CaeManager.Domain.Empresas.Empresa.CrearComoCliente("Cliente acotado dos S.A.", "B10380228", false, null, null);
            contexto.Empresas.AddRange(uno, dos);
            var vigente = AsignacionOperacion.Externa(_tenantA, _tenantOperador, ServicioCae.Outbound,
                AmbitoAsignacion.DeRelacionCliente(uno.Id), ahora, null, ahora, _gestora);
            var programada = AsignacionOperacion.Externa(_tenantA, _tenantOperador, ServicioCae.Outbound,
                AmbitoAsignacion.DeRelacionCliente(dos.Id), ahora.AddDays(2), null, ahora, _gestora);
            var deInbound = AsignacionOperacion.Externa(_tenantA, _tenantOperador, ServicioCae.Inbound,
                AmbitoAsignacion.Universal, ahora, null, ahora, _gestora);
            var c1 = AsignacionCartera.Externa(vigente, _gestora, "GestorCae", AmbitoAsignacion.Universal, ahora, null, ahora, _gestora);
            // Programada y principal: la función la cierra sin recordar la marca (D-9).
            var c2 = AsignacionCartera.Externa(programada, _gestora, "GestorCae", AmbitoAsignacion.Universal, ahora.AddDays(3), null, ahora, _gestora);
            c2.DesignarPrincipal();
            var c3 = AsignacionCartera.Externa(deInbound, _gestora, "GestorCae", AmbitoAsignacion.Universal, ahora, null, ahora, _gestora);
            contexto.AsignacionesOperacion.AddRange(vigente, programada, deInbound);
            contexto.AsignacionesCartera.AddRange(c1, c2, c3);
            await contexto.SaveChangesAsync();
            programada.Estado.Should().Be(EstadoAsignacion.Programada, "control: empieza en el futuro");
            (acotadaVigente, acotadaProgramada, inbound, carteraAcotada, carteraProgramada, carteraInbound) =
                (vigente.Id, programada.Id, deInbound.Id, c1.Id, c2.Id, c3.Id);
        }

        var sesion = await AbrirSesionConCapacidadAsync();
        (await RevocarComoSoporteAsync(_tenantA, sesion)).Should().Be("revocada");

        foreach (var operacion in new[] { _operacion, acotadaVigente, acotadaProgramada })
            (await EscalarAsync<string>(
                @"SELECT ""Estado"" || '/' || ""MotivoCierre"" FROM ""AsignacionesOperacion"" WHERE ""Id"" = @u;", operacion))
                .Should().Be("Cerrada/Revocada", "toda Asignación de Operación Outbound del par se cierra, también las acotadas");
        foreach (var cartera in new[] { _cartera, carteraAcotada, carteraProgramada })
            (await EscalarAsync<string>(
                @"SELECT ""Estado"" || '/' || ""MotivoCierre"" FROM ""AsignacionesCartera"" WHERE ""Id"" = @u;", cartera))
                .Should().Be("Cerrada/Revocada", "y todas sus carteras");
        (await EscalarAsync<string>(
            @"SELECT ""EraPrincipalAlCerrarsePorCascada""::text FROM ""AsignacionesCartera"" WHERE ""Id"" = @u;", carteraAcotada))
            .Should().Be("false", "solo recuerda la marca la cartera que la llevaba (D-9)");
        (await EscalarAsync<string>(
            @"SELECT ""EsPrincipal""::text || '/' || ""EraPrincipalAlCerrarsePorCascada""::text FROM ""AsignacionesCartera"" WHERE ""Id"" = @u;", carteraProgramada))
            .Should().Be("false/false", "una cartera principal que no estaba vigente se cierra sin recordar la marca");
        (await EscalarAsync<bool>(
            @"SELECT ""VigenciaHasta"" = ""VigenciaDesde"" FROM ""AsignacionesOperacion"" WHERE ""Id"" = @u;", acotadaProgramada))
            .Should().BeTrue("la programada se cierra sin haber empezado, como Cerrar en C#");

        (await EscalarAsync<string>(@"SELECT ""Estado"" FROM ""AsignacionesOperacion"" WHERE ""Id"" = @u;", inbound))
            .Should().Be("Vigente", "Inbound tiene su propio ciclo");
        (await EscalarAsync<string>(@"SELECT ""Estado"" FROM ""AsignacionesCartera"" WHERE ""Id"" = @u;", carteraInbound))
            .Should().Be("Vigente");

        var filas = await LeerAuditoriaDeSesionAsync(sesion);
        filas.Select(f => (f.EntidadTipo, f.EntidadId)).Should().BeEquivalentTo(
            [("DelegacionTenant", _delegacion),
             ("AsignacionOperacion", _operacion), ("AsignacionOperacion", acotadaVigente), ("AsignacionOperacion", acotadaProgramada),
             ("AsignacionCartera", _cartera), ("AsignacionCartera", carteraAcotada), ("AsignacionCartera", carteraProgramada)],
            "una fila de auditoría por cada fila que la función escribe, y ninguna de Inbound");
    }

    /// <summary>
    /// Rondas 1 y 2 de Codex en la PR #2223, con la opción A (decisión de Chris del 2026-11-14):
    /// reactivar no repone ninguna cartera, así que la función ya no marca nada como restaurable. Cierra
    /// por revocación toda cartera no cerrada del par, en cualquier estado —una Suspendida cuya fecha de
    /// fin ya pasada coincide con la de su operación, una Vigente principal bajo una operación Suspendida
    /// y la Vigente principal bajo la del Tenant entero— y apaga la marca de principal de todas.
    /// </summary>
    [Fact]
    public async Task Con_la_capacidad_cierra_toda_cartera_del_par_en_cualquier_estado_y_apaga_la_marca_de_principal()
    {
        var otraGestora = Guid.NewGuid();
        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            SembrarCuenta(contexto, "otra-gestora", _tenantOperador, otraGestora);
            await contexto.SaveChangesAsync();
        }

        Guid suspendida, bajoSuspendida;
        await using (var contexto = CrearContexto(_tenantA, cadena: _cadenaConexion))
        {
            var ahora = DateTime.UtcNow.AddMinutes(-30);
            var finPasado = DateTime.UtcNow.AddMinutes(-1);
            var operacion = await contexto.AsignacionesOperacion.SingleAsync(o => o.Id == _operacion);
            var cartera = AsignacionCartera.Externa(
                operacion, otraGestora, "GestorCae", AmbitoAsignacion.Universal, ahora, finPasado, ahora, _gestora);
            cartera.Suspender();

            var cliente = CaeManager.Domain.Empresas.Empresa.CrearComoCliente("Cliente suspendido S.A.", "B10380202", false, null, null);
            contexto.Empresas.Add(cliente);
            var acotada = AsignacionOperacion.Externa(_tenantA, _tenantOperador, ServicioCae.Outbound,
                AmbitoAsignacion.DeRelacionCliente(cliente.Id), ahora, null, ahora, _gestora);
            var vigenteBajoSuspendida = AsignacionCartera.Externa(
                acotada, _gestora, "GestorCae", AmbitoAsignacion.Universal, ahora, null, ahora, _gestora);
            vigenteBajoSuspendida.DesignarPrincipal();
            acotada.Suspender();

            contexto.AsignacionesOperacion.Add(acotada);
            contexto.AsignacionesCartera.AddRange(cartera, vigenteBajoSuspendida);
            await contexto.SaveChangesAsync();
            (suspendida, bajoSuspendida) = (cartera.Id, vigenteBajoSuspendida.Id);
        }

        // La colisión: la operación vigente del Tenant entero con la misma fecha de fin ya pasada que la
        // suspendida (el job de expiración aún no la ha cerrado). Cerrar conserva esa fecha en las dos.
        await EjecutarAsync(
            @"UPDATE ""AsignacionesOperacion"" SET ""VigenciaHasta"" =
                (SELECT ""VigenciaHasta"" FROM ""AsignacionesCartera"" WHERE ""Id"" = @u)
              WHERE ""Id"" = (SELECT ""AsignacionOperacionId"" FROM ""AsignacionesCartera"" WHERE ""Id"" = @u);", suspendida);

        var sesion = await AbrirSesionConCapacidadAsync();
        (await RevocarComoSoporteAsync(_tenantA, sesion)).Should().Be("revocada");

        const string Marca = @"
SELECT c.""Estado"" || '/' || c.""MotivoCierre"" || '/' || c.""EsPrincipal""::text || '/' ||
       CASE WHEN c.""VigenciaHasta"" = o.""VigenciaHasta"" THEN 'igual' ELSE 'otro' END
FROM ""AsignacionesCartera"" c JOIN ""AsignacionesOperacion"" o ON o.""Id"" = c.""AsignacionOperacionId""
WHERE c.""Id"" = @u;";
        (await EscalarAsync<string>(Marca, suspendida)).Should().Be("Cerrada/Revocada/false/igual",
            "la suspendida se cierra y conserva la fecha de fin que compartía con su operación");
        (await EscalarAsync<string>(Marca, bajoSuspendida)).Should().Be("Cerrada/Revocada/false/igual",
            "la principal bajo una operación suspendida se cierra sin la marca de principal");
        (await EscalarAsync<string>(Marca, _cartera)).Should().StartWith("Cerrada/Revocada/false/",
            "y la principal bajo la del Tenant entero, también");
    }

    // ── Cada precondición de la función, incumplida ──────────────────────────

    [Fact]
    public async Task Otro_actor_no_puede_ejercer_la_sesion()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        var antes = await InstantaneaAsync();
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion, actor: _adminPlataforma);
        (await LlamarFuncionAsync(contexto, sesion, _delegacion, Motivo)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Theory]
    [InlineData(@"UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada' WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);")]
    [InlineData(@"UPDATE ""ConcesionesPrivilegio"" SET ""VigenciaHasta"" = now() - interval '1 minute' WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);")]
    [InlineData(@"UPDATE ""TenantsAlcanzadosPorConcesion"" SET ""TenantId"" = gen_random_uuid() WHERE ""ConcesionPrivilegioId"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);")]
    [InlineData(@"UPDATE ""SesionesPrivilegiadas"" SET ""UsuarioSimuladoId"" = gen_random_uuid() WHERE ""Id"" = @u;")]
    public async Task Concesion_no_vigente_sin_Tenant_o_sesion_con_simulacion_no_revoca(string preparacion)
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarAsync(preparacion, sesion);
        var antes = await InstantaneaAsync();
        (await RevocarComoSoporteAsync(_tenantA, sesion)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task Un_motivo_de_mas_de_500_caracteres_no_revoca()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        var antes = await InstantaneaAsync();
        (await RevocarComoSoporteAsync(_tenantA, sesion, new string('a', 501))).Should().Be("motivo_no_valido");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task La_operacion_de_otro_Operador_CAE_externo_queda_intacta()
    {
        // El índice IX_AsignacionesOperacion_DelegacionTotalVigente no admite dos delegaciones
        // totales Outbound vigentes del mismo propietario: la del Operador CAE externo original se
        // cierra antes de sembrar la del otro, para que solo el filtro por Operador la separe.
        Guid otraOperacion;
        await using (var contexto = CrearContexto(_tenantA, cadena: _cadenaConexion))
        {
            var ahora = DateTime.UtcNow.AddMinutes(-30);
            var original = await contexto.AsignacionesOperacion.SingleAsync(o => o.Id == _operacion);
            original.Cerrar(MotivoCierreAsignacion.Revocada, DateTime.UtcNow);
            var otra = AsignacionOperacion.Externa(_tenantA, _tenantB, ServicioCae.Outbound,
                AmbitoAsignacion.Universal, ahora, null, ahora, _gestora);
            contexto.AsignacionesOperacion.Add(otra);
            await contexto.SaveChangesAsync();
            otraOperacion = otra.Id;
        }
        var sesion = await AbrirSesionConCapacidadAsync();
        var antes = await FilaAsync("AsignacionesOperacion", otraOperacion);
        (await RevocarComoSoporteAsync(_tenantA, sesion)).Should().Be("revocada");
        (await FilaAsync("AsignacionesOperacion", otraOperacion)).Should().Be(antes);
        (await LeerAuditoriaDeSesionAsync(sesion)).Should().NotContain(f => f.EntidadId == otraOperacion);
    }

    [Fact]
    public async Task La_operacion_de_otro_servicio_del_mismo_Operador_CAE_externo_queda_intacta()
    {
        Guid inbound;
        await using (var contexto = CrearContexto(_tenantA, cadena: _cadenaConexion))
        {
            var ahora = DateTime.UtcNow.AddMinutes(-30);
            var operacion = AsignacionOperacion.Externa(_tenantA, _tenantOperador, ServicioCae.Inbound,
                AmbitoAsignacion.Universal, ahora, null, ahora, _gestora);
            contexto.AsignacionesOperacion.Add(operacion);
            await contexto.SaveChangesAsync();
            inbound = operacion.Id;
        }
        var sesion = await AbrirSesionConCapacidadAsync();
        var antes = await FilaAsync("AsignacionesOperacion", inbound);
        (await RevocarComoSoporteAsync(_tenantA, sesion)).Should().Be("revocada");
        (await FilaAsync("AsignacionesOperacion", inbound)).Should().Be(antes);
        (await LeerAuditoriaDeSesionAsync(sesion)).Should().NotContain(f => f.EntidadId == inbound);
    }

    [Fact]
    public async Task Un_motivo_de_exactamente_500_caracteres_con_espacios_alrededor_si_revoca()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        (await RevocarComoSoporteAsync(_tenantA, sesion, "  " + new string('a', 500) + "  ")).Should().Be("revocada");
    }

    [Fact]
    public async Task Una_cartera_ya_cerrada_queda_intacta_al_revocar()
    {
        await using (var contexto = CrearContexto(_tenantA, cadena: _cadenaConexion))
        {
            var cartera = await contexto.AsignacionesCartera.SingleAsync(c => c.Id == _cartera);
            cartera.Cerrar(MotivoCierreAsignacion.Revocada, DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }
        var sesion = await AbrirSesionConCapacidadAsync();
        var antes = await FilaAsync("AsignacionesCartera", _cartera);
        (await RevocarComoSoporteAsync(_tenantA, sesion)).Should().Be("revocada");
        (await FilaAsync("AsignacionesCartera", _cartera)).Should().Be(antes);
        (await LeerAuditoriaDeSesionAsync(sesion)).Should().NotContain(f => f.EntidadId == _cartera);
    }

    [Fact]
    public async Task Una_sesion_de_SoporteLectura_no_revoca()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();

        (await RevocarComoSoporteAsync(_tenantA, sesion)).Should().Be("sesion_no_autorizada");
        await IntactaAsync(sesion);
    }

    [Fact]
    public async Task Una_sesion_por_Tenant_de_otra_capacidad_acotada_no_revoca()
    {
        var sesion = await AbrirSesionConCapacidadAsync(capacidad: CapacidadPrivilegio.RestablecimientoSegundoFactor);

        (await RevocarComoSoporteAsync(_tenantA, sesion)).Should().Be("sesion_no_autorizada");
        await IntactaAsync(sesion);
    }

    [Theory]
    [InlineData(@"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = now() WHERE ""Id"" = @u;")]
    [InlineData(@"UPDATE ""SesionesPrivilegiadas"" SET ""ExpiraEnUtc"" = now() - interval '1 minute' WHERE ""Id"" = @u;")]
    public async Task Una_sesion_cerrada_o_caducada_no_revoca(string preparacion)
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarAsync(preparacion, sesion);

        (await RevocarComoSoporteAsync(_tenantA, sesion)).Should().Be("sesion_no_autorizada");
        await IntactaAsync(sesion);
    }

    [Fact]
    public async Task Una_concesion_que_se_dio_el_propio_tecnico_no_revoca()
    {
        var sesion = await AbrirSesionConCapacidadAsync(concedidaPor: _tecnico);

        (await RevocarComoSoporteAsync(_tenantA, sesion)).Should().Be("sesion_no_autorizada");
        await IntactaAsync(sesion);
    }

    [Fact]
    public async Task Una_sesion_sobre_otro_Tenant_no_alcanza_la_Delegacion_del_Tenant_propietario()
    {
        var sesionSobreB = await AbrirSesionConCapacidadAsync(tenantObjetivo: _tenantB);

        (await RevocarComoSoporteAsync(_tenantB, sesionSobreB)).Should().Be("delegacion_no_encontrada",
            "la Delegación es del Tenant propietario A, no del Tenant objetivo de la sesión");
        (await RevocarComoSoporteAsync(_tenantA, sesionSobreB)).Should().Be("sesion_no_autorizada",
            "con el contexto de A, una sesión abierta sobre B no ampara nada");
        await IntactaAsync(sesionSobreB);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Sin_motivo_no_revoca(string motivo)
    {
        var sesion = await AbrirSesionConCapacidadAsync();

        (await RevocarComoSoporteAsync(_tenantA, sesion, motivo)).Should().Be("motivo_no_valido");
        await IntactaAsync(sesion);
    }

    [Fact]
    public async Task Una_Delegacion_de_Soporte_no_se_revoca_por_aqui()
    {
        Guid deSoporte;
        await using (var contexto = CrearContexto(_tenantA, cadena: _cadenaConexion))
        {
            var delegacion = DelegacionTenant.ParaSoporte(_tenantPlataforma, _tenantA);
            contexto.DelegacionesTenant.Add(delegacion);
            await contexto.SaveChangesAsync();
            deSoporte = delegacion.Id;
        }
        var sesion = await AbrirSesionConCapacidadAsync();

        (await RevocarComoSoporteAsync(_tenantA, sesion, delegacion: deSoporte)).Should().Be("delegacion_no_encontrada");
        await IntactaAsync(sesion);
    }

    [Fact]
    public async Task Una_Delegacion_ya_revocada_no_se_vuelve_a_revocar()
    {
        var sesion = await AbrirSesionConCapacidadAsync();

        (await RevocarComoSoporteAsync(_tenantA, sesion)).Should().Be("revocada");
        (await RevocarComoSoporteAsync(_tenantA, sesion)).Should().Be("ya_inactiva");
        (await LeerAuditoriaDeSesionAsync(sesion)).Should().HaveCount(3, "la segunda llamada no escribe nada");
    }

    [Fact]
    public async Task Un_usuario_de_negocio_sin_sesion_no_puede_ejecutar_la_funcion()
    {
        // El Administrador del Tenant propietario, con su conexión normal (cae_app_runtime): la
        // función solo tiene EXECUTE para cae_app_soporte.
        await using var contexto = CrearContexto(_tenantA, actor: _administradorDeA, tenantOrigen: _tenantA);

        var llamada = () => LlamarFuncionAsync(contexto, Guid.NewGuid(), _delegacion, Motivo);

        (await llamada.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await EscalarAsync<bool>(@"SELECT ""Activa"" FROM ""DelegacionesTenant"" WHERE ""Id"" = @u;", _delegacion))
            .Should().BeTrue();
    }

    [Fact]
    public async Task La_conexion_de_la_sesion_sigue_sin_poder_escribir_la_Delegacion_directamente()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);

        var escritura = () => contexto.Database.ExecuteSqlAsync(
            $@"UPDATE ""DelegacionesTenant"" SET ""Activa"" = false WHERE ""Id"" = {_delegacion}");

        (await escritura.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "la capacidad abre un acto por la función, no un camino de escritura");
        await IntactaAsync(sesion);
    }

    // ── Preparación y lectura ────────────────────────────────────────────────

    private async Task<Guid> AbrirSesionConCapacidadAsync(
        Guid? concedidaPor = null,
        CapacidadPrivilegio capacidad = CapacidadPrivilegio.RevocacionOperadorCaeExterno,
        Guid? tenantObjetivo = null)
    {
        var objetivo = tenantObjetivo ?? _tenantA;
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, capacidad, [objetivo], ahora.AddMinutes(-10), ahora.AddDays(1),
            concedidaPorUsuarioId: concedidaPor ?? _adminPlataforma);
        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            contexto.ConcesionesPrivilegio.Add(concesion);
            await contexto.SaveChangesAsync();
        }

        return await AbrirComoRuntimeAsync(concesion.Id, objetivo);
    }

    private async Task<Guid> AbrirSesionDeSoporteLecturaAsync()
    {
        // Sembrada como propietario: bajo runtime la auto-concesión exige AdminPlataforma vigente
        // (privilegio_autoconcesion), y aquí solo hace falta la sesión de SoporteLectura.
        await using var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion);
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SoporteLecturaGlobal(
            _tecnico, ahora.AddMinutes(-10), ahora.AddDays(30), concedidaPorUsuarioId: _tecnico);
        contexto.ConcesionesPrivilegio.Add(concesion);
        await contexto.SaveChangesAsync();
        return await AbrirComoRuntimeAsync(concesion.Id, _tenantA);
    }

    private async Task<Guid> AbrirComoRuntimeAsync(Guid concesionId, Guid tenantObjetivo)
    {
        await using var contexto = CrearContexto(_tenantPlataforma);
        var handler = new AbrirSesionPrivilegiadaCommandHandler(
            contexto, contexto, new PlataformaWriter(contexto), Usuario(), contexto);

        var resultado = await handler.Handle(
            new AbrirSesionPrivilegiadaCommand(concesionId, tenantObjetivo, "Petición del Tenant propietario", HorasDeVentana: 1),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        return resultado.Valor;
    }

    private async Task<string> RevocarComoSoporteAsync(
        Guid tenantDelContexto, Guid sesion, string motivo = Motivo, Guid? delegacion = null)
    {
        await using var contexto = CrearContexto(tenantDelContexto, sesionPrivilegiadaId: sesion);
        return await LlamarFuncionAsync(contexto, sesion, delegacion ?? _delegacion, motivo);
    }

    // La misma llamada que RevocacionOperadorCaeExternoPorSoporte, para leer el código exacto.
    private static Task<string> LlamarFuncionAsync(CaeManagerDbContext contexto, Guid sesion, Guid delegacion, string motivo) =>
        contexto.Database
            .SqlQuery<string>($"SELECT app_revocar_operador_cae_externo_por_soporte({sesion}, {delegacion}, {motivo}) AS \"Value\"")
            .SingleAsync();

    private async Task IntactaAsync(Guid sesion)
    {
        (await EscalarAsync<bool>(@"SELECT ""Activa"" FROM ""DelegacionesTenant"" WHERE ""Id"" = @u;", _delegacion))
            .Should().BeTrue("la Delegación sigue activa");
        (await EscalarAsync<string>(@"SELECT ""Estado"" FROM ""AsignacionesOperacion"" WHERE ""Id"" = @u;", _operacion))
            .Should().Be("Vigente");
        (await EscalarAsync<string>(@"SELECT ""Estado"" FROM ""AsignacionesCartera"" WHERE ""Id"" = @u;", _cartera))
            .Should().Be("Vigente");
        (await LeerAuditoriaDeSesionAsync(sesion)).Should().BeEmpty("sin escritura no hay fila de auditoría");
    }

    private Task<string> FilaAsync(string tabla, Guid id) =>
        EscalarAsync<string>($"SELECT row_to_json(t)::text FROM \"{tabla}\" t WHERE t.\"Id\" = @u;", id);

    private Task<string> InstantaneaAsync() => EscalarAsync<string>(@"
SELECT concat_ws('|',
 (SELECT coalesce(json_agg(row_to_json(t) ORDER BY t.""Id"")::text, '[]') FROM ""DelegacionesTenant"" t),
 (SELECT coalesce(json_agg(row_to_json(t) ORDER BY t.""Id"")::text, '[]') FROM ""AsignacionesOperacion"" t),
 (SELECT coalesce(json_agg(row_to_json(t) ORDER BY t.""Id"")::text, '[]') FROM ""AsignacionesCartera"" t),
 (SELECT coalesce(json_agg(row_to_json(t) ORDER BY t.""Id"")::text, '[]') FROM ""RegistrosAuditoria"" t),
 (SELECT coalesce(json_agg(row_to_json(t) ORDER BY t.""Id"")::text, '[]') FROM ""SesionesPrivilegiadas"" t),
 (SELECT coalesce(json_agg(row_to_json(t) ORDER BY t.""Id"")::text, '[]') FROM ""ConcesionesPrivilegio"" t),
 (SELECT coalesce(json_agg(row_to_json(t) ORDER BY t.""Id"")::text, '[]') FROM ""TenantsAlcanzadosPorConcesion"" t));", Guid.Empty);

    private async Task<T> EscalarAsync<T>(string sql, Guid parametro)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("u", parametro);
        return (T)(await comando.ExecuteScalarAsync())!;
    }

    private async Task EjecutarAsync(string sql, Guid parametro)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("u", parametro);
        (await comando.ExecuteNonQueryAsync()).Should().Be(1, "la preparación tiene que tocar la sesión");
    }

    private sealed record FilaAuditoria(
        Guid TenantId, string EntidadTipo, Guid EntidadId, string Accion, string? DatosDespues,
        Guid? UsuarioId, Guid? ActorRealUsuarioId, string? TipoActor);

    private async Task<List<FilaAuditoria>> LeerAuditoriaDeSesionAsync(Guid sesion)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT ""TenantId"", ""EntidadTipo"", ""EntidadId"", ""Accion"", ""DatosDespues"",
       ""UsuarioId"", ""ActorRealUsuarioId"", ""TipoActor""
FROM ""RegistrosAuditoria""
WHERE ""ViaAcceso"" = 'SesionPrivilegiada' AND ""ViaAccesoId"" = @u;";
        comando.Parameters.AddWithValue("u", sesion);
        await using var lector = await comando.ExecuteReaderAsync();
        var filas = new List<FilaAuditoria>();
        while (await lector.ReadAsync())
            filas.Add(new FilaAuditoria(
                lector.GetGuid(0), lector.GetString(1), lector.GetGuid(2), lector.GetString(3),
                lector.IsDBNull(4) ? null : lector.GetString(4),
                lector.IsDBNull(5) ? null : lector.GetGuid(5), lector.IsDBNull(6) ? null : lector.GetGuid(6),
                lector.IsDBNull(7) ? null : lector.GetString(7)));
        return filas;
    }

    private static void SembrarCuenta(CaeManagerDbContext contexto, string nombre, Guid tenant, Guid id)
    {
        var correo = $"{nombre}@revocar-soporte.test";
        contexto.Users.Add(new ApplicationUser
        {
            Id = id,
            UserName = correo,
            NormalizedUserName = correo.ToUpperInvariant(),
            Email = correo,
            NormalizedEmail = correo.ToUpperInvariant(),
            NombreCompleto = nombre,
            TenantId = tenant,
            TwoFactorEnabled = true,
            SecurityStamp = Guid.NewGuid().ToString("N").ToUpperInvariant(),
        });
    }

    private CurrentUserServiceFalso Usuario(Guid? actor = null, Guid? tenantOrigen = null) =>
        new(actor ?? _tecnico, rol: null, tenantOrigenId: tenantOrigen ?? _tenantPlataforma, tieneDobleFactorActivo: true);

    /// <summary>
    /// Por defecto conecta como <c>cae_app_runtime</c>: la identidad de producción. Solo la
    /// siembra pasa la cadena del propietario.
    /// </summary>
    private CaeManagerDbContext CrearContexto(
        Guid tenantId, Guid? sesionPrivilegiadaId = null, string? cadena = null, Guid? actor = null, Guid? tenantOrigen = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var seleccion = new ClienteActivoSeleccionadoFalso(
            sesionPrivilegiadaId is null ? null : tenantId, sesionPrivilegiadaId);
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                cadena ?? BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual, seleccion, Usuario(actor, tenantOrigen), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private static Tenant CrearTenant(Guid id, string nombre, bool esPlataforma = false)
    {
        var tenant = new Tenant(nombre);
        typeof(Domain.Common.Entity).GetProperty(nameof(Domain.Common.Entity.Id))!.SetValue(tenant, id);
        if (esPlataforma) tenant.MarcarComoPlataforma();
        return tenant;
    }

    private sealed class ClienteActivoSeleccionadoFalso(Guid? tenantId, Guid? sesionPrivilegiadaId)
        : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantId;

        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }
}
