using CaeManager.Application.Comunicaciones.Matching;
using CaeManager.Application.Plataforma;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Domain.Integraciones;
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
/// Regresión del hallazgo N-2 de Project-Hydra-Negocio/seguridad/INFORME-AUDITORIA-2.md. La cola de triage
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
        // vez de la cola de triage: acotar solo el listado (ObtenerConversacionesQuery,
        // ya cubierto arriba) dejaría el hilo accesible por Id igual que pasaba
        // con el triage antes de este mismo archivo.
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
        // DECISION_LOG 2026-11-14, decisión 2: el correo de un buzón personal no registrado solo vive en la cuenta de su
        // Gestor CAE propietario, que lo ve siempre (condición 2 de VisibilidadConversacion). Los dos Gestores CAE tienen
        // la misma cartera acotada y ninguno pasa el triaje.
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
        var propietario = Guid.NewGuid();
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
        (await AlcanceReal(lectura, Guid.NewGuid()).BuzonesPersonalesPropiosAsync()).Should().BeEmpty("otro Gestor CAE");
    }

    [Fact]
    public async Task Una_conexion_personal_deshabilitada_no_es_buzon_personal_propio()
    {
        // Única conexión personal de su Gestor CAE en este Tenant (índice único por Tenant y GestorPropietarioId).
        var propietario = Guid.NewGuid();
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
        var propietario = Guid.NewGuid();
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
            alcance,
            new CurrentUserServiceFalso(usuarioId: usuarioId, rol: rol));

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
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
