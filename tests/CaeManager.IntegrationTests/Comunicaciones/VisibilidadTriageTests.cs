using CaeManager.Application.Comunicaciones.Matching;
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

    private Task<IReadOnlyList<ConversacionListaDto>> ListarComo(string rol, bool? triajeVisible = null) =>
        ListarComo(rol, new AlcanceDatosServiceFalso(clienteIds: [_clientePropio], triajeVisible: triajeVisible));

    private async Task<IReadOnlyList<ConversacionListaDto>> ListarComo(string rol, AlcanceDatosServiceFalso alcance)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerConversacionesQueryHandler(
            contexto,
            contexto,
            contexto,
            alcance,
            new CurrentUserServiceFalso(rol: rol));

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

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
