using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// La siembra completa de Comunicaciones de la demo (<see cref="ComunicacionesDemoCompletaSeeder"/>): deja en el Tenant de la demo
/// un conjunto suficiente para revisar toda la pantalla de Comunicaciones y la de Buzones; es idempotente, no deja nada que un
/// servicio de fondo pueda leer o enviar y no hace nada con la guarda <c>DatosPrueba:Activo</c> apagada.
/// </summary>
public class ComunicacionesDemoCompletaSeederTests
{
    private const string MarcaCorreo = "demo-comunicaciones-";
    private const string MarcaWhatsApp = "+3460022200";

    private sealed record Conjunto(
        Guid TenantId,
        List<Conversacion> Conversaciones,
        List<Mensaje> Mensajes,
        List<AdjuntoMensaje> Adjuntos,
        List<ConexionIntegracion> Conexiones,
        int Notas,
        int Reclamaciones,
        int CredencialesDeEsasConexiones,
        int ConversacionesAjenas);

    [Fact]
    public async Task Con_la_guarda_apagada_no_siembra_nada_ni_toca_el_almacen()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var almacen = new AlmacenDeSiembraInboundEnMemoria();
        var apagada = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DatosPrueba:Activo"] = "false" }).Build();

        using (var ambito = arnes.Servicios.CreateScope())
        {
            var sp = ambito.ServiceProvider;
            // Tanto la siembra de la demo (que la contiene) como la nuestra, llamada directa, deben ser inertes.
            await DelegacionDemoSeeder.SeedAsync(
                sp.GetRequiredService<CaeManagerDbContext>(), sp.GetRequiredService<UserManager<ApplicationUser>>(),
                sp.GetRequiredService<IUserStore<ApplicationUser>>(), apagada, EntornoDePrueba.Desarrollo, NullLogger.Instance,
                almacen: almacen);
            await ComunicacionesDemoCompletaSeeder.SeedAsync(
                sp.GetRequiredService<CaeManagerDbContext>(), sp.GetRequiredService<UserManager<ApplicationUser>>(),
                almacen, apagada, NullLogger.Instance);
        }

