using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Inbound.Comercial.Commands.ContratarLineaInbound;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Configurations;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Infrastructure.Plataforma;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Inbound.Comercial;

/// <summary>
/// B-04a: la contratación de la línea Inbound (<see cref="ContratarLineaInboundCommand"/>, decisiones D-B y D-C del
/// 2026-11-15) contra PostgreSQL real, componiendo el flujo como <c>EncargoAdministracionPorAprovisionamientoTests</c>:
/// <see cref="IMediator"/> real, <see cref="ISesionPrivilegiadaActual"/> real, la elevación real, el
/// <see cref="TenantRlsConnectionInterceptor"/> real y, para el guardado, los interceptores de producción en su orden con
/// el <see cref="ActorAuditoriaDesdeSesion"/> de producción.
///
/// <para>
/// Lo que solo esta composición prueba: que el handler escribe como <c>cae_app_aprovisionamiento</c> con el único
/// privilegio de escritura que la migración le concede para el alta (INSERT; de lectura, solo las columnas del WHERE de
/// la baja), que el INSERT de EF no necesita leer, que el Tenant de la
/// fila es el de la Sesión Privilegiada, y que el índice único parcial impone un Producto Contratado de la línea Inbound
/// vigente por Tenant. Y la otra mitad, con SQL directo bajo cada rol: el runtime del propio Tenant no puede concederse la
/// línea, y el rol elevado no escribe fuera del Tenant de su contexto firmado ni lee ni cambia lo que inserta.
/// </para>
/// </summary>
public class ContratarLineaInboundPorAprovisionamientoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _actorPlataforma = Guid.NewGuid();
    private readonly Tenant _plataforma = new("Organización de plataforma de prueba", esPlataforma: true);
    private readonly Tenant _tce = new("Tenant TCE de prueba");
    private readonly Tenant _otro = new("Otro Tenant de prueba");

    private Guid _sesionDeAprovisionamiento;
    private Guid _sesionDeLectura;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);
        await using var contexto = CrearContexto(_tce.Id, new Seleccion(_tce.Id, null));

        var ahora = DateTime.UtcNow;
        contexto.Tenants.AddRange(_plataforma, _tce, _otro);

        var aprovisionamiento = ConcesionPrivilegio.SobreTenants(
            _actorPlataforma, CapacidadPrivilegio.Aprovisionamiento, [_tce.Id],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));
        var lectura = ConcesionPrivilegio.SobreTenants(
            _actorPlataforma, CapacidadPrivilegio.SoporteLectura, [_tce.Id],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));
        contexto.ConcesionesPrivilegio.AddRange(aprovisionamiento, lectura);

        var sesionDeAprovisionamiento = SesionPrivilegiada.Abrir(
            aprovisionamiento, _tce.Id, "Alta de la línea Inbound", ahora,
            ventana: TimeSpan.FromHours(1), usuarioSimuladoId: null, ticket: null);
        var sesionDeLectura = SesionPrivilegiada.Abrir(
            lectura, _tce.Id, "Consulta de soporte", ahora,
            ventana: TimeSpan.FromHours(1), usuarioSimuladoId: null, ticket: null);
        contexto.SesionesPrivilegiadas.AddRange(sesionDeAprovisionamiento, sesionDeLectura);

        await contexto.SaveChangesAsync();
        _sesionDeAprovisionamiento = sesionDeAprovisionamiento.Id;
        _sesionDeLectura = sesionDeLectura.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Una_sesion_de_Aprovisionamiento_contrata_la_linea_Inbound_una_sola_vez_por_el_pipeline_completo()
    {
        // Sin este control el test no distinguiría la elevación: si el rol de lectura de la sesión pudiera insertar, la
        // contratación saldría bien aunque el comando no llegara a elevarse.
        (await TienePrivilegioAsync("cae_app_soporte", "INSERT")).Should().BeFalse("control: la sesión lee como cae_app_soporte");
        (await TienePrivilegioAsync("cae_app_aprovisionamiento", "SELECT")).Should().BeFalse(
            "control: el rol elevado no lee la tabla entera (solo las columnas del WHERE de la baja), así que el alta no " +
            "puede depender de una lectura previa de lo contratado");

        var primera = await EnviarAsync(_sesionDeAprovisionamiento, rol: null,
            new ContratarLineaInboundCommand("cus_PagadorTce", TramoTrabajadoresActivos.De41A80, ModalidadSuscripcionAlta.Anual));
        primera.EsExitoso.Should().BeTrue(primera.EsFallido ? $"código: {primera.Error.Codigo}" : string.Empty);

        var segunda = await EnviarAsync(_sesionDeAprovisionamiento, rol: null,
            new ContratarLineaInboundCommand("cus_OtroPagador", TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Mensual));
        segunda.Error.Should().Be(ErroresContratarLineaInbound.YaContratada, "un solo Producto Contratado Inbound vigente por Tenant (D-B)");

        var filas = await FilasComoPropietarioAsync();
        var fila = filas.Should().ContainSingle().Subject;
        fila.Id.Should().Be(primera.Valor);
        fila.TenantId.Should().Be(_tce.Id, "el Tenant beneficiario es el objetivo de la Sesión Privilegiada");
        fila.Linea.Should().Be(LineaProductoContratado.Inbound);
        fila.PagadorStripeCustomerId.Should().Be("cus_PagadorTce");
        fila.Tramo.Should().Be(TramoTrabajadoresActivos.De41A80);
        fila.Modalidad.Should().Be(ModalidadSuscripcionAlta.Anual);
        fila.Estado.Should().Be(EstadoProductoContratado.Vigente);

        // El guardado pasó por AuditoriaInterceptor con el actor de producción, con la conexión elevada: la fila de
        // auditoría atribuye el alta al Actor de Plataforma por la vía de su Sesión Privilegiada, y el intento rechazado
        // no dejó la suya.
        var auditoria = (await AuditoriaComoPropietarioAsync(primera.Valor)).Should().ContainSingle().Subject;
        auditoria.TenantId.Should().Be(_tce.Id);
        auditoria.UsuarioId.Should().Be(_actorPlataforma);
        auditoria.ActorRealUsuarioId.Should().Be(_actorPlataforma);
        auditoria.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.SesionPrivilegiada);
        auditoria.ViaAccesoId.Should().Be(_sesionDeAprovisionamiento);
    }

    [Fact]
    public async Task Dos_contrataciones_simultaneas_dejan_una_sola_linea_Inbound_vigente()
    {
        // Solape determinista, con el patrón de RegistroAltaBajoRuntimeTests y sin depender de tiempos: el propietario
        // ocupa la plaza del índice único parcial con una fila sin confirmar, las dos contrataciones quedan esperando ese
        // bloqueo en su INSERT, y al deshacerla se sueltan a la vez y compiten por la plaza.
        await using var retenedor = new NpgsqlConnection(_cadenaConexion);
        await retenedor.OpenAsync();
        await using var transaccion = await retenedor.BeginTransactionAsync();
        await using (var plaza = new NpgsqlCommand(InsertInbound(_tce.Id), retenedor, transaccion))
            await plaza.ExecuteNonQueryAsync();

        var comando = new ContratarLineaInboundCommand("cus_PagadorTce", TramoTrabajadoresActivos.De41A80, ModalidadSuscripcionAlta.Anual);
        var tareas = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(() => EnviarAsync(_sesionDeAprovisionamiento, rol: null, comando)))
            .ToArray();

        await using (var observador = new NpgsqlConnection(_cadenaConexion))
        {
            await observador.OpenAsync();
            await using var esperando = new NpgsqlCommand(
                """
                SELECT count(*) FROM pg_stat_activity
                WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%ProductosContratados%'
                """,
                observador);
            var limite = DateTime.UtcNow.AddSeconds(30);
            while ((long)(await esperando.ExecuteScalarAsync())! < 2)
            {
                // Si una terminó antes de bloquearse, el fallo dice con qué, en vez de agotar la espera.
                if (tareas.FirstOrDefault(t => t.IsCompleted) is { } terminada)
                {
                    var resultado = await terminada;
                    resultado.EsExitoso.Should().BeFalse("ninguna contratación entra mientras la plaza está ocupada");
                    throw new Xunit.Sdk.XunitException(
                        $"una contratación terminó sin esperar el bloqueo de la plaza: {resultado.Error.Codigo}");
                }

                DateTime.UtcNow.Should().BeBefore(limite, "las dos contrataciones deben quedar esperando la plaza del índice");
                await Task.Delay(25);
            }
        }

        await transaccion.RollbackAsync();
        var resultados = await Task.WhenAll(tareas);

        var ganadora = resultados.Should().ContainSingle(r => r.EsExitoso, "el índice deja pasar una").Subject;
        resultados.Single(r => !r.EsExitoso).Error.Should().Be(ErroresContratarLineaInbound.YaContratada);
        (await FilasComoPropietarioAsync()).Should().ContainSingle().Which.Id.Should().Be(ganadora.Valor);
        (await AuditoriaComoPropietarioAsync(ganadora.Valor)).Should().ContainSingle();
    }

    [Fact]
    public async Task Un_duplicado_rechazado_retira_el_intento_por_referencia_y_su_auditoria()
    {
        // Hallazgo 3 de Codex (ronda 1): antes, el repositorio vaciaba el ChangeTracker entero. Aquí lo ya rastreado sin
        // cambios (el Tenant leído) se queda, y lo que se retira es el intento y el registro que el interceptor le generó.
        await EjecutarComoPropietarioAsync(InsertInbound(_tce.Id));

        await using var contexto = CrearContextoPropietarioAuditado();
        var otro = await contexto.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == _otro.Id);

        var repositorio = new ProductoContratadoRepository(contexto, new ActorFijo(_actorPlataforma));
        var intento = ContratoInbound("cus_Duplicado");
        repositorio.Agregar(intento);

        (await repositorio.GuardarDetectandoInboundVigenteDuplicadoAsync(intento)).Should().BeFalse("ya hay una vigente");

        contexto.Entry(intento).State.Should().Be(EntityState.Detached, "la fila rechazada no se cuela después");
        contexto.ChangeTracker.Entries<RegistroAuditoria>().Should().BeEmpty("la auditoría del intento fallido tampoco");
        contexto.Entry(otro).State.Should().Be(EntityState.Unchanged, "lo rastreado antes del guardado no se toca");

        (await contexto.SaveChangesAsync()).Should().Be(0, "no queda nada del intento por escribir");
        (await AuditoriaComoPropietarioAsync(intento.Id)).Should().BeEmpty();
        (await FilasComoPropietarioAsync()).Should().ContainSingle("solo la vigente sembrada");
    }

    [Fact]
    public async Task Con_otro_Producto_Contratado_pendiente_no_guarda_nada_y_retira_solo_el_intento()
    {
        // Hallazgo 2 de Codex (ronda 2): antes, ante el duplicado se retiraban todos los Productos Contratados añadidos.
        await using var contexto = CrearContextoPropietarioAuditado();
        var repositorio = new ProductoContratadoRepository(contexto, new ActorFijo(_actorPlataforma));
        var previo = ContratoInbound("cus_Previo");
        repositorio.Agregar(previo);
        var intento = ContratoInbound("cus_Intento");
        repositorio.Agregar(intento);

        var guardar = () => repositorio.GuardarDetectandoInboundVigenteDuplicadoAsync(intento);

        await guardar.Should().ThrowAsync<InvalidOperationException>("el intento se guarda solo");
        contexto.Entry(intento).State.Should().Be(EntityState.Detached, "el intento no se cuela en otro guardado");
        contexto.Entry(previo).State.Should().Be(EntityState.Added, "el pendiente previo es de otro acto y se queda");
        contexto.ChangeTracker.Entries<RegistroAuditoria>().Should().BeEmpty("no se llegó a guardar: nada se auditó");
        (await FilasComoPropietarioAsync()).Should().BeEmpty("no se escribió nada");
    }

    [Fact]
    public async Task Con_cambios_pendientes_ajenos_no_guarda_nada_ni_consume_su_auditoria()
    {
        // Hallazgo 3 de Codex (ronda 2): con un cambio ajeno pendiente, el guardado bajo el rol elevado lo arrastraría, y
        // AuditoriaInterceptor consumiría su acción propia aunque la base rechazara el intento. El repositorio no llega a
        // SaveChanges: la prueba de que el interceptor no corrió es que no añadió ningún registro, ni del intento ni del
        // Tenant cambiado.
        await using var contexto = CrearContextoPropietarioAuditado();
        var otro = await contexto.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == _otro.Id);
        var addOnEsperado = !otro.AddOnSubidaAutomaticaActivo;
        otro.CambiarAddOnSubidaAutomatica(addOnEsperado);
        var auditoriaAjena = new RegistroAuditoria(
            "PruebaAjena", Guid.NewGuid(), "Creado", null, null,
            usuarioId: _actorPlataforma, tipoActor: TipoActorAuditoria.Persona, actorRealUsuarioId: _actorPlataforma,
            viaAcceso: TipoViaAccesoAuditoria.Normal, viaAccesoId: null);
        contexto.RegistrosAuditoria.Add(auditoriaAjena);

        var repositorio = new ProductoContratadoRepository(contexto, new ActorFijo(_actorPlataforma));
        var intento = ContratoInbound("cus_Intento");
        repositorio.Agregar(intento);

        var guardar = () => repositorio.GuardarDetectandoInboundVigenteDuplicadoAsync(intento);

        await guardar.Should().ThrowAsync<InvalidOperationException>("el intento se guarda solo");
        contexto.Entry(intento).State.Should().Be(EntityState.Detached);
        contexto.Entry(otro).State.Should().Be(EntityState.Modified, "el cambio pendiente de otro acto sigue rastreado");
        contexto.ChangeTracker.Entries<RegistroAuditoria>().Should().ContainSingle(
                "el interceptor no corrió: solo está el registro ajeno de antes")
            .Which.Entity.Should().BeSameAs(auditoriaAjena);

        // El acto ajeno se guarda después con su auditoría, una sola vez.
        await contexto.SaveChangesAsync();
        (await AuditoriaComoPropietarioAsync(auditoriaAjena.EntidadId)).Should().ContainSingle();
        (await AuditoriaComoPropietarioAsync(_otro.Id)).Should().ContainSingle();
        await using var lectura = CrearContextoPropietarioAuditado();
        (await lectura.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == _otro.Id))
            .AddOnSubidaAutomaticaActivo.Should().Be(addOnEsperado);
        (await FilasComoPropietarioAsync()).Should().BeEmpty("el intento no se escribió");
    }

    [Fact]
    public async Task Con_una_entidad_ajena_con_accion_de_auditoria_propia_marcada_no_la_consume()
    {
        // Issue #2254 (deuda de la ronda 3 de Codex en la #2252): el caso anterior usa un Tenant, que no marca acción
        // propia. Aquí el cambio ajeno es una renovación de un Contrato de acceso, que marca «Renovado» para que
        // AuditoriaInterceptor la registre así y no como «Modificado». Si el repositorio llegara a SaveChanges, el
        // interceptor consumiría la marca aunque la base rechazara el intento, y al guardar después el acto ajeno quedaría
        // como «Modificado». No se comprueba la acción persistida: haría falta sembrar el Centro y la Empresa contratista
        // del contrato; basta con que la marca siga sin consumir, porque es lo único que el interceptor lee.
        await using var contexto = CrearContextoPropietarioAuditado();
        var contrato = ContratoAcceso.Crear(
            clienteEmpresarialId: Guid.NewGuid(), centroId: Guid.NewGuid(), empresaContratistaId: Guid.NewGuid(),
            fechaInicio: new DateOnly(2026, 1, 1), fechaFin: new DateOnly(2026, 12, 31), notas: null);
        contexto.Attach(contrato);
        contrato.Renovar(new DateOnly(2027, 12, 31), _actorPlataforma, DateTime.UtcNow);
        contexto.ChangeTracker.DetectChanges();
        contexto.Entry(contrato).State.Should().Be(EntityState.Modified, "control: el cambio ajeno está pendiente");

        var repositorio = new ProductoContratadoRepository(contexto, new ActorFijo(_actorPlataforma));
        var intento = ContratoInbound("cus_Intento");
        repositorio.Agregar(intento);

        var guardar = () => repositorio.GuardarDetectandoInboundVigenteDuplicadoAsync(intento);

        await guardar.Should().ThrowAsync<InvalidOperationException>("el intento se guarda solo");
        contexto.Entry(intento).State.Should().Be(EntityState.Detached);
        contexto.Entry(contrato).State.Should().Be(EntityState.Modified, "la renovación sigue pendiente, sin escribir");
        contexto.ChangeTracker.Entries<RegistroAuditoria>().Should().BeEmpty("el interceptor no corrió");
        ((IAccionAuditoriaPropia)contrato).ConsumirAccionAuditoria().Should().Be(RegistroAuditoria.AccionRenovado,
            "la marca de la renovación no se consumió: el acto ajeno se auditará como «Renovado»");
        (await FilasComoPropietarioAsync()).Should().BeEmpty("el intento no se escribió");
    }

    private static ProductoContratado ContratoInbound(string pagador)
    {
        var ahora = DateTime.UtcNow;
        return ProductoContratado.Contratar(
            LineaProductoContratado.Inbound, pagador, TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Anual,
            vigenteDesdeUtc: ahora, ahoraUtc: ahora);
    }

    [Fact]
    public async Task Una_sesion_de_soporte_de_solo_lectura_no_contrata_la_linea_Inbound()
    {
        var resultado = await EnviarAsync(_sesionDeLectura, rol: null,
            new ContratarLineaInboundCommand("cus_PagadorTce", TramoTrabajadoresActivos.De41A80, ModalidadSuscripcionAlta.Anual));

        // Lo deniega AutorizacionEscrituraBehavior antes del handler: SoporteLectura no tiene camino de escritura.
        resultado.Error.Codigo.Should().Be("Autorizacion.SesionPrivilegiadaSoloLectura",
            "la capacidad de la sesión no es Aprovisionamiento");
        (await FilasComoPropietarioAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Administrador_del_propio_Tenant_sin_Sesion_Privilegiada_no_se_concede_la_linea_Inbound()
    {
        // AutorizacionEscrituraBehavior deja pasar el rol con escritura; el handler deniega, y aunque no lo hiciera,
        // el runtime no tiene INSERT (lo prueba el test de SQL directo de abajo).
        var resultado = await EnviarAsync(sesion: null, rol: "Administrador",
            new ContratarLineaInboundCommand("cus_PagadorTce", TramoTrabajadoresActivos.De41A80, ModalidadSuscripcionAlta.Anual));

        resultado.Error.Should().Be(ErroresContratarLineaInbound.NoAutorizado);
        (await FilasComoPropietarioAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Con_SQL_directo_el_runtime_no_inserta_y_el_rol_elevado_solo_inserta_en_su_Tenant_y_una_vez()
    {
        // El runtime con el contexto firmado del propio Tenant: sin INSERT, aunque el WITH CHECK lo admitiría.
        (await SqlStateComoAsync("cae_app_runtime", _tce.Id, InsertInbound(_tce.Id))).SqlState
            .Should().Be(PostgresErrorCodes.InsufficientPrivilege, "un Tenant no se concede su propia línea Inbound");

        // Control positivo del mismo INSERT con el rol elevado: sin él, los rechazos de abajo pasarían igual con un rol
        // que no pudiera insertar nada.
        (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, InsertInbound(_tce.Id)))
            .Should().Be(((string?)null, 1, (string?)null));

        // Fuera del Tenant del contexto firmado: el WITH CHECK de aislamiento_tenant lo rechaza.
        (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, InsertInbound(_otro.Id))).SqlState
            .Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        // Dos vigentes del mismo Tenant: el índice único parcial. Uno finalizado no cuenta.
        var dosVigentes = await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id,
            InsertInbound(_tce.Id) + InsertInbound(_tce.Id));
        dosVigentes.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        dosVigentes.Restriccion.Should().Be(ProductoContratadoConfiguration.IndiceInboundVigentePorTenant);
        // La finalizada la inserta el superusuario de pruebas (RESET ROLE dentro de la misma transacción), porque el rol
        // elevado ya no puede insertar una fila finalizada.
        (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id,
                "RESET ROLE;" + InsertInbound(_tce.Id, finalizado: true)
                + "SET LOCAL ROLE cae_app_aprovisionamiento;" + InsertInbound(_tce.Id)))
            .SqlState.Should().BeNull("una fila finalizada no ocupa la plaza de la vigente");

        // Hallazgo 2 de Codex (ronda 1): la política RESTRICTIVE de INSERT solo deja al rol elevado la fila que contrata
        // ContratarLineaInboundCommand. El control positivo de arriba usa el mismo INSERT con la línea Inbound vigente.
        foreach (var (sql, motivo) in new[]
                 {
                     (InsertInbound(_tce.Id, linea: "OutboundGestionPropia"), "otra línea del Producto Contratado"),
                     (InsertInbound(_tce.Id, finalizado: true), "una fila ya finalizada"),
                     (InsertInbound(_tce.Id, eliminado: true), "una fila eliminada"),
                     // Hallazgo 4 de Codex (ronda 2): la vigencia empieza al contratar, con 5 minutos de margen.
                     (InsertInbound(_tce.Id, vigenteDesde: "now() - interval '1 day'"), "una vigencia empezada antes"),
                     (InsertInbound(_tce.Id, vigenteDesde: "now() + interval '1 day'"), "una vigencia que empieza después"),
                     // El límite de atrás fija el margen: con uno más ancho, esta entraría.
                     (InsertInbound(_tce.Id, vigenteDesde: "now() - interval '6 minutes'"), "6 minutos antes, fuera del margen"),
                     // Issue #2259: el inicio nunca queda en el futuro, ni dentro del antiguo margen del reloj de la
                     // aplicación. Una fila con el inicio futuro haría que el gate DN-7, que compara con clock_timestamp(),
                     // diera valores distintos dentro de una misma consulta e incumpliera su promesa de STABLE.
                     (InsertInbound(_tce.Id, vigenteDesde: "now() + interval '6 minutes'"), "6 minutos después"),
                     (InsertInbound(_tce.Id, vigenteDesde: "now() + interval '4 minutes'"), "4 minutos después"),
                     (InsertInbound(_tce.Id, vigenteDesde: "now() + interval '1 millisecond'"), "un milisegundo después"),
                 })
            (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, sql)).SqlState
                .Should().Be(PostgresErrorCodes.InsufficientPrivilege, motivo);
        // La política solo restringe al rol elevado: el superusuario de pruebas, que no la sufre, sí inserta la otra línea,
        // así que el rechazo de arriba es de la política y no de la fila.
        (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id,
                "RESET ROLE;" + InsertInbound(_tce.Id, linea: "OutboundGestionPropia")))
            .SqlState.Should().BeNull("control: la fila de otra línea es válida para la tabla");
        // Y el margen hacia atrás sigue entrando, hasta el propio now(): el rechazo de arriba no es de cualquier desfase.
        foreach (var desfase in new[] { "now() - interval '4 minutes'", "now()" })
            (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, InsertInbound(_tce.Id, vigenteDesde: desfase)))
                .SqlState.Should().BeNull($"dentro del margen: {desfase}");

        // Ni lee ni cambia lo contratado: la baja (FinalizaLineaInboundPorAprovisionamiento) solo le concede leer las
        // columnas de su WHERE y actualizar Estado y VigenteHastaUtc, que prueba BajaYCorreccionLineaInboundPorAprovisionamientoTests.
        foreach (var sql in new[]
                 {
                     """SELECT "PagadorStripeCustomerId" FROM "ProductosContratados";""",
                     """SELECT "Tramo", "Modalidad", "VigenteDesdeUtc" FROM "ProductosContratados";""",
                     """UPDATE "ProductosContratados" SET "Tramo" = 'De1A10';""",
                     """DELETE FROM "ProductosContratados";""",
                 })
            (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, sql)).SqlState
                .Should().Be(PostgresErrorCodes.InsufficientPrivilege, sql);
    }

    private static string InsertInbound(
        Guid tenantId, bool finalizado = false, string linea = "Inbound", bool eliminado = false,
        string vigenteDesde = "now()") =>
        $"""
        INSERT INTO "ProductosContratados" ("Id", "Linea", "PagadorStripeCustomerId", "Tramo", "Modalidad",
            "VigenteDesdeUtc", "VigenteHastaUtc", "Estado", "TenantId", "Version", "CreadoEnUtc", "EstaEliminado")
        VALUES ('{Guid.NewGuid()}', '{linea}', 'cus_X', 'De1A10', 'Anual', {vigenteDesde},
            {(finalizado ? "now()" : "NULL")}, '{(finalizado ? "Finalizado" : "Vigente")}', '{tenantId}', '{Guid.NewGuid()}', now(),
            {(eliminado ? "true" : "false")});
        """;

    private async Task<Result<Guid>> EnviarAsync(Guid? sesion, string? rol, ContratarLineaInboundCommand comando)
    {
        await using var proveedor = ConstruirProveedor(sesion, rol);
        return await proveedor.GetRequiredService<IMediator>().Send(comando);
    }

    /// <summary>Las filas como superusuario de pruebas: la comprobación no depende de los privilegios que se prueban.</summary>
    private async Task<List<ProductoContratado>> FilasComoPropietarioAsync()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(_cadenaConexion).Options;
        await using var contexto = new CaeManagerDbContext(
            options, new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = _tce.Id });
        return await contexto.ProductosContratados.IgnoreQueryFilters().ToListAsync();
    }

    /// <summary>
    /// Ejecuta <paramref name="sql"/> con <paramref name="rol"/> y el contexto firmado de <paramref name="tenantContexto"/>,
    /// dentro de una transacción que se deshace. Devuelve el SQLSTATE y la restricción del rechazo o, si entró,
    /// <c>null</c> y las filas afectadas.
    /// </summary>
    private async Task<(string? SqlState, int Filas, string? Restriccion)> SqlStateComoAsync(string rol, Guid tenantContexto, string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();

        var contextoFirmado = await ContextoRlsDePruebas.FirmarAsync(conexion, tenantId: tenantContexto);
        await using (var preparar = conexion.CreateCommand())
        {
            preparar.Transaction = transaccion;
            preparar.CommandText = $"SELECT set_config('app.contexto', @contexto, true); SET LOCAL ROLE {rol};";
            preparar.Parameters.AddWithValue("contexto", contextoFirmado);
            await preparar.ExecuteNonQueryAsync();
        }

        await using (var comprobarRol = conexion.CreateCommand())
        {
            comprobarRol.Transaction = transaccion;
            comprobarRol.CommandText = "SELECT current_user::text;";
            ((string)(await comprobarRol.ExecuteScalarAsync())!).Should().Be(rol);
        }

        try
        {
            await using var comando = conexion.CreateCommand();
            comando.Transaction = transaccion;
            comando.CommandText = sql;
            return (null, await comando.ExecuteNonQueryAsync(), null);
        }
        catch (PostgresException ex)
        {
            return (ex.SqlState, -1, ex.ConstraintName);
        }
        finally
        {
            await transaccion.RollbackAsync();
        }
    }

    private async Task<bool> TienePrivilegioAsync(string rol, string privilegio)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT has_table_privilege(@rol, 'public.\"ProductosContratados\"', @privilegio);";
        comando.Parameters.AddWithValue("rol", rol);
        comando.Parameters.AddWithValue("privilegio", privilegio);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private ServiceProvider ConstruirProveedor(Guid? sesionSeleccionada, string? rol)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tce.Id };
        // Con Sesión Privilegiada, el Actor de Plataforma es de la organización de plataforma; sin ella, el usuario es del
        // propio Tenant TCE.
        var currentUser = sesionSeleccionada is null
            ? new CurrentUserServiceFalso(_actorPlataforma, rol: rol, tenantOrigenId: _tce.Id)
            : new CurrentUserServiceFalso(_actorPlataforma, rol: rol, tenantOrigenId: _plataforma.Id);
        var seleccion = new Seleccion(_tce.Id, sesionSeleccionada);
        // El actor de auditoría de producción (hallazgo 4 de Codex, ronda 1): sin consultar la base, lee la Sesión
        // Privilegiada de la selección, así que la atribución sale igual con la conexión elevada.
        var actorAuditoria = new ActorAuditoriaDesdeSesion(currentUser, seleccion, currentUser);
        var dbContext = CrearContexto(_tce.Id, seleccion, currentUser, actorAuditoria);

        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddApplication();

        servicios.AddSingleton<ICurrentUserService>(currentUser);
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddSingleton<IClienteActivoSeleccionado>(seleccion);
        servicios.AddSingleton(dbContext);
        servicios.AddSingleton(TimeProvider.System);

        servicios.AddSingleton<IPlataformaQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ISesionPrivilegiadaActual>(sp => new SesionPrivilegiadaActual(
            sp.GetRequiredService<IPlataformaQueryContext>(), seleccion, currentUser));
        servicios.AddSingleton<IElevacionEscrituraPrivilegiada>(sp =>
            new ElevacionEscrituraPrivilegiada(sp.GetRequiredService<CaeManagerDbContext>()));

        servicios.AddSingleton<IUnitOfWork>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        // GateComercialTenantBehavior lee el Tenant por aquí; el resto, como en EncargoAdministracionPorAprovisionamientoTests.
        servicios.AddSingleton<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IEncargosAdministracionQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IProductoContratadoRepository, ProductoContratadoRepository>();
        servicios.AddSingleton<ITransaccionDeComando>(sp => new TransaccionDeComando(sp.GetRequiredService<CaeManagerDbContext>()));
        servicios.AddSingleton<IActorAuditoria>(actorAuditoria);

        return servicios.BuildServiceProvider();
    }

    /// <summary>
    /// Con <paramref name="actorAuditoria"/>, los interceptores de guardado de producción en su orden
    /// (<c>ConfiguracionDeContexto</c>): auditoría, contexto RLS, sellado de Tenant y concurrencia optimista. Sin él, la
    /// siembra, que no se audita.
    /// </summary>
    private CaeManagerDbContext CrearContexto(
        Guid tenantId, IClienteActivoSeleccionado seleccion, ICurrentUserService? currentUserService = null,
        IActorAuditoria? actorAuditoria = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var rls = new TenantRlsConnectionInterceptor(
            tenantActual, seleccion,
            currentUserService ?? new CurrentUserServiceFalso(_actorPlataforma, rol: null, tenantOrigenId: tenantId),
            BaseDatosPostgresDePruebas.FirmanteContextoRls);
        var constructor = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        if (actorAuditoria is null)
            constructor.AddInterceptors(new TenantSelladoInterceptor(tenantActual), rls);
        else
            constructor.AddInterceptors(
                new AuditoriaInterceptor(actorAuditoria), rls, new TenantSelladoInterceptor(tenantActual),
                new ConcurrenciaOptimistaInterceptor());

        return new CaeManagerDbContext(constructor.Options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>
    /// Un contexto como superusuario de pruebas, sin contexto RLS, con la auditoría y el sellado: para probar el
    /// repositorio sin los privilegios del rol elevado, que no deja guardar cambios ajenos a ProductosContratados.
    /// </summary>
    private CaeManagerDbContext CrearContextoPropietarioAuditado()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tce.Id };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion)
            .AddInterceptors(new AuditoriaInterceptor(new ActorFijo(_actorPlataforma)), new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private async Task EjecutarComoPropietarioAsync(string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<List<RegistroAuditoria>> AuditoriaComoPropietarioAsync(Guid entidadId)
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(_cadenaConexion).Options;
        await using var contexto = new CaeManagerDbContext(
            options, new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = _tce.Id });
        return await contexto.RegistrosAuditoria.IgnoreQueryFilters().Where(r => r.EntidadId == entidadId).ToListAsync();
    }

    private sealed class Seleccion(Guid tenantSeleccionado, Guid? sesionPrivilegiadaId) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantSeleccionado;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }

    private sealed class ActorFijo(Guid actorReal) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(actorReal));
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(actorReal);
    }
}
