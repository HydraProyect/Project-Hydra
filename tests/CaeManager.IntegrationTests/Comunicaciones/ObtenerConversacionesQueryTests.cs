using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// H2 (Project-Hydra-Negocio/tecnico/docs/COMUNICACIONES.md § 9, "sin búsqueda de texto en conversaciones")
/// y "riesgo de escalabilidad" (bandeja agrupada sin paginación visible) — la
/// bandeja cargaba TODAS las conversaciones que cumplieran los filtros en
/// cada visita, y la búsqueda solo comparaba el asunto.
/// </summary>
public class ObtenerConversacionesQueryTests : IAsyncLifetime
{
    [Fact]
    public async Task Fuera_de_SLA_filtra_antes_de_paginar_y_excluye_las_no_vencidas()
    {
        await using var contexto = CrearContexto();
        for (var i = 0; i < 22; i++)
        {
            var conversacion = new Conversacion($"Vencida {i}");
            conversacion.CambiarPrioridad(PrioridadConversacion.Critica);
            conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@ejemplo.test", "Pendiente",
                DateTime.UtcNow.AddDays(-14).AddMinutes(i));
            contexto.Conversaciones.Add(conversacion);
        }
        var futura = new Conversacion("Todavía en plazo");
        futura.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@ejemplo.test", "Pendiente", DateTime.UtcNow.AddDays(30));
        contexto.Conversaciones.Add(futura);
        await contexto.SaveChangesAsync();
        var plazos = new Application.Comunicaciones.PlazosConversacionService(new OperacionSlaFalsa(),
            Microsoft.Extensions.Options.Options.Create(new Application.Comunicaciones.CalendariosOperadorCaeOptions()));
        var handler = new ObtenerConversacionesQueryHandler(contexto, contexto, contexto, new AlcanceDatosServiceFalso(),
            new CurrentUserServiceFalso(rol: "GestorCae"), plazos);

        var primera = await handler.Handle(new(SoloFueraDeSla: true), CancellationToken.None);
        var segunda = await handler.Handle(new(SoloFueraDeSla: true, Pagina: 2), CancellationToken.None);

