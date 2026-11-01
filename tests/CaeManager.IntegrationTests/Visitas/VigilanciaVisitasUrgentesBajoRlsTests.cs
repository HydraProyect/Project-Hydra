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
/// cableado de producción: cuenta el Tenant entero, avisa a sus Administradores y a Dirección CAE, no
/// toca a otro Tenant y no abre ese alcance a quien pregunta por la vía de una sesión.
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
    private const string TituloDelAviso = "Gestiones urgentes de visita";

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
        await fixture.Arnes.EnTenantAsync(tenant, (db, _) =>
            db.Set<NotificacionUsuario>().Where(n => n.Titulo == TituloDelAviso).ExecuteDeleteAsync());
        await CicloAsync();

        var estado = await EstadoAsync(tenant);
        estado.UltimoResultadoExitoso.Should().BeTrue();
        estado.UltimoMensajeError.Should().BeNull();
        estado.UltimosElementosEvaluados.Should().Be(inicial.UltimosElementosEvaluados + 1);
        (await EstadoAsync(otroTenant)).UltimosElementosEvaluados.Should().Be(
            otroInicial.UltimosElementosEvaluados, "la Visita es de otro Tenant");

        var total = estado.UltimosElementosEvaluados!.Value;
        var mensaje = total == 1
            ? "Hay 1 gestión urgente de visita pendiente (ventana mínima de validación)."
            : $"Hay {total} gestiones urgentes de visita pendientes (ventana mínima de validación).";

        var avisados = await fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Set<NotificacionUsuario>()
            .Where(n => n.Titulo == TituloDelAviso && n.Mensaje == mensaje)
            .Select(n => n.UsuarioDestinatarioId)
            .ToListAsync());
        avisados.Should().BeEquivalentTo(destinatarios);
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
                return administradores.Concat(direccionCae).Select(u => u.Id).Distinct().ToList();
            });
            if (destinatarios.Count > 0)
                return (tenant, destinatarios);
        }

        throw new InvalidOperationException("Ningún Tenant sembrado con Centros tiene destinatarios del aviso.");
    }

    private Task<EstadoAutomatizacion> EstadoAsync(Guid tenant) =>
        fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Set<EstadoAutomatizacion>().AsNoTracking()
            .SingleAsync(e => e.TrabajoId == CatalogoAutomatizaciones.VigilanciaVisitasUrgentes));

    private Task AnadirVisitasAsync(Guid tenant, int cuantas, DateOnly inicio, DateOnly fin) =>
        fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            var centro = await db.Centros.OrderBy(c => c.Id).Select(c => c.Id).FirstAsync();
            db.Visitas.AddRange(Enumerable.Range(0, cuantas).Select(_ => new Visita(centro, inicio, fin, notas: null)));
            return await db.SaveChangesAsync();
        });

    private sealed class RelojFijo(DateTimeOffset instante) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instante;
    }
}
