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
/// H2 (docs/COMUNICACIONES.md § 9, "sin búsqueda de texto en conversaciones")
/// y "riesgo de escalabilidad" (bandeja agrupada sin paginación visible) — la
/// bandeja cargaba TODAS las conversaciones que cumplieran los filtros en
/// cada visita, y la búsqueda solo comparaba el asunto.
/// </summary>
public class ObtenerConversacionesQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

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