        var hay = await LeerConjuntoAsync(arnes);
        hay.Should().BeNull("con DatosPrueba:Activo apagado no se siembra ni una conversación, buzón o nota de esta siembra");
        almacen.Llamadas.Should().Be(0, "ni siquiera se guarda el PDF de la demo");
        almacen.Archivos.Should().BeEmpty();
    }

    [Fact]
    public async Task Siembra_el_conjunto_completo_en_el_Tenant_de_la_demo_y_cubre_toda_la_pantalla()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        var almacen = new AlmacenDeSiembraInboundEnMemoria();
        await SembrarDemoAsync(arnes, almacen);

        var c = (await LeerConjuntoAsync(arnes))!;
        var ahora = DateTime.UtcNow;

        c.Conversaciones.Should().HaveCount(16, "13 de correo y 3 de WhatsApp");
        c.Conversaciones.Count(x => x.Canal == CanalConversacion.Correo).Should().Be(13);
        c.Conversaciones.Count(x => x.Canal == CanalConversacion.WhatsApp).Should().Be(3);
        c.ConversacionesAjenas.Should().BeGreaterThan(0,
            "control positivo: las conversaciones que ya sembraba DatosPruebaSeeder siguen ahí, y esta siembra completó lo que faltaba");

        foreach (var estado in Enum.GetValues<EstadoConversacion>())
            c.Conversaciones.Should().Contain(x => x.Estado == estado, $"debe haber una conversación {estado}");
        foreach (var prioridad in Enum.GetValues<PrioridadConversacion>())
            c.Conversaciones.Should().Contain(x => x.Prioridad == prioridad, $"debe haber una conversación {prioridad}");

        c.Conversaciones.Where(x => x.EmpresaId is not null).Select(x => x.EmpresaId).Distinct().Count()
            .Should().BeGreaterThanOrEqualTo(3, "al menos tres Empresas cliente");
        c.Conversaciones.Should().Contain(x => x.EmpresaId == null, "una con remitente sin identificar");
        c.Conversaciones.Select(x => x.NumeroTicket).Should().OnlyHaveUniqueItems().And.OnlyContain(n => n > 0);

        var asignados = c.Conversaciones.Select(x => x.EjecutivoAsignadoId).ToList();
        asignados.Count(a => a is null).Should().BeGreaterThan(0, "alguna sin asignar");
        asignados.Where(a => a is not null).Distinct().Count().Should().BeGreaterThanOrEqualTo(2, "asignadas al Gestor CAE de la demo y a otro");

        c.Conversaciones.Should().NotContain(x => x.AvisoCriticaPendienteId != null,
            "una Crítica sembrada nace con su aviso confirmado: el reintento de avisos no tiene nada que enviar");
        c.Notas.Should().BeGreaterThanOrEqualTo(4);
        c.Reclamaciones.Should().BeGreaterThanOrEqualTo(2, "historial para el filtro Origen");

        foreach (var reparto in Enum.GetValues<EstadoRepartoAdjunto>())
            c.Adjuntos.Should().Contain(a => a.EstadoReparto == reparto, $"debe haber un adjunto {reparto}");

        // Plazos: se recalculan con la misma regla que la pantalla, sobre las que esperan respuesta nuestra.
        var calendario = new CalendarioLaboralOperadorCae([]);
        var vencimientos = c.Conversaciones
            .Where(x => x.Estado is EstadoConversacion.Abierta or EstadoConversacion.Pendiente)
            .Select(x =>
            {
                var primero = PlazosConversacionService.PrimerEntranteSinRespuesta(c.Mensajes.Where(m => m.ConversacionId == x.Id));
                return primero is null
                    ? (DateTimeOffset?)null
                    : PlazosConversacionService.Vencimiento(x.Prioridad, new DateTimeOffset(DateTime.SpecifyKind(primero.Value, DateTimeKind.Utc)), calendario);
            })
            .Where(v => v is not null).Select(v => v!.Value).ToList();
        vencimientos.Count(v => v < ahora).Should().BeGreaterThanOrEqualTo(1, "al menos un plazo vencido");
        vencimientos.Count(v => v > ahora.AddHours(2)).Should().BeGreaterThanOrEqualTo(2, "al menos dos con tiempo");
        // shortcut: no se exige «una por vencer en menos de 2 h» ni «Crítica vencida» porque fuera de jornada laboral el reloj del
        // calendario hace que dependa de la hora a la que corre el test.

        // El PDF dividible está de verdad en el almacén y tiene seis páginas.
        var pdf = c.Adjuntos.Single(a => a.NombreArchivo == "expediente-trabajadores-escaneado.pdf");
        almacen.Archivos.Should().ContainKey(pdf.ArchivoUrl);
        var texto = Encoding.Latin1.GetString(almacen.Archivos[pdf.ArchivoUrl]);
        texto.Should().StartWith("%PDF-");
        System.Text.RegularExpressions.Regex.Matches(texto, @"/Type /Page\b").Count.Should().Be(6);
    }

    [Fact]
    public async Task Los_canales_sembrados_estan_deshabilitados_y_sin_credencial_para_que_ningun_servicio_de_fondo_los_toque()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        await SembrarDemoAsync(arnes, new AlmacenDeSiembraInboundEnMemoria());

        var c = (await LeerConjuntoAsync(arnes))!;

        c.Conexiones.Should().HaveCount(3, "un buzón compartido, uno personal y una línea de WhatsApp");
        c.Conexiones.Select(x => x.Proveedor).Should().Contain([ProveedorIntegracion.Microsoft365, ProveedorIntegracion.WhatsApp]);
        c.Conexiones.Should().OnlyContain(x => x.Estado == EstadoConexionIntegracion.Deshabilitada,
            "todos los servicios de ingesta, renovación, verificación y recuperación saltan una conexión Deshabilitada");
        c.CredencialesDeEsasConexiones.Should().Be(0, "sin credencial no hay token que renovar ni llamada a Microsoft Graph");
        c.Mensajes.Where(m => m.Direccion == DireccionMensaje.Saliente).Should().OnlyContain(
            m => m.EstadoEntrega == EstadoEntregaMensaje.Enviado || m.EstadoEntrega == EstadoEntregaMensaje.Leido,
            "los salientes son históricos: ninguno queda pendiente de envío");
    }

    [Fact]
    public async Task Un_segundo_arranque_no_duplica_nada()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        await SembrarDemoAsync(arnes, new AlmacenDeSiembraInboundEnMemoria());
        var primero = (await LeerConjuntoAsync(arnes))!;

        var segundoAlmacen = new AlmacenDeSiembraInboundEnMemoria();
        using (var ambito = arnes.Servicios.CreateScope())
        {
            var sp = ambito.ServiceProvider;
            var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
            using (AmbitoTenantExplicito.Establecer(primero.TenantId))
            {
                await ComunicacionesDemoCompletaSeeder.SeedAsync(
                    sp.GetRequiredService<CaeManagerDbContext>(), userManager, segundoAlmacen,
                    sp.GetRequiredService<IConfiguration>(), NullLogger.Instance);
            }
        }

        var segundo = (await LeerConjuntoAsync(arnes))!;
        segundo.Conversaciones.Should().HaveCount(primero.Conversaciones.Count);
        segundo.Conexiones.Should().HaveCount(primero.Conexiones.Count);
        segundo.Adjuntos.Should().HaveCount(primero.Adjuntos.Count);
        segundo.Notas.Should().Be(primero.Notas);
        segundo.Reclamaciones.Should().Be(primero.Reclamaciones);
        segundo.ConversacionesAjenas.Should().Be(primero.ConversacionesAjenas);
        segundoAlmacen.Llamadas.Should().Be(0, "no hay nada pendiente, así que tampoco se vuelve a guardar el PDF");
    }

    private static async Task SembrarDemoAsync(ArnesDeArranqueRuntime arnes, AlmacenDeSiembraInboundEnMemoria almacen)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        await DelegacionDemoSeeder.SeedAsync(
            sp.GetRequiredService<CaeManagerDbContext>(),
            sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<IUserStore<ApplicationUser>>(),
            sp.GetRequiredService<IConfiguration>(),
            EntornoDePrueba.Desarrollo,
            NullLogger.Instance,
            almacen: almacen);
    }

    /// <summary>Lo que esta siembra dejó, en el único Tenant que lo tiene; null si ningún Tenant tiene nada suyo.</summary>
    private static async Task<Conjunto?> LeerConjuntoAsync(ArnesDeArranqueRuntime arnes)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var db = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var tenants = await db.Tenants.AsNoTracking().Select(t => t.Id).ToListAsync();

        Conjunto? encontrado = null;
        foreach (var tenantId in tenants)
        {
            using (AmbitoTenantExplicito.Establecer(tenantId))
            {
                var todas = await db.Conversaciones.AsNoTracking().ToListAsync();
                var suyas = todas.Where(x =>
                    (x.HiloExternoId != null && x.HiloExternoId.StartsWith(MarcaCorreo, StringComparison.Ordinal))
                    || (x.TelefonoContacto != null && x.TelefonoContacto.StartsWith(MarcaWhatsApp, StringComparison.Ordinal))).ToList();
                var conexiones = await db.ConexionesIntegracion.AsNoTracking()
                    .Where(x => x.BuzonEmail.EndsWith("@buzon-simulado.example") || x.BuzonEmail == "+34600000101").ToListAsync();
                if (suyas.Count == 0 && conexiones.Count == 0)
                    continue;

                encontrado.Should().BeNull("la siembra solo escribe en el Tenant de la demo, no en dos");
                var ids = suyas.Select(x => x.Id).ToList();
                var conexionIds = conexiones.Select(x => x.Id).ToList();
                var mensajes = await db.Mensajes.AsNoTracking().Where(m => ids.Contains(m.ConversacionId)).ToListAsync();
                var mensajeIds = mensajes.Select(m => m.Id).ToList();
                encontrado = new Conjunto(
                    tenantId,
                    suyas,
                    mensajes,
                    await db.AdjuntosMensaje.AsNoTracking().Where(a => mensajeIds.Contains(a.MensajeId)).ToListAsync(),
                    conexiones,
                    await db.NotasInternasConversacion.AsNoTracking().CountAsync(n => ids.Contains(n.ConversacionId)),
                    await db.ReclamacionesDocumentales.AsNoTracking().CountAsync(r => r.ConversacionId != null && ids.Contains(r.ConversacionId.Value)),
                    await db.CredencialesIntegracion.AsNoTracking().CountAsync(k => conexionIds.Contains(k.ConexionIntegracionId)),
                    todas.Count - suyas.Count);
            }
        }

        return encontrado;
    }
}
