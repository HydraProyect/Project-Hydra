using CaeManager.Application.Comunicaciones.Matching;
using CaeManager.Application.Plataforma;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Operaciones;
using CaeManager.IntegrationTests.Arranque;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Regresión del hallazgo N-2 de Project-Hydra-Negocio/seguridad/INFORME-AUDITORIA-2.md. La cola de triaje
/// (conversaciones con ClienteId null) quedaba visible para cualquier usuario
/// autenticado del tenant, rol <c>Cliente</c> incluido: un contacto de una
/// empresa cliente externa podía leer el correo entrante sin triar del resto
/// de clientes escribiendo /comunicaciones en la barra de direcciones.
///
/// La página lleva ahora <c>[Authorize(Roles = RolesComunicaciones.Gestion)]</c>,
/// pero eso cierra la puerta, no el dato: esto cubre la query, que es lo que
/// protege a cualquier UI o API futura.
/// </summary>
public class VisibilidadTriageTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _clientePropio;
    private Guid _conversacionSinCliente;
    private AsignacionOperacion? _operacionAcotada;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Contrata Propia S.L.", "B12345674", false, null, null);
        contexto.Empresas.Add(cliente);

        var propia = new Conversacion("Mi propia consulta", clienteId: cliente.Id);
        propia.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "yo@propia.test", "<p>Hola</p>");

        var sinTriar = new Conversacion("Correo de otra empresa sin triar");
        sinTriar.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "otro@ajena.test", "<p>Datos de otra empresa</p>");

        contexto.Conversaciones.AddRange(propia, sinTriar);
        await contexto.SaveChangesAsync();

        _clientePropio = cliente.Id;
        _conversacionSinCliente = sinTriar.Id;
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Con_el_triaje_cerrado_la_lista_no_muestra_la_cola_ni_con_la_cartera_total()
    {
        // La lista no mira el rol: obedece solo a TriajeVisibleAsync. Que el rol Cliente lo tenga cerrado lo fija
        // TriajeVisibleTests (Web.Tests) contra el servicio real, con el rol como única variable. Aquí, con la cartera
        // sin restricción, el «no ve» solo puede venir del triaje cerrado; con la cartera de lista de antes, lo podía
        // explicar la cartera.
        var conversaciones = await ListarComo("Cliente", AlcanceDelRolClienteConTodoLoDemasAbierto());

        conversaciones.Should().ContainSingle("con la cartera total ve su hilo anclado, pero no la cola de triaje")
            .Which.ClienteId.Should().Be(_clientePropio);
    }

    [Fact]
    public async Task Con_el_triaje_cerrado_el_detalle_no_abre_un_hilo_sin_triar_aunque_se_tenga_el_Guid_ni_con_la_cartera_total()
    {
        // Acotar solo el listado dejaba el hilo accesible por Id. El detalle tampoco mira el rol (ver el test de arriba).
        await using var contexto = CrearContexto();
        var handler = new ObtenerConversacionPorIdQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto, contexto, AlcanceDelRolClienteConTodoLoDemasAbierto(),
            new GanssSanitizadorHtmlService(),
            new MotorCoincidenciaConversacionesService(new ConversacionRepository(contexto)), PoliticaNotaInternaPruebas.Con("GestorCae"));

        var detalle = await handler.Handle(
            new ObtenerConversacionPorIdQuery(_conversacionSinCliente), CancellationToken.None);

        detalle.Should().BeNull();
    }

    /// <summary>
    /// Cartera de Clientes y alcance de Empresas sin restricción (con ellos, el doble daría acceso total y el Tenant
    /// entero), y solo el triaje cerrado, que es lo que <c>AlcanceDatosService.TriajeVisibleAsync</c> contesta, por
    /// ejemplo, al rol Cliente antes de mirar el ámbito. El doble no conoce el rol: estos tests fijan que la consulta
    /// obedece al triaje cerrado, no la decisión por rol, que fija <c>TriajeVisibleTests</c>.
    /// </summary>
    private static AlcanceDatosServiceFalso AlcanceDelRolClienteConTodoLoDemasAbierto() =>
        new(triajeVisible: false);

    [Fact]
    public async Task Un_gestor_no_abre_por_Id_un_hilo_del_buzon_personal_de_otro_gestor()
    {
        // Mismo hallazgo que el resto de este archivo, pero para el buzón
        // personal de un gestor (ConexionIntegracion.GestorPropietarioId) en
        // vez de la cola de triaje: acotar solo el listado (ObtenerConversacionesQuery,
        // ya cubierto arriba) dejaría el hilo accesible por Id igual que pasaba
        // con el triaje antes de este mismo archivo.
        Guid conversacionPersonalId;
        Guid conexionPersonalId;
        await using (var contexto = CrearContexto())
        {
            var conexionPersonal = new ConexionIntegracion(
                "gestor.otro@ejemplo.local", "Buzón personal", gestorPropietarioId: Guid.NewGuid());
            contexto.ConexionesIntegracion.Add(conexionPersonal);
            await contexto.SaveChangesAsync();
            conexionPersonalId = conexionPersonal.Id;

            var deBuzonPersonal = new Conversacion("Asunto personal ajeno");
            deBuzonPersonal.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "quien-sea@externo.test", "<p>Privado</p>");
            deBuzonPersonal.AsociarConexion(conexionPersonal.Id, "hilo-personal-vt-1");
            contexto.Conversaciones.Add(deBuzonPersonal);
            await contexto.SaveChangesAsync();
            conversacionPersonalId = deBuzonPersonal.Id;
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerConversacionPorIdQueryHandler(
            lectura, lectura, lectura, lectura, lectura, lectura, lectura, lectura,
            new AlcanceDatosServiceFalso(clienteIds: [_clientePropio], conexionesIntegracionAjenas: [conexionPersonalId]),
            new GanssSanitizadorHtmlService(),
            new MotorCoincidenciaConversacionesService(new ConversacionRepository(lectura)), PoliticaNotaInternaPruebas.Con("GestorCae"));

        var detalle = await handler.Handle(new ObtenerConversacionPorIdQuery(conversacionPersonalId), CancellationToken.None);

        detalle.Should().BeNull();
    }

    [Fact]
    public async Task Un_gestor_con_ambito_acotado_no_ve_la_cola_de_triage_ni_en_la_lista_ni_en_el_detalle()
    {
        // D-6 b, A-16: el triaje no tiene cartera que lo acote, así que solo lo ve quien alcanza el Tenant entero
        // (IAlcanceDatosService.TriajeVisibleAsync). Un Gestor CAE bajo una Asignación de Operación acotada ve lo de
        // su Cliente y nada de la cola, y la lista y el detalle deciden igual.
        var conversaciones = await ListarComo("GestorCae");

        conversaciones.Should().ContainSingle().Which.ClienteId.Should().Be(_clientePropio);
        (await DetalleSinClienteComo(triajeVisible: false)).Should().BeNull();
    }

    [Fact]
    public async Task Un_gestor_que_alcanza_el_Tenant_entero_si_ve_la_cola_de_triage_en_la_lista_y_en_el_detalle()
    {
        // Contrapeso imprescindible: si el triaje deja de verse para los roles
        // de gestión con el Tenant entero, nadie puede triar y el módulo se queda
        // sin su función principal (§ 12.4).
        var conversaciones = await ListarComo("GestorCae", triajeVisible: true);

        conversaciones.Should().HaveCount(2);
        conversaciones.Should().Contain(c => c.ClienteId == null);
        (await DetalleSinClienteComo(triajeVisible: true)).Should().NotBeNull();
    }

    [Fact]
    public async Task El_Gestor_CAE_propietario_sin_el_Tenant_entero_ve_su_hilo_sin_ancla_en_la_lista_y_en_el_detalle_y_otro_Gestor_CAE_no()
    {
        // DECISION_LOG 2026-11-14, decisión 2: el correo de un buzón personal que no se deja como registro «solo vive en la
        // cuenta del Gestor CAE propietario» (condición 2 de VisibilidadConversacion): lo ve su propietario sin el Tenant
        // entero, con un ámbito efectivo que concede algo, y nadie más por esta vía. Los dos Gestores CAE tienen la misma
        // cartera acotada y ninguno pasa el triaje.
        var propietario = Guid.NewGuid();
        var otroGestor = Guid.NewGuid();
        var (conexion, hilo) = await CrearHiloSinAnclaAsync(
            new ConexionIntegracion("gestora@ejemplo.local", "Buzón personal", gestorPropietarioId: propietario), "hilo-propio-vt-1");

        var delPropietario = new AlcanceDatosServiceFalso(
            clienteIds: [_clientePropio], triajeVisible: false, buzonesPersonalesPropios: [conexion]);
        (await ListarComo("GestorCae", delPropietario, propietario)).Select(c => c.Id)
            .Should().Contain(hilo).And.NotContain(_conversacionSinCliente, "la cola de triaje sigue cerrada");
        (await DetalleComo(hilo, delPropietario)).Should().NotBeNull();

        var delOtro = new AlcanceDatosServiceFalso(
            clienteIds: [_clientePropio], triajeVisible: false, conexionesIntegracionAjenas: [conexion]);
        (await ListarComo("GestorCae", delOtro, otroGestor)).Select(c => c.Id).Should().NotContain(hilo);
        (await DetalleComo(hilo, delOtro)).Should().BeNull();
    }

    [Fact]
    public async Task El_Administrador_ve_los_hilos_sin_ancla_de_un_buzon_compartido_y_no_los_de_un_buzon_personal_ajeno()
    {
        var administrador = Guid.NewGuid();
        var (_, delCompartido) = await CrearHiloSinAnclaAsync(
            ConexionIntegracion.CrearBuzonCompartido("soporte@ejemplo.local", "Soporte", Guid.NewGuid().ToString(), administrador),
            "hilo-compartido-vt-1");
        var (personalAjeno, delPersonalAjeno) = await CrearHiloSinAnclaAsync(
            new ConexionIntegracion("gestor@ejemplo.local", "Buzón personal", gestorPropietarioId: Guid.NewGuid()), "hilo-ajeno-vt-2");

        // Acceso total y triaje; la lista cierra el buzón personal ajeno contra la tabla de conexiones, con el usuario real.
        var alcance = new AlcanceDatosServiceFalso(triajeVisible: true, conexionesIntegracionAjenas: [personalAjeno]);
        var ids = (await ListarComo("Administrador", alcance, administrador)).Select(c => c.Id).ToList();

        ids.Should().Contain(delCompartido).And.NotContain(delPersonalAjeno);
        (await DetalleComo(delCompartido, alcance)).Should().NotBeNull();
        (await DetalleComo(delPersonalAjeno, alcance)).Should().BeNull();
    }

    [Fact]
    public async Task El_buzon_personal_propio_es_solo_la_conexion_personal_habilitada_del_propietario_en_este_Tenant()
    {
        // AlcanceDatosService real: la consulta de IAlcanceDatosService.BuzonesPersonalesPropiosAsync. Las guardas que
        // cierran antes de leer la base (Sesión Privilegiada, lente de demo, rol) las fija TriajeVisibleTests (Web.Tests).
        // El esquema admite una sola conexión personal no eliminada por Gestor CAE y Tenant
        // (IX_ConexionesIntegracion_TenantId_GestorPropietarioId, filtrado por NOT EstaEliminado): aquí el propietario
        // tiene la habilitada y una eliminada (fuera del índice); la deshabilitada y la de ConError van en sus propios
        // tests, cada una como única conexión personal de su Gestor CAE, y la de otro Tenant, en otro Tenant.
        var propietario = await OtorgarCarteraAsync();
        var otroGestor = await OtorgarCarteraAsync();
        Guid habilitada;
        await using (var contexto = CrearContexto())
        {
            var personal = new ConexionIntegracion("a@ejemplo.local", "Personal", gestorPropietarioId: propietario);
            var eliminada = new ConexionIntegracion("c@ejemplo.local", "Personal eliminado", gestorPropietarioId: propietario);
            eliminada.MarcarComoEliminado(propietario);
            var ajena = new ConexionIntegracion("d@ejemplo.local", "Personal ajeno", gestorPropietarioId: Guid.NewGuid());
            var compartido = ConexionIntegracion.CrearBuzonCompartido("e@ejemplo.local", "Compartido", Guid.NewGuid().ToString(), propietario);
            var general = new ConexionIntegracion("f@ejemplo.local", "General");
            contexto.ConexionesIntegracion.AddRange(personal, eliminada, ajena, compartido, general);
            await contexto.SaveChangesAsync();
            habilitada = personal.Id;
        }

        var otroTenant = Guid.NewGuid();
        await using (var contexto = CrearContexto(otroTenant))
        {
            contexto.ConexionesIntegracion.Add(
                new ConexionIntegracion("g@ejemplo.local", "Personal en otro Tenant", gestorPropietarioId: propietario));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        (await AlcanceReal(lectura, propietario).BuzonesPersonalesPropiosAsync()).Should().Equal(habilitada);
        (await AlcanceReal(lectura, otroGestor).BuzonesPersonalesPropiosAsync()).Should().BeEmpty("otro Gestor CAE, con cartera");
    }

    [Fact]
    public async Task Sin_Asignacion_de_Cartera_vigente_no_hay_buzon_personal_propio_y_con_ella_si()
    {
        // ADR-011, cartera ∩ operación: sin Asignación de Cartera vigente el ámbito efectivo no concede nada, y el
        // propietario no ve sus hilos sin ancla por la condición 2, como no ve ningún Cliente empresarial, Centro ni
        // Empresa. Con la cartera otorgada, el mismo propietario recupera su conexión: lo que cierra es el ámbito.
        var propietario = Guid.NewGuid();
        Guid conexion;
        await using (var contexto = CrearContexto())
        {
            var personal = new ConexionIntegracion("sin-cartera@ejemplo.local", "Personal", gestorPropietarioId: propietario);
            contexto.ConexionesIntegracion.Add(personal);
            await contexto.SaveChangesAsync();
            conexion = personal.Id;
        }

        await using (var lectura = CrearContexto())
            (await AlcanceReal(lectura, propietario).BuzonesPersonalesPropiosAsync()).Should().BeEmpty("sin cartera vigente");

        await OtorgarCarteraAsync(propietario);
        await using (var lectura = CrearContexto())
            (await AlcanceReal(lectura, propietario).BuzonesPersonalesPropiosAsync()).Should().Equal(conexion);
    }

    [Fact]
    public async Task Un_hilo_de_una_conexion_eliminada_no_sale_en_la_lista_ni_con_el_triaje_visible()
    {
        // La lista excluye las conexiones eliminadas del Tenant (ConversacionesVisibles, leídas sin el filtro de borrado
        // y con el Tenant explícito), como el detalle, que falla cerrado con ConexionIntegracionVisibleAsync.
        var administrador = Guid.NewGuid();
        var compartido = ConexionIntegracion.CrearBuzonCompartido(
            "eliminado@ejemplo.local", "Soporte eliminado", Guid.NewGuid().ToString(), administrador);
        compartido.MarcarComoEliminado(administrador);
        var (_, hilo) = await CrearHiloSinAnclaAsync(compartido, "hilo-eliminado-vt-4");

        var ids = (await ListarComo("Administrador", new AlcanceDatosServiceFalso(triajeVisible: true), administrador))
            .Select(c => c.Id).ToList();

        ids.Should().NotContain(hilo).And.Contain(_conversacionSinCliente, "control: el triaje sí está visible");
    }

    [Fact]
    public async Task La_bandeja_de_un_Gestor_CAE_acotado_sin_triaje_cabe_en_el_presupuesto_SQL_de_Conversaciones()
    {
        // EscalaPresupuestoSqlYAislamientoTests mide «Conversaciones» con la Gestora CAE del piloto, que alcanza el Tenant
        // entero y ve el triaje: con el triaje visible no se pide la sentencia de buzones personales propios
        // (VisibilidadConversacion.ResolverAsync). Este caso mide el otro camino, con el AlcanceDatosService real: un Gestor
        // CAE con la cartera acotada a un Cliente empresarial, sin triaje y con su buzón personal, que sí la lanza. El
        // techo es el del presupuesto «Conversaciones» de la bandeja; el número medido se confirma en CI.
        var gestor = await OtorgarCarteraAsync();
        var (_, delBuzonPropio) = await CrearHiloSinAnclaAsync(
            new ConexionIntegracion("presupuesto@ejemplo.local", "Personal", gestorPropietarioId: gestor), "hilo-presupuesto-vt-5");

        await ListarConAlcanceRealComo(gestor); // calentamiento: el modelo de EF y la primera conexión no cuentan
        var (lista, sentencias) = await ArnesPilotoOutbound.Contador.ContarAsync(() => ListarConAlcanceRealComo(gestor));

        lista.Select(c => c.Id).Should().Contain(delBuzonPropio, "control: el camino del buzón personal propio se recorre")
            .And.NotContain(_conversacionSinCliente, "control: sin triaje");
        sentencias.Should().BeLessThanOrEqualTo(18, $"presupuesto «Conversaciones» de la bandeja; medido {sentencias}");
    }

    [Fact]
    public async Task Una_conexion_personal_deshabilitada_no_es_buzon_personal_propio()
    {
        // Única conexión personal de su Gestor CAE en este Tenant (índice único por Tenant y GestorPropietarioId). Con
        // cartera: lo que la cierra es el estado de la conexión, no el ámbito.
        var propietario = await OtorgarCarteraAsync();
        await using (var contexto = CrearContexto())
        {
            var deshabilitada = new ConexionIntegracion("b@ejemplo.local", "Personal desconectado", gestorPropietarioId: propietario);
            deshabilitada.Deshabilitar();
            contexto.ConexionesIntegracion.Add(deshabilitada);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        (await AlcanceReal(lectura, propietario).BuzonesPersonalesPropiosAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Con_su_buzon_personal_en_ConError_el_propietario_sin_el_Tenant_entero_no_ve_sus_hilos_sin_ancla()
    {
        // Fallo cerrado ya decidido para R-3a (pendiente de que Chris lo confirme): la condición 2 de VisibilidadConversacion
        // solo abre los hilos de una conexión personal Habilitada. Con la conexión en ConError, la consulta real no la
        // devuelve, y sin el Tenant entero (triaje cerrado) su propietario deja de ver esos hilos en la lista y en el detalle.
        var propietario = await OtorgarCarteraAsync();
        var conError = new ConexionIntegracion("h@ejemplo.local", "Personal con error", gestorPropietarioId: propietario);
        conError.MarcarConError("token revocado").Should().BeTrue("control: la conexión queda en ConError");
        var (conexion, hilo) = await CrearHiloSinAnclaAsync(conError, "hilo-con-error-vt-3");

        IReadOnlyList<Guid> buzones;
        await using (var lectura = CrearContexto())
            buzones = await AlcanceReal(lectura, propietario).BuzonesPersonalesPropiosAsync();
        buzones.Should().NotContain(conexion, "la consulta real no devuelve una conexión en ConError");

        var delPropietario = new AlcanceDatosServiceFalso(
            clienteIds: [_clientePropio], triajeVisible: false, buzonesPersonalesPropios: buzones);
        (await ListarComo("GestorCae", delPropietario, propietario)).Select(c => c.Id).Should().NotContain(hilo);
        (await DetalleComo(hilo, delPropietario)).Should().BeNull();
    }

    /// <summary>
    /// Una Asignación de Cartera universal vigente bajo una Asignación de Operación interna acotada al Cliente empresarial
    /// propio, como en <c>AlcanceEmpresaPropiaPorCarteraTests</c>: un ámbito efectivo acotado que concede algo y no alcanza
    /// el Tenant entero (sin triaje).
    /// </summary>
    private async Task<Guid> OtorgarCarteraAsync(Guid? usuarioId = null)
    {
        // Una sola operación acotada por test: IX_AsignacionesOperacion_ResponsableRelacionVigente admite una sola
        // Asignación de Operación vigente por Tenant, servicio y Cliente empresarial; las carteras de cada usuario cuelgan de ella.
        var usuario = usuarioId ?? Guid.NewGuid();
        await using var contexto = CrearContexto();
        var ahora = DateTime.UtcNow;
        if (_operacionAcotada is { } existente)
            contexto.AsignacionesOperacion.Attach(existente);
        else
        {
            _operacionAcotada = AsignacionOperacion.Interna(
                _tenant, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(_clientePropio), ahora, null, ahora);
            contexto.AsignacionesOperacion.Add(_operacionAcotada);
        }
        contexto.AsignacionesCartera.Add(
            AsignacionCartera.Interna(_operacionAcotada, usuario, AmbitoAsignacion.Universal, ahora, null, ahora));
        await contexto.SaveChangesAsync();
        return usuario;
    }

    private async Task<IReadOnlyList<ConversacionListaDto>> ListarConAlcanceRealComo(Guid usuarioId)
    {
        await using var contexto = CrearContexto();
        var usuario = new CurrentUserServiceFalso(usuarioId, "GestorCae", tenantOrigenId: _tenant);
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var handler = new ObtenerConversacionesQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto,
            new AlcanceDatosService(contexto, usuario, tenantActual, new SesionPrivilegiadaAusente()), usuario, tenantActual);

        return (await handler.Handle(new ObtenerConversacionesQuery(), CancellationToken.None)).Elementos;
    }

    private AlcanceDatosService AlcanceReal(CaeManagerDbContext contexto, Guid usuarioId) =>
        new(contexto, new CurrentUserServiceFalso(usuarioId, "GestorCae", tenantOrigenId: _tenant),
            new TenantActualAmbiental { TenantId = _tenant }, new SesionPrivilegiadaAusente());

    private async Task<(Guid Conexion, Guid Conversacion)> CrearHiloSinAnclaAsync(ConexionIntegracion conexion, string hiloExterno)
    {
        await using var contexto = CrearContexto();
        contexto.ConexionesIntegracion.Add(conexion);
        await contexto.SaveChangesAsync();

        var hilo = new Conversacion($"Sin ancla por {conexion.BuzonEmail}");
        hilo.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "quien-sea@externo.test", "<p>Hola</p>");
        hilo.AsociarConexion(conexion.Id, hiloExterno);
        contexto.Conversaciones.Add(hilo);
        await contexto.SaveChangesAsync();
        return (conexion.Id, hilo.Id);
    }

    private async Task<ConversacionDetalleDto?> DetalleComo(Guid conversacionId, AlcanceDatosServiceFalso alcance)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerConversacionPorIdQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto, contexto, alcance,
            new GanssSanitizadorHtmlService(),
            new MotorCoincidenciaConversacionesService(new ConversacionRepository(contexto)), PoliticaNotaInternaPruebas.Con("GestorCae"));

        return await handler.Handle(new ObtenerConversacionPorIdQuery(conversacionId), CancellationToken.None);
    }

    private sealed class SesionPrivilegiadaAusente : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private Task<IReadOnlyList<ConversacionListaDto>> ListarComo(string rol, bool? triajeVisible = null) =>
        ListarComo(rol, new AlcanceDatosServiceFalso(clienteIds: [_clientePropio], triajeVisible: triajeVisible));

    private async Task<IReadOnlyList<ConversacionListaDto>> ListarComo(
        string rol, AlcanceDatosServiceFalso alcance, Guid? usuarioId = null)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerConversacionesQueryHandler(
            contexto,
            contexto,
            contexto,
            contexto,
            contexto,
            contexto,
            alcance,
            new CurrentUserServiceFalso(usuarioId: usuarioId, rol: rol),
            new TenantActualAmbiental { TenantId = _tenant });

        return (await handler.Handle(new ObtenerConversacionesQuery(), CancellationToken.None)).Elementos;
    }

    private async Task<ConversacionDetalleDto?> DetalleSinClienteComo(bool triajeVisible)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerConversacionPorIdQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto, contexto,
            new AlcanceDatosServiceFalso(clienteIds: [_clientePropio], triajeVisible: triajeVisible),
            new GanssSanitizadorHtmlService(),
            new MotorCoincidenciaConversacionesService(new ConversacionRepository(contexto)), PoliticaNotaInternaPruebas.Con("GestorCae"));

        return await handler.Handle(new ObtenerConversacionPorIdQuery(_conversacionSinCliente), CancellationToken.None);
    }

    private CaeManagerDbContext CrearContexto(Guid? tenant = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant ?? _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), ArnesPilotoOutbound.Contador)
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
