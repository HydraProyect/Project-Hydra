using CaeManager.Domain.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciaVisitaCorreo;
using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciasVisitaCorreoPendientes;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>Fase F: fuente de "visita sorpresa" para la Bandeja del gestor.</summary>
public class ObtenerSugerenciasVisitaCorreoPendientesQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _centroVisibleId;
    private Guid _centroAjenoId;
    private Guid _clienteId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Cliente Sugerencia S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa Sugerencia S.L.", "B87654323");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centroVisible = new Centro(cliente.Id, empresa.Id, "Centro visible");
        var centroAjeno = new Centro(cliente.Id, empresa.Id, "Centro ajeno a la cartera");
        contexto.Centros.AddRange(centroVisible, centroAjeno);
        await contexto.SaveChangesAsync();

        _centroVisibleId = centroVisible.Id;
        _centroAjenoId = centroAjeno.Id;
        _clienteId = cliente.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private async Task<Guid> SembrarSugerenciaAsync(
        Guid? centroId, bool resuelta, CanalConversacion canal = CanalConversacion.Correo, Guid? clienteId = null)
    {
        await using var contexto = CrearContexto();

        var conversacion = canal == CanalConversacion.WhatsApp
            ? Conversacion.CrearWhatsApp("+34600000000", Guid.NewGuid(), clienteId: null, ejecutivoId: null)
            : new Conversacion("Solicitud de entrada", clienteId: clienteId);
        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();

        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, canal, "cliente@ejemplo.com", "Necesitamos entrar mañana");
        await contexto.SaveChangesAsync();

        var sugerencia = new SugerenciaVisitaCorreo(mensaje.Id, centroId, DiaDeNegocio.Hoy().AddDays(1), null, "Pide entrar mañana", 90, 90, 90);
        if (resuelta) sugerencia.Resolver(ResolucionSugerencia.Descartada);
        contexto.SugerenciasVisitaCorreo.Add(sugerencia);
        await contexto.SaveChangesAsync();

        return sugerencia.Id;
    }

    [Fact]
    public async Task Solo_devuelve_sugerencias_sin_resolver()
    {
        var pendienteId = await SembrarSugerenciaAsync(_centroVisibleId, resuelta: false);
        await SembrarSugerenciaAsync(_centroVisibleId, resuelta: true);

        await using var lectura = CrearContexto();
        var handler = Handler(lectura, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerSugerenciasVisitaCorreoPendientesQuery(), CancellationToken.None);

        resultado.Should().ContainSingle(s => s.Id == pendienteId);
    }

    [Fact]
    public async Task Respeta_el_alcance_de_cartera_por_centro()
    {
        await SembrarSugerenciaAsync(_centroAjenoId, resuelta: false);

        await using var lectura = CrearContexto();
        var handler = Handler(lectura, new AlcanceDatosServiceFalso(centroIds: [_centroVisibleId]));

        var resultado = await handler.Handle(new ObtenerSugerenciasVisitaCorreoPendientesQuery(), CancellationToken.None);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task Trae_el_canal_de_la_conversacion_de_origen()
    {
        var whatsappId = await SembrarSugerenciaAsync(_centroVisibleId, resuelta: false, canal: CanalConversacion.WhatsApp);

        await using var lectura = CrearContexto();
        var handler = Handler(lectura, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerSugerenciasVisitaCorreoPendientesQuery(), CancellationToken.None);

        resultado.Should().ContainSingle(s => s.Id == whatsappId).Subject.Canal.Should().Be(CanalConversacion.WhatsApp);
    }

    [Fact]
    public async Task Un_Gestor_CAE_acotado_con_el_Centro_en_cartera_no_ve_la_sugerencia_de_un_hilo_de_triaje_y_con_triaje_si()
    {
        // D-6 b, R-3a: el listado (Mi trabajo y la Bandeja del Gestor CAE, con su contador) decide como la acción
        // (DescartarSugerenciaVisitaCorreoCommand y CrearVisitaCommand, con MensajeVisibleAsync): la sugerencia nace de un
        // hilo sin ancla, que solo se ve con el triaje. El Centro está en cartera en los dos casos; solo cambia el triaje.
        var sugerenciaId = await SembrarSugerenciaAsync(_centroVisibleId, resuelta: false);

        await using var lectura = CrearContexto();
        var acotado = await Handler(lectura, new AlcanceDatosServiceFalso(
                centroIds: [_centroVisibleId], clienteIds: [_clienteId], triajeVisible: false))
            .Handle(new ObtenerSugerenciasVisitaCorreoPendientesQuery(), CancellationToken.None);
        var conTriaje = await Handler(lectura, new AlcanceDatosServiceFalso(
                centroIds: [_centroVisibleId], clienteIds: [_clienteId], triajeVisible: true))
            .Handle(new ObtenerSugerenciasVisitaCorreoPendientesQuery(), CancellationToken.None);

        acotado.Should().NotContain(s => s.Id == sugerenciaId);
        conTriaje.Should().ContainSingle(s => s.Id == sugerenciaId);
    }

    [Fact]
    public async Task La_sugerencia_de_un_hilo_anclado_a_un_Cliente_empresarial_de_la_cartera_se_ve_sin_triaje()
    {
        // Control del anterior: sin triaje, lo que cierra es el hilo sin ancla, no el filtro de conversación entero.
        var sugerenciaId = await SembrarSugerenciaAsync(_centroVisibleId, resuelta: false, clienteId: _clienteId);

        await using var lectura = CrearContexto();
        var resultado = await Handler(lectura, new AlcanceDatosServiceFalso(
                centroIds: [_centroVisibleId], clienteIds: [_clienteId], triajeVisible: false))
            .Handle(new ObtenerSugerenciasVisitaCorreoPendientesQuery(), CancellationToken.None);

        resultado.Should().ContainSingle(s => s.Id == sugerenciaId);
    }

    [Fact]
    public async Task El_prefill_de_la_Visita_desde_una_sugerencia_decide_como_la_accion()
    {
        // ObtenerSugerenciaVisitaCorreoQuery da paso a CrearVisitaCommand, que autoriza con MensajeVisibleAsync: un hilo
        // de triaje abre el prefill con el triaje visible y no lo abre sin él, como la acción.
        var sugerenciaId = await SembrarSugerenciaAsync(_centroVisibleId, resuelta: false);

        await using var lectura = CrearContexto();
        var conTriaje = await new ObtenerSugerenciaVisitaCorreoQueryHandler(lectura, new AlcanceDatosServiceFalso(
                centroIds: [_centroVisibleId], clienteIds: [_clienteId], triajeVisible: true))
            .Handle(new ObtenerSugerenciaVisitaCorreoQuery(sugerenciaId), CancellationToken.None);
        var sinTriaje = await new ObtenerSugerenciaVisitaCorreoQueryHandler(lectura, new AlcanceDatosServiceFalso(
                centroIds: [_centroVisibleId], clienteIds: [_clienteId], triajeVisible: false))
            .Handle(new ObtenerSugerenciaVisitaCorreoQuery(sugerenciaId), CancellationToken.None);

        conTriaje.Should().NotBeNull();
        sinTriaje.Should().BeNull();
    }

    private ObtenerSugerenciasVisitaCorreoPendientesQueryHandler Handler(CaeManagerDbContext lectura, AlcanceDatosServiceFalso alcance) =>
        new(lectura, lectura, lectura, alcance, lectura, new CurrentUserServiceFalso(rol: "GestorCae"),
            new TenantActualAmbiental { TenantId = _tenant });

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
