using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Visitas;
using CaeManager.IntegrationTests.Arranque;
using CaeManager.IntegrationTests.EstadoMaterializado;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.IntegrationTests.Visitas;

/// <summary>
/// C-02: la vigilancia de gestiones urgentes pedía la cola de la Bandeja sin usuario, el alcance de
/// datos caía en «ninguno» y el trabajo se registraba «exitoso, 0 evaluados» sin avisar jamás. Aquí
/// corre un ciclo real del servicio de fondo bajo <c>cae_app_runtime</c>, sin usuario y con el
/// cableado de producción: cuenta el Tenant entero, mantiene un aviso agregado por Administrador y por
/// Dirección CAE, no toca a otro Tenant y no abre ese alcance a quien pregunta por la vía de una sesión.
///
/// <para>
/// La línea base de cada test sale de un ciclo previo: la siembra ya puede dejar gestiones urgentes.
/// Fixture propia (otra base sembrada): estos tests añaden Visitas y notificaciones.
/// </para>
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class VigilanciaVisitasUrgentesBajoRlsTests(EstadoMaterializadoPilotoFixture fixture)
    : IClassFixture<EstadoMaterializadoPilotoFixture>
{
    [Fact]
    public async Task Sin_usuario_una_Visita_urgente_llega_a_los_destinatarios_de_su_Tenant_y_no_toca_a_otro()
    {
        using var reloj = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(DateTimeOffset.UtcNow));
        using var actor = AmbitoActorAuditoria.EstablecerSistema();
        var tenants = await TenantsConCentroAsync();
        var (tenant, destinatarios) = await TenantConDestinatariosAsync(tenants);
        var otroTenant = tenants.First(t => t != tenant);

        await CicloAsync();
        var inicial = await EstadoAsync(tenant);
        var otroInicial = await EstadoAsync(otroTenant);

        var manana = DiaDeNegocio.Hoy().AddDays(1);
        await AnadirVisitasAsync(tenant, 1, manana, manana);
        await CicloAsync();

        var estado = await EstadoAsync(tenant);
        estado.UltimoResultadoExitoso.Should().BeTrue();
        estado.UltimoMensajeError.Should().BeNull();
        estado.UltimosElementosEvaluados.Should().Be(inicial.UltimosElementosEvaluados + 1);
        (await EstadoAsync(otroTenant)).UltimosElementosEvaluados.Should().Be(
            otroInicial.UltimosElementosEvaluados, "la Visita es de otro Tenant");

        var mensaje = MensajeEsperado(estado.UltimosElementosEvaluados!.Value);

        var avisos = await AvisosAsync(tenant);
        avisos.Select(n => n.UsuarioDestinatarioId).Should().BeEquivalentTo(destinatarios, "uno solo por destinatario");
        avisos.Should().OnlyContain(n => n.Mensaje == mensaje);
    }

    [Fact]
    public async Task Sin_leer_se_actualiza_la_misma_fila_y_leido_con_urgencias_se_reabre_en_el_ciclo_siguiente()
    {
        using var reloj = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(DateTimeOffset.UtcNow));
        using var actor = AmbitoActorAuditoria.EstablecerSistema();
        var (tenant, destinatarios) = await TenantConDestinatariosAsync(await TenantsConCentroAsync());
        var manana = DiaDeNegocio.Hoy().AddDays(1);
        await AnadirVisitasAsync(tenant, 1, manana, manana);

        await CicloAsync();
        var primeros = await AvisosAsync(tenant);
        primeros.Should().HaveCount(destinatarios.Count);

        await AnadirVisitasAsync(tenant, 1, manana, manana);
        await CicloAsync();
        var mensaje = MensajeEsperado((await EstadoAsync(tenant)).UltimosElementosEvaluados!.Value);
        var actualizados = await AvisosAsync(tenant);
        actualizados.Select(n => n.Id).Should().BeEquivalentTo(primeros.Select(n => n.Id));
        actualizados.Should().OnlyContain(n => n.Mensaje == mensaje);
        mensaje.Should().NotBe(primeros[0].Mensaje, "el número ha subido");

        var leido = primeros[0].Id;
        await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            (await db.Set<NotificacionUsuario>().SingleAsync(n => n.Id == leido)).MarcarComoLeida();
            return await db.SaveChangesAsync();
        });
        await CicloAsync();

        var tras = await AvisosAsync(tenant);
        tras.Select(n => n.Id).Should().BeEquivalentTo(primeros.Select(n => n.Id), "se reabre la misma fila");
        tras.Should().OnlyContain(n => !n.Leida);
    }

    /// <summary>
    /// El recuento cero también escribe, sin usuario y bajo <c>cae_app_runtime</c>: el aviso sale de la
    /// campana con su fila intacta, y vuelve en esa misma fila si reaparecen urgencias. El cero se fija
    /// sustituyendo solo el recuento, porque la siembra y los otros tests dejan gestiones que no vencen.
    /// </summary>
    [Fact]
    public async Task A_cero_se_retira_el_aviso_y_vuelve_en_la_misma_fila_si_reaparecen_urgencias()
    {
        using var reloj = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(DateTimeOffset.UtcNow));
        using var actor = AmbitoActorAuditoria.EstablecerSistema();
        var (tenant, _) = await TenantConDestinatariosAsync(await TenantsConCentroAsync());
        var manana = DiaDeNegocio.Hoy().AddDays(1);
        await AnadirVisitasAsync(tenant, 1, manana, manana);
        await CicloAsync();
        var antes = await AvisosAsync(tenant);
        antes.Should().NotBeEmpty().And.OnlyContain(n => !n.Leida);

        await CicloSinUrgenciasAsync(tenant);

        var retirados = await AvisosAsync(tenant);
        retirados.Select(n => n.Id).Should().BeEquivalentTo(antes.Select(n => n.Id));
        retirados.Should().OnlyContain(n => n.Leida);

        await CicloAsync();

        var devueltos = await AvisosAsync(tenant);
        devueltos.Select(n => n.Id).Should().BeEquivalentTo(antes.Select(n => n.Id));
        devueltos.Should().OnlyContain(n => !n.Leida);
    }

    /// <summary>
    /// Bajo <c>cae_app_runtime</c> la lectura de avisos solo ve su Tenant, y lo corta la RLS: sin los
    /// filtros de EF tampoco aparecen, así que el aviso de otro Tenant no se actualiza ni se retira desde aquí.
    /// </summary>
    [Fact]
    public async Task La_lectura_de_avisos_no_ve_los_de_otro_Tenant()
    {
        using var actor = AmbitoActorAuditoria.EstablecerSistema();
        var tenants = await TenantsConCentroAsync();
        var (tenant, destinatarios) = await TenantConDestinatariosAsync(tenants);
        var otroTenant = tenants.First(t => t != tenant);
        var gestion = Guid.NewGuid();
        await AgregarAvisoAsync(tenant, destinatarios[0], gestion);

        var enSuTenant = await fixture.Arnes.EnTenantAsync(tenant, (_, sp) =>
            sp.GetRequiredService<INotificacionUsuarioRepository>().ObtenerPorEventoOrigenAsync(gestion));
        var enOtroTenant = await fixture.Arnes.EnTenantAsync(otroTenant, (_, sp) =>
            sp.GetRequiredService<INotificacionUsuarioRepository>().ObtenerPorEventoOrigenAsync(gestion));
        var enOtroTenantSinFiltros = await fixture.Arnes.EnTenantAsync(otroTenant, (db, _) =>
            db.Set<NotificacionUsuario>().IgnoreQueryFilters().CountAsync(n => n.EventoOrigenId == gestion));

        enSuTenant.Should().ContainSingle();
        enOtroTenant.Should().BeEmpty();
        enOtroTenantSinFiltros.Should().Be(0);
    }

    /// <summary>
    /// Si dos instancias llegaran a la vez, lo último que las separa es el índice único. Aquí solo se
    /// fija el índice, con dos inserciones directas; no se ejerce el servicio en concurrencia.
    /// </summary>
    [Fact]
    public async Task El_indice_unico_impide_una_segunda_fila_del_mismo_aviso()
    {
        using var actor = AmbitoActorAuditoria.EstablecerSistema();
        var (tenant, destinatarios) = await TenantConDestinatariosAsync(await TenantsConCentroAsync());
        var gestion = Guid.NewGuid();
        await AgregarAvisoAsync(tenant, destinatarios[0], gestion);

        var segunda = () => AgregarAvisoAsync(tenant, destinatarios[0], gestion);

        await segunda.Should().ThrowAsync<DbUpdateException>();
        (await AvisosAsync(tenant, gestion)).Should().ContainSingle();
    }

    private static string MensajeEsperado(int gestiones) => gestiones == 1
        ? "Hay 1 gestión urgente de visita pendiente (ventana mínima de validación)."
        : $"Hay {gestiones} gestiones urgentes de visita pendientes (ventana mínima de validación).";

    private async Task CicloSinUrgenciasAsync(Guid tenant)
    {
        var servicios = fixture.Arnes.Servicios;
        var servicio = new VigilanciaSinUrgencias(
            servicios.GetRequiredService<IServiceScopeFactory>(), servicios.GetRequiredService<IEleccionLiderService>(),
            servicios.GetRequiredService<ILogger<VigilanciaVisitasUrgentesHostedService>>());
        await servicio.ProcesarTenantsAsync([tenant], CancellationToken.None);
        (await EstadoAsync(tenant)).UltimoResultadoExitoso.Should().BeTrue();
    }

    /// <summary>
    /// Contraprueba del alcance: lo que ve el servicio no se ha abierto a la consulta que usan las
    /// sesiones. Sin usuario, esa consulta sigue devolviendo vacío en un Tenant con Visitas urgentes.
    /// </summary>
    [Fact]
    public async Task La_consulta_de_las_sesiones_sigue_vacia_sin_usuario_aunque_haya_Visitas_urgentes()
    {
        using var reloj = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(DateTimeOffset.UtcNow));
        using var actor = AmbitoActorAuditoria.EstablecerSistema();
        var tenant = (await TenantsConCentroAsync())[0];
        var manana = DiaDeNegocio.Hoy().AddDays(1);
        await AnadirVisitasAsync(tenant, 1, manana, manana);

        var resultado = await fixture.Arnes.EnTenantAsync(tenant, (_, sp) =>
            sp.GetRequiredService<IMediator>().Send(GestionesUrgentesDeVisita.ConsultaCandidatas()));

        resultado.TotalElementos.Should().Be(0);
    }

    [Fact]
    public async Task Una_sugerencia_de_visita_sin_Centro_resuelto_cuenta_en_el_Tenant_entero()
    {
        using var reloj = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(DateTimeOffset.UtcNow));
        using var actor = AmbitoActorAuditoria.EstablecerSistema();
        var tenant = (await TenantsConCentroAsync())[0];

        await CicloAsync();
        var inicial = await EstadoAsync(tenant);

        await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            var conversacion = new Conversacion("Solicitud urgente de entrada");
            db.Conversaciones.Add(conversacion);
            await db.SaveChangesAsync();
            var mensaje = conversacion.AgregarMensaje(
                DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@ejemplo.test", "Necesitamos entrar hoy mismo");
            await db.SaveChangesAsync();
            db.SugerenciasVisitaCorreo.Add(new SugerenciaVisitaCorreo(mensaje.Id, null, null, null, "Pide entrar hoy mismo", 90, 90, 0));
            return await db.SaveChangesAsync();
        });
        await CicloAsync();

        (await EstadoAsync(tenant)).UltimosElementosEvaluados.Should().Be(inicial.UltimosElementosEvaluados + 1);
    }

    /// <summary>
    /// Las Visitas en curso son candidatas, van delante por fecha y no cuentan como urgentes: con más
    /// de una página de ellas, leer solo la primera se quedaría sin la urgente que viene detrás.
    /// </summary>
    [Fact]
    public async Task Las_Visitas_en_curso_de_la_primera_pagina_no_tapan_una_urgente_de_la_siguiente()
    {
        using var reloj = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(DateTimeOffset.UtcNow));
        using var actor = AmbitoActorAuditoria.EstablecerSistema();
        var tenant = (await TenantsConCentroAsync())[1];
        var hoy = DiaDeNegocio.Hoy();

        await CicloAsync();
        var inicial = await EstadoAsync(tenant);

        await AnadirVisitasAsync(tenant, GestionesUrgentesDeVisita.ConsultaCandidatas().TamanoPagina + 1, hoy.AddDays(-1), hoy.AddDays(1));
        await AnadirVisitasAsync(tenant, 1, hoy.AddDays(1), hoy.AddDays(1));
        await CicloAsync();

        (await EstadoAsync(tenant)).UltimosElementosEvaluados.Should().Be(inicial.UltimosElementosEvaluados + 1);
    }

    // ---- Ayudas ----

    /// <summary>Un ciclo del servicio de fondo sobre todos los Tenants, sin la elección de líder ni la espera.</summary>
    private Task CicloAsync()
    {
        var servicios = fixture.Arnes.Servicios;
        var servicio = new VigilanciaVisitasUrgentesHostedService(
            servicios.GetRequiredService<IServiceScopeFactory>(), servicios.GetRequiredService<IEleccionLiderService>(),
            servicios.GetRequiredService<ILogger<VigilanciaVisitasUrgentesHostedService>>());
        return servicio.ProcesarTodosLosTenantsAsync(CancellationToken.None);
    }

    /// <summary>Los Tenants con algún Centro, en orden estable.</summary>
    private async Task<IReadOnlyList<Guid>> TenantsConCentroAsync()
    {
        var conCentro = new List<Guid>();
        foreach (var tenant in fixture.Tenants)
            if (await fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Centros.AnyAsync()))
                conCentro.Add(tenant);

        conCentro.Should().HaveCountGreaterThanOrEqualTo(2, "hacen falta dos Tenants con Centros para comparar");
        return conCentro;
    }

    /// <summary>El primer Tenant con Administrador o Dirección CAE visibles: sin ellos, no se probaría el aviso.</summary>
    private async Task<(Guid Tenant, IReadOnlyList<Guid> Destinatarios)> TenantConDestinatariosAsync(IEnumerable<Guid> tenants)
    {
        foreach (var tenant in tenants)
        {
            var destinatarios = await fixture.Arnes.EnTenantAsync(tenant, async (_, sp) =>
            {
                var directorio = sp.GetRequiredService<DirectorioUsuariosTenant>();
                var administradores = await directorio.ObtenerVisiblesEnRolAsync(Roles.Administrador);
                var direccionCae = await directorio.ObtenerVisiblesEnRolAsync(Roles.DireccionCae);
                var rolesEjercidos = await directorio.ObtenerRolesDeOperadoresDelegadosAsync();
                return VigilanciaVisitasUrgentesHostedService
                    .DestinatariosDelAviso(administradores, direccionCae, rolesEjercidos)
                    .Select(u => u.Id).ToList();
            });
            if (destinatarios.Count > 0)
                return (tenant, destinatarios);
        }

        throw new InvalidOperationException("Ningún Tenant sembrado con Centros tiene destinatarios del aviso.");
    }

    private Task<EstadoAutomatizacion> EstadoAsync(Guid tenant) =>
        fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Set<EstadoAutomatizacion>().AsNoTracking()
            .SingleAsync(e => e.TrabajoId == CatalogoAutomatizaciones.VigilanciaVisitasUrgentes));

    private Task<List<Guid>> AnadirVisitasAsync(Guid tenant, int cuantas, DateOnly inicio, DateOnly fin) =>
        fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            var centro = await db.Centros.OrderBy(c => c.Id).Select(c => c.Id).FirstAsync();
            var visitas = Enumerable.Range(0, cuantas).Select(_ => new Visita(centro, inicio, fin, notas: null)).ToList();
            db.Visitas.AddRange(visitas);
            await db.SaveChangesAsync();
            return visitas.Select(v => v.Id).ToList();
        });

    private Task<List<NotificacionUsuario>> AvisosAsync(Guid tenant) =>
        AvisosAsync(tenant, VigilanciaVisitasUrgentesHostedService.EventoAvisoGestionesUrgentes);

    private Task<List<NotificacionUsuario>> AvisosAsync(Guid tenant, Guid evento) =>
        fixture.Arnes.EnTenantAsync(tenant, (db, _) =>
            db.Set<NotificacionUsuario>().AsNoTracking().Where(n => n.EventoOrigenId == evento).ToListAsync());

    private Task<int> AgregarAvisoAsync(Guid tenant, Guid destinatario, Guid gestion) =>
        fixture.Arnes.EnTenantAsync(tenant, (db, _) =>
        {
            db.Set<NotificacionUsuario>().Add(new NotificacionUsuario(destinatario, "Gestión urgente de visita", "Aviso de prueba.", eventoOrigenId: gestion));
            return db.SaveChangesAsync();
        });

    private sealed class VigilanciaSinUrgencias(
        IServiceScopeFactory ambitoFactory, IEleccionLiderService eleccionLider, ILogger<VigilanciaVisitasUrgentesHostedService> logger)
        : VigilanciaVisitasUrgentesHostedService(ambitoFactory, eleccionLider, logger)
    {
        protected override Task<int> ContarGestionesUrgentesAsync(IServiceScope ambito, CancellationToken stoppingToken) =>
            Task.FromResult(0);
    }

    private sealed class RelojFijo(DateTimeOffset instante) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instante;
    }
}