        primera.TotalElementos.Should().Be(22);
        primera.Elementos.Should().HaveCount(20).And.OnlyContain(c => c.VencimientoSlaUtc < DateTime.UtcNow);
        segunda.Elementos.Should().HaveCount(2);
        primera.Elementos.Concat(segunda.Elementos).Select(c => c.Id).Should().OnlyHaveUniqueItems().And.NotContain(futura.Id);
    }

    private sealed class OperacionSlaFalsa : Application.Comunicaciones.IContextoOperativoConversacionService
    {
        public Task<Application.Comunicaciones.OperacionDeConversacion?> ResolverAsync(Guid propietarioTenantId, Guid? clienteEmpresarialId,
            Guid? empresaId, CancellationToken cancellationToken) =>
            Task.FromResult<Application.Comunicaciones.OperacionDeConversacion?>(new(propietarioTenantId, Guid.NewGuid()));
        public Task AvisarCriticaAsync(Application.Comunicaciones.OperacionDeConversacion operacion, Guid eventoId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Una lectura de SLA nunca genera avisos.");
    }

    [Fact]
    public async Task Un_hilo_cerrado_permite_una_continuacion_y_el_repositorio_solo_devuelve_la_nueva()
    {
        await using var contexto = CrearContexto();
        var conexion = new ConexionIntegracion("buzon@ejemplo.test", "Buzón CAE");
        contexto.ConexionesIntegracion.Add(conexion);
        var anterior = new Conversacion("Anterior");
        anterior.AsociarConexion(conexion.Id, "hilo-cerrado");
        anterior.CambiarEstado(EstadoConversacion.Cerrada);
        contexto.Conversaciones.Add(anterior);
        await contexto.SaveChangesAsync();
        var repositorio = new CaeManager.Infrastructure.Persistence.Repositories.ConversacionRepository(contexto);
        (await repositorio.ObtenerPorHiloExternoAsync(conexion.Id, "hilo-cerrado")).Should().BeNull();
        (await repositorio.ObtenerUltimaCerradaPorHiloExternoAsync(conexion.Id, "hilo-cerrado"))!.Id.Should().Be(anterior.Id);

        var nueva = new Conversacion("Continuación");
        nueva.AsociarConexion(conexion.Id, "hilo-cerrado");
        nueva.EnlazarConversacionAnterior(anterior);
        contexto.Conversaciones.Add(nueva);
        await contexto.SaveChangesAsync();
        contexto.ChangeTracker.Clear();

        var encontrada = await repositorio.ObtenerPorHiloExternoAsync(conexion.Id, "hilo-cerrado");
        encontrada!.Id.Should().Be(nueva.Id);
        encontrada.ConversacionAnteriorId.Should().Be(anterior.Id);
    }

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Esperando_respuesta_externa_se_filtra_por_estado_sin_incluir_terceros_o_resueltas()
    {
        await using (var contexto = CrearContexto())
        {
            foreach (var estado in new[] { EstadoConversacion.EsperandoRespuestaExterna,
                         EstadoConversacion.EnEsperaDeTercero, EstadoConversacion.Resuelta, EstadoConversacion.Cerrada })
            {
                var conversacion = new Conversacion(estado.ToString());
                conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@ejemplo.test", "Texto");
                conversacion.CambiarEstado(estado);
                contexto.Conversaciones.Add(conversacion);
            }
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync(new ObtenerConversacionesQuery(SoloEsperandoCliente: true));

        resultado.TotalElementos.Should().Be(1);
        resultado.Elementos.Should().ContainSingle().Which.Estado.Should().Be(EstadoConversacion.EsperandoRespuestaExterna);
    }

    [Fact]
    public async Task La_busqueda_encuentra_una_conversacion_por_el_cuerpo_de_un_mensaje_aunque_el_asunto_no_coincida()
    {
        await using (var contexto = CrearContexto())
        {
            var conversacion = new Conversacion("Documentación pendiente");
            conversacion.AgregarMensaje(
                DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@externo.test",
                "<p>Os adjuntamos el certificado de formación en riesgos laborales renovado.</p>");
            contexto.Conversaciones.Add(conversacion);
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync(new ObtenerConversacionesQuery(Busqueda: "riesgos laborales"));

        resultado.TotalElementos.Should().Be(1);
    }

    [Fact]
    public async Task La_busqueda_sigue_encontrando_por_asunto()
    {
        await using (var contexto = CrearContexto())
        {
            var conversacion = new Conversacion("Certificado de formación pendiente");
            conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@externo.test", "<p>Sin nada relevante en el cuerpo.</p>");
            contexto.Conversaciones.Add(conversacion);
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync(new ObtenerConversacionesQuery(Busqueda: "Certificado"));

        resultado.TotalElementos.Should().Be(1);
    }

    [Fact]
    public async Task Paginacion_devuelve_el_total_real_y_solo_el_tamano_de_pagina_pedido()
    {
        await using (var contexto = CrearContexto())
        {
            for (var i = 0; i < 25; i++)
            {
                var conversacion = new Conversacion($"Conversación {i:D2}");
                conversacion.AgregarMensaje(
                    DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@externo.test", "<p>Cuerpo</p>",
                    fechaUtc: DateTime.UtcNow.AddMinutes(-i));
                contexto.Conversaciones.Add(conversacion);
            }
            await contexto.SaveChangesAsync();
        }

        var primeraPagina = await EjecutarAsync(new ObtenerConversacionesQuery(Pagina: 1, TamanoPagina: 20));
        var segundaPagina = await EjecutarAsync(new ObtenerConversacionesQuery(Pagina: 2, TamanoPagina: 20));

        primeraPagina.TotalElementos.Should().Be(25);
        primeraPagina.Elementos.Should().HaveCount(20);
        primeraPagina.TotalPaginas.Should().Be(2);

        segundaPagina.TotalElementos.Should().Be(25);
        segundaPagina.Elementos.Should().HaveCount(5);

        // Sin solapes ni huecos entre páginas — mismo orden (más reciente
        // primero) que ve el gestor en la bandeja.
        primeraPagina.Elementos.Select(c => c.Id).Should().NotIntersectWith(segundaPagina.Elementos.Select(c => c.Id));
    }

    [Fact]
    public async Task Esperando_cliente_es_filtro_de_servidor_y_no_se_pierde_al_paginar()
    {
        // La reclasificación a filtro de servidor existe exactamente para
        // este caso: si el único hilo que cumple "esperando cliente" queda
        // en una página distinta de la 1, filtrar en memoria sobre una sola
        // página lo habría dejado invisible.
        await using (var contexto = CrearContexto())
        {
            // 19 conversaciones "de relleno" con último mensaje ENTRANTE (no
            // esperan al cliente) — llenan la página 1 completa (tamaño 20).
            for (var i = 0; i < 19; i++)
            {
                var relleno = new Conversacion($"Relleno {i:D2}");
                relleno.AgregarMensaje(
                    DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@externo.test", "<p>Entrante</p>",
                    fechaUtc: DateTime.UtcNow.AddMinutes(-i));
                contexto.Conversaciones.Add(relleno);
            }

            // La única "esperando cliente" de verdad: Abierta, último mensaje
            // SALIENTE, con fecha más antigua para que quede en la página 2.
            var esperando = new Conversacion("Reclamación sin contestar");
            esperando.AgregarMensaje(
                DireccionMensaje.Saliente, CanalConversacion.Correo, "cae@buzon.local", "<p>Reclamamos</p>",
                fechaUtc: DateTime.UtcNow.AddDays(-10));
            contexto.Conversaciones.Add(esperando);

            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync(new ObtenerConversacionesQuery(SoloEsperandoCliente: true, Pagina: 1, TamanoPagina: 20));

        resultado.TotalElementos.Should().Be(1);
        resultado.Elementos.Should().ContainSingle().Which.Asunto.Should().Be("Reclamación sin contestar");
    }

    [Fact]
    public async Task Un_hilo_del_buzon_personal_de_otro_gestor_no_aparece_en_la_bandeja_general()
    {
        // Regresión: el buzón personal de un gestor (ConexionIntegracion.
        // GestorPropietarioId) también tiene ClienteId null, igual que la cola
        // de triage genuina — sin excluirlo, su correspondencia se colaba en
        // la bandeja de cualquier otro gestor con acceso a Comunicaciones.
        Guid conversacionTriageId;
        await using (var contexto = CrearContexto())
        {
            var conexionPersonal = new ConexionIntegracion(
                "gestor.otro@ejemplo.local", "Buzón personal", gestorPropietarioId: Guid.NewGuid());
            contexto.ConexionesIntegracion.Add(conexionPersonal);
            await contexto.SaveChangesAsync();

            var deBuzonPersonal = new Conversacion("Asunto personal del otro gestor");
            deBuzonPersonal.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "quien-sea@externo.test", "<p>Privado</p>");
            deBuzonPersonal.AsociarConexion(conexionPersonal.Id, "hilo-personal-1");
            contexto.Conversaciones.Add(deBuzonPersonal);

            var deTriageGenuina = new Conversacion("Correo sin triar de verdad");
            deTriageGenuina.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@externo.test", "<p>Triage</p>");
            contexto.Conversaciones.Add(deTriageGenuina);

            await contexto.SaveChangesAsync();
            conversacionTriageId = deTriageGenuina.Id;
        }

        // Usuario actual sin Id conocido (equivalente a "no soy el dueño de
        // ningún buzón personal") — el caso más común: cualquier gestor que
        // no sea el propietario de esa conexión concreta.
        var resultado = await EjecutarAsync(new ObtenerConversacionesQuery());

        resultado.Elementos.Should().ContainSingle().Which.Id.Should().Be(conversacionTriageId);
    }

    [Fact]
    public async Task El_propio_dueno_del_buzon_personal_si_ve_su_hilo()
    {
        var gestorPropietarioId = Guid.NewGuid();
        Guid conversacionPersonalId;
        await using (var contexto = CrearContexto())
        {
            var conexionPersonal = new ConexionIntegracion(
                "gestor.propietario@ejemplo.local", "Buzón personal", gestorPropietarioId: gestorPropietarioId);
            contexto.ConexionesIntegracion.Add(conexionPersonal);
            await contexto.SaveChangesAsync();

            var deBuzonPersonal = new Conversacion("Asunto personal propio");
            deBuzonPersonal.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "quien-sea@externo.test", "<p>Privado</p>");
            deBuzonPersonal.AsociarConexion(conexionPersonal.Id, "hilo-personal-2");
            contexto.Conversaciones.Add(deBuzonPersonal);

            await contexto.SaveChangesAsync();
            conversacionPersonalId = deBuzonPersonal.Id;
        }

        await using var contextoLectura = CrearContexto();
        var handler = new ObtenerConversacionesQueryHandler(
            contextoLectura, contextoLectura, contextoLectura, new AlcanceDatosServiceFalso(),
            new CurrentUserServiceFalso(usuarioId: gestorPropietarioId, rol: "GestorCae"));

        var resultado = await handler.Handle(new ObtenerConversacionesQuery(), CancellationToken.None);

        resultado.Elementos.Should().ContainSingle().Which.Id.Should().Be(conversacionPersonalId);
    }

    private async Task<Application.Common.ResultadoPaginado<ConversacionListaDto>> EjecutarAsync(ObtenerConversacionesQuery query)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerConversacionesQueryHandler(
            contexto, contexto, contexto, new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(rol: "GestorCae"));

        return await handler.Handle(query, CancellationToken.None);
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
