using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
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
        var handler = new ObtenerConversacionesQueryHandler(contexto, contexto, contexto, contexto, contexto, contexto, new AlcanceDatosServiceFalso(),
            new CurrentUserServiceFalso(rol: "GestorCae"), new TenantActualAmbiental { TenantId = _tenant }, plazos);

        var primera = await handler.Handle(new(SoloFueraDeSla: true), CancellationToken.None);
        var segunda = await handler.Handle(new(SoloFueraDeSla: true, Pagina: 2), CancellationToken.None);

        primera.TotalElementos.Should().Be(22);
        primera.Elementos.Should().HaveCount(20).And.OnlyContain(c => c.VencimientoSlaUtc < DateTime.UtcNow);
        segunda.Elementos.Should().HaveCount(2);
        primera.Elementos.Concat(segunda.Elementos).Select(c => c.Id).Should().OnlyHaveUniqueItems().And.NotContain(futura.Id);
    }

    [Fact]
    public async Task Fuera_de_SLA_no_toma_el_acuse_automatico_por_una_respuesta()
    {
        await using var contexto = CrearContexto();
        var inicio = DateTime.UtcNow.AddDays(-14);
        var conAcuse = new Conversacion("Con acuse");
        conAcuse.CambiarPrioridad(PrioridadConversacion.Critica);
        conAcuse.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@ejemplo.test", "Pregunta", inicio);
        conAcuse.AgregarMensaje(DireccionMensaje.Saliente, CanalConversacion.Correo, "cae@ejemplo.test", "Acuse", inicio.AddMinutes(1), esAutomatico: true);
        contexto.Conversaciones.Add(conAcuse);
        var respondida = new Conversacion("Respondida por una persona");
        respondida.CambiarPrioridad(PrioridadConversacion.Critica);
        respondida.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@ejemplo.test", "Pregunta", inicio);
        respondida.AgregarMensaje(DireccionMensaje.Saliente, CanalConversacion.Correo, "cae@ejemplo.test", "Respuesta", inicio.AddMinutes(1));
        contexto.Conversaciones.Add(respondida);
        await contexto.SaveChangesAsync();
        var plazos = new Application.Comunicaciones.PlazosConversacionService(new OperacionSlaFalsa(),
            Microsoft.Extensions.Options.Options.Create(new Application.Comunicaciones.CalendariosOperadorCaeOptions()));
        var handler = new ObtenerConversacionesQueryHandler(contexto, contexto, contexto, contexto, contexto, contexto, new AlcanceDatosServiceFalso(),
            new CurrentUserServiceFalso(rol: "GestorCae"), new TenantActualAmbiental { TenantId = _tenant }, plazos);

        var resultado = await handler.Handle(new(SoloFueraDeSla: true), CancellationToken.None);

        resultado.Elementos.Should().ContainSingle().Which.Id.Should().Be(conAcuse.Id);
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
        encontrada.NumeroTicket.Should().Be(anterior.NumeroTicket + 1, "la continuación tras el cierre es un ticket nuevo");
    }

    [Fact]
    public async Task La_bandeja_devuelve_el_numero_de_ticket_de_cada_conversacion()
    {
        await using var contexto = CrearContexto();
        var conversacion = new Conversacion("Con número");
        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();
        var handler = new ObtenerConversacionesQueryHandler(contexto, contexto, contexto, contexto, contexto, contexto, new AlcanceDatosServiceFalso(),
            new CurrentUserServiceFalso(rol: "GestorCae"), new TenantActualAmbiental { TenantId = _tenant });

        var resultado = await handler.Handle(new(), CancellationToken.None);

        resultado.Elementos.Should().ContainSingle().Which.NumeroTicket.Should().Be(1);
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
    public async Task El_acuse_automatico_no_hace_que_la_conversacion_parezca_respondida()
    {
        await using (var contexto = CrearContexto())
        {
            var conversacion = new Conversacion("Con acuse");
            var inicio = DateTime.UtcNow.AddMinutes(-5);
            conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@ejemplo.test", "Pregunta del contacto", inicio);
            conversacion.AgregarMensaje(DireccionMensaje.Saliente, CanalConversacion.Correo, "cae@ejemplo.test", "Acuse", inicio.AddMinutes(1), esAutomatico: true);
            contexto.Conversaciones.Add(conversacion);

            var respondida = new Conversacion("Respondida por una persona");
            respondida.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@ejemplo.test", "Pregunta", inicio);
            respondida.AgregarMensaje(DireccionMensaje.Saliente, CanalConversacion.Correo, "cae@ejemplo.test", "Respuesta", inicio.AddMinutes(1));
            contexto.Conversaciones.Add(respondida);
            await contexto.SaveChangesAsync();
        }

        var esperando = await EjecutarAsync(new ObtenerConversacionesQuery(SoloEsperandoCliente: true));
        var bandeja = await EjecutarAsync(new ObtenerConversacionesQuery());

        esperando.Elementos.Should().ContainSingle().Which.Asunto.Should().Be("Respondida por una persona");
        var conAcuse = bandeja.Elementos.Should().ContainSingle(c => c.Asunto == "Con acuse").Subject;
        conAcuse.PreviewUltimoMensaje.Should().Contain("Pregunta del contacto");
        conAcuse.UltimoMensajeDireccion.Should().Be(DireccionMensaje.Entrante);
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
    public async Task La_busqueda_encuentra_una_conversacion_por_su_etiqueta_aunque_ni_el_asunto_ni_los_mensajes_coincidan()
    {
        await using (var contexto = CrearContexto())
        {
            var etiquetada = new Conversacion("Asunto sin relación", etiquetas: "Centro Logístico Sevilla, visita anual");
            etiquetada.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@externo.test", "<p>Cuerpo sin relación.</p>");
            var sinEtiquetas = new Conversacion("Otro asunto");
            sinEtiquetas.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@externo.test", "<p>Otro cuerpo.</p>");
            contexto.Conversaciones.AddRange(etiquetada, sinEtiquetas);
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync(new ObtenerConversacionesQuery(Busqueda: "visita anual"));

        resultado.Elementos.Should().ContainSingle().Which.Asunto.Should().Be("Asunto sin relación");
    }

    [Fact]
    public async Task La_busqueda_por_etiqueta_no_devuelve_una_conversacion_que_la_visibilidad_no_deja_ver()
    {
        // Regresión de visibilidad: la etiqueta se busca dentro del conjunto ya acotado, no abre nada nuevo. Con el
        // triaje cerrado, la conversación sin ancla no sale aunque lleve la etiqueta buscada; la anclada al Cliente
        // propio, con la misma etiqueta, sí.
        Guid clientePropioId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Contrata Propia S.L.", "B12345674", false, null, null);
            contexto.Empresas.Add(cliente);
            var anclada = new Conversacion("Anclada al Cliente propio", clienteId: cliente.Id, etiquetas: "Centro Norte");
            anclada.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "yo@propia.test", "<p>Hola</p>");
            var sinTriar = new Conversacion("Sin triar", etiquetas: "Centro Norte");
            sinTriar.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "otro@ajena.test", "<p>Datos de otra empresa</p>");
            contexto.Conversaciones.AddRange(anclada, sinTriar);
            await contexto.SaveChangesAsync();
            clientePropioId = cliente.Id;
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerConversacionesQueryHandler(
            lectura, lectura, lectura, lectura, lectura, lectura, new AlcanceDatosServiceFalso(clienteIds: [clientePropioId], triajeVisible: false),
            new CurrentUserServiceFalso(rol: "GestorCae"), new TenantActualAmbiental { TenantId = _tenant });

        var resultado = await handler.Handle(new ObtenerConversacionesQuery(Busqueda: "centro norte"), CancellationToken.None);

        resultado.Elementos.Should().ContainSingle().Which.ClienteId.Should().Be(clientePropioId);
    }

    // ---- Buscar por Centro: los cinco vínculos que ya existen en el esquema, sin migración. ----

    [Fact]
    public async Task Buscar_por_Centro_encuentra_el_hilo_que_tiene_al_Centro_de_participante_aunque_el_texto_no_lo_nombre()
    {
        var (_, _, centros) = await CrearCentrosAsync(_tenant, "B12345674", "B87654323", "Planta Mercadona Sevilla", "Planta Lidl Bilbao");
        await using (var contexto = CrearContexto())
        {
            var (ligada, _) = HiloSinTexto("Acceso de la semana que viene");
            ligada.AgregarParticipante("planta@centro.test", RolParticipante.Cc, TipoParticipanteOrigen.Centro, centros[0]);
            // Mismo identificador, pero como Trabajador: el vínculo exige que el participante sea un Centro.
            var (comoTrabajador, _) = HiloSinTexto("Otro asunto");
            comoTrabajador.AgregarParticipante("persona@externo.test", RolParticipante.Cc, TipoParticipanteOrigen.Trabajador, centros[0]);
            var (sinVinculo, _) = HiloSinTexto("Sin vínculo");
            // Ligado por el mismo vínculo a otro Centro: el nombre buscado lo deja fuera.
            var (deOtroCentro, _) = HiloSinTexto("Participa otro Centro");
            deOtroCentro.AgregarParticipante("planta@lidl.test", RolParticipante.Cc, TipoParticipanteOrigen.Centro, centros[1]);
            contexto.Conversaciones.AddRange(ligada, comoTrabajador, sinVinculo, deOtroCentro);
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync(new ObtenerConversacionesQuery(Busqueda: "mercadona sev"));

        resultado.Elementos.Should().ContainSingle().Which.Asunto.Should().Be("Acceso de la semana que viene");
    }

    [Fact]
    public async Task Buscar_por_Centro_encuentra_por_la_intencion_confirmada_y_no_por_la_propuesta_sin_confirmar()
    {
        var (_, _, centros) = await CrearCentrosAsync(_tenant, "B12345674", "B87654323", "Nave Logística Zaragoza", "Nave Frío Valencia");
        await using (var contexto = CrearContexto())
        {
            var (confirmada, mensajeConfirmada) = HiloSinTexto("Intención confirmada");
            var (propuesta, mensajePropuesta) = HiloSinTexto("Intención solo propuesta por la IA");
            contexto.Conversaciones.AddRange(confirmada, propuesta);
            var intencionConfirmada = new IntencionConversacion(
                confirmada.Id, mensajeConfirmada.Id, IntencionCae.CoordinacionAcceso, ConfianzaIntencion.Alta,
                TipoEntidadIntencion.Centro, centros[0]);
            intencionConfirmada.Confirmar(IntencionCae.CoordinacionAcceso);
            var intencionSinConfirmar = new IntencionConversacion(
                propuesta.Id, mensajePropuesta.Id, IntencionCae.CoordinacionAcceso, ConfianzaIntencion.Alta,
                TipoEntidadIntencion.Centro, centros[0]);
            var (deOtroCentro, mensajeOtroCentro) = HiloSinTexto("Intención confirmada sobre otro Centro");
            contexto.Conversaciones.Add(deOtroCentro);
            var intencionOtroCentro = new IntencionConversacion(
                deOtroCentro.Id, mensajeOtroCentro.Id, IntencionCae.CoordinacionAcceso, ConfianzaIntencion.Alta,
                TipoEntidadIntencion.Centro, centros[1]);
            intencionOtroCentro.Confirmar(IntencionCae.CoordinacionAcceso);
            // Confirmada, con el mismo identificador, pero de otro tipo de entidad (Empresa): no es un Centro.
            var (deOtroTipo, mensajeOtroTipo) = HiloSinTexto("Intención confirmada sobre una Empresa");
            contexto.Conversaciones.Add(deOtroTipo);
            var intencionOtroTipo = new IntencionConversacion(
                deOtroTipo.Id, mensajeOtroTipo.Id, IntencionCae.CoordinacionAcceso, ConfianzaIntencion.Alta,
                TipoEntidadIntencion.Empresa, centros[0]);
            intencionOtroTipo.Confirmar(IntencionCae.CoordinacionAcceso);
            contexto.IntencionesConversacion.AddRange(intencionConfirmada, intencionSinConfirmar, intencionOtroCentro, intencionOtroTipo);
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync(new ObtenerConversacionesQuery(Busqueda: "logistica zaragoza"));

        resultado.Elementos.Should().ContainSingle().Which.Asunto.Should().Be("Intención confirmada");
    }

    [Fact]
    public async Task Buscar_por_Centro_encuentra_por_la_Gestion_nacida_de_un_mensaje_del_hilo_y_ni_la_manual_ni_la_de_otro_hilo_ligan()
    {
        var (_, empresaId, centros) = await CrearCentrosAsync(_tenant, "B12345674", "B87654323", "Obra Hospital Norte", "Obra Colegio Sur");
        await using (var contexto = CrearContexto())
        {
            var trabajador = Trabajador.DeEmpresa(empresaId, "Primer", "Técnico Prueba", "77189989B");
            var tipo = new TipoDocumento("Autorización uso de maquinaria", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
            contexto.Trabajadores.Add(trabajador);
            contexto.TiposDocumento.Add(tipo);
            await contexto.SaveChangesAsync();

            var (conGestion, mensaje) = HiloSinTexto("Renovación de EPI");
            var (sinGestion, _) = HiloSinTexto("Sin gestión");
            contexto.Conversaciones.AddRange(conGestion, sinGestion);
            // La del hilo, la creada a mano (sin mensaje de origen) y una con el mismo Centro pero de un mensaje que
            // no es de ninguna conversación: solo la primera liga el hilo con el Centro.
            var (deOtroCentro, mensajeOtroCentro) = HiloSinTexto("Gestión en otro Centro");
            contexto.Conversaciones.Add(deOtroCentro);
            contexto.Gestiones.AddRange(
                new Gestion(trabajador.Id, centros[1], tipo.Id, mensajeOrigenId: mensajeOtroCentro.Id),
                new Gestion(trabajador.Id, centros[0], tipo.Id, mensajeOrigenId: mensaje.Id),
                new Gestion(trabajador.Id, centros[0], tipo.Id),
                new Gestion(trabajador.Id, centros[0], tipo.Id, mensajeOrigenId: Guid.NewGuid()));
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync(new ObtenerConversacionesQuery(Busqueda: "hospital norte"));

        resultado.Elementos.Should().ContainSingle().Which.Asunto.Should().Be("Renovación de EPI");
    }

    [Fact]
    public async Task Buscar_por_Centro_encuentra_por_la_sugerencia_de_visita_aceptada_tal_cual_y_no_por_las_demas()
    {
        var (_, _, centros) = await CrearCentrosAsync(_tenant, "B12345674", "B87654323", "Fábrica Cartuja", "Fábrica Pisa");
        await using (var contexto = CrearContexto())
        {
            var estados = new (string Asunto, ResolucionSugerencia? Resolucion)[]
            {
                ("Aceptada tal cual", ResolucionSugerencia.Confirmada),
                ("Aceptada con edición", ResolucionSugerencia.ConfirmadaConEdicion),
                ("Descartada", ResolucionSugerencia.Descartada),
                ("Pendiente", null),
            };
            foreach (var (asunto, resolucion) in estados)
            {
                var (hilo, mensaje) = HiloSinTexto(asunto);
                contexto.Conversaciones.Add(hilo);
                var sugerencia = new SugerenciaVisitaCorreo(mensaje.Id, centros[0], null, null, "Pide agendar una entrada.", 90, 90, 90);
                if (resolucion is { } r) sugerencia.Resolver(r);
                contexto.SugerenciasVisitaCorreo.Add(sugerencia);
            }
            // Aceptada tal cual, pero sobre otro Centro: el nombre buscado la deja fuera.
            var (deOtroCentro, mensajeOtroCentro) = HiloSinTexto("Aceptada sobre otro Centro");
            contexto.Conversaciones.Add(deOtroCentro);
            var sugerenciaOtroCentro = new SugerenciaVisitaCorreo(mensajeOtroCentro.Id, centros[1], null, null, "Pide agendar una entrada.", 90, 90, 90);
            sugerenciaOtroCentro.Resolver(ResolucionSugerencia.Confirmada);
            contexto.SugerenciasVisitaCorreo.Add(sugerenciaOtroCentro);
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync(new ObtenerConversacionesQuery(Busqueda: "cartuja"));

        resultado.Elementos.Should().ContainSingle().Which.Asunto.Should().Be("Aceptada tal cual");
    }

    [Fact]
    public async Task Buscar_por_Centro_encuentra_por_la_Visita_creada_desde_el_hilo_en_el_Centro_real_de_la_Visita()
    {
        var (_, _, centros) = await CrearCentrosAsync(_tenant, "B12345674", "B87654323", "Polígono Sur Granada", "Centro Corregido Málaga");
        await using (var contexto = CrearContexto())
        {
            var hoy = DiaDeNegocio.Hoy();
            var visita = new Visita(centros[1], hoy, hoy, null);
            contexto.Visitas.Add(visita);
            var (conVisita, mensaje) = HiloSinTexto("Entrada de operarios");
            var (sinVisita, _) = HiloSinTexto("Sin visita");
            contexto.Conversaciones.AddRange(conVisita, sinVisita);
            contexto.EventosConversacion.Add(new EventoConversacion(conVisita.Id, TipoEventoConversacion.VisitaCreada, visita.Id, DateTime.UtcNow));
            // Un evento de otro tipo cuyo ReferenciaId es la misma Visita no liga: solo VisitaCreada lo hace.
            contexto.EventosConversacion.Add(new EventoConversacion(sinVisita.Id, TipoEventoConversacion.DocumentoActualizado, visita.Id, DateTime.UtcNow));
            // La IA propuso otro Centro (Granada) y el Gestor lo corrigió al crear la Visita (Málaga): manda el Centro de
            // la Visita, no el propuesto.
            var sugerencia = new SugerenciaVisitaCorreo(mensaje.Id, centros[0], null, null, "Pide agendar una entrada.", 90, 90, 90);
            sugerencia.Resolver(ResolucionSugerencia.ConfirmadaConEdicion);
            contexto.SugerenciasVisitaCorreo.Add(sugerencia);
            await contexto.SaveChangesAsync();
        }

        var porCentroReal = await EjecutarAsync(new ObtenerConversacionesQuery(Busqueda: "corregido málaga"));
        var porCentroPropuesto = await EjecutarAsync(new ObtenerConversacionesQuery(Busqueda: "polígono sur"));

        porCentroReal.Elementos.Should().ContainSingle().Which.Asunto.Should().Be("Entrada de operarios");
        porCentroPropuesto.Elementos.Should().BeEmpty("la sugerencia aceptada con edición no dice qué Centro quedó");
    }

    [Fact]
    public async Task Buscar_por_Centro_no_devuelve_una_conversacion_ligada_que_la_visibilidad_no_deja_ver()
    {
        // La visibilidad va primero: con el triaje cerrado, el hilo ligado al Centro pero sin ancla de Cliente no
        // sale; el ligado al mismo Centro y anclado al Cliente propio, sí.
        var (clienteId, _, centros) = await CrearCentrosAsync(_tenant, "B12345674", "B87654323", "Planta Visible Valencia");
        await using (var contexto = CrearContexto())
        {
            var anclada = new Conversacion("Anclada al Cliente propio", clienteId: clienteId);
            anclada.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "yo@propia.test", "<p>Hola</p>");
            anclada.AgregarParticipante("planta@centro.test", RolParticipante.Cc, TipoParticipanteOrigen.Centro, centros[0]);
            var sinTriar = new Conversacion("Sin triar");
            sinTriar.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "otro@ajena.test", "<p>Datos de otra empresa</p>");
            sinTriar.AgregarParticipante("planta@centro.test", RolParticipante.Cc, TipoParticipanteOrigen.Centro, centros[0]);
            contexto.Conversaciones.AddRange(anclada, sinTriar);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerConversacionesQueryHandler(
            lectura, lectura, lectura, lectura, lectura, lectura,
            new AlcanceDatosServiceFalso(clienteIds: [clienteId], triajeVisible: false),
            new CurrentUserServiceFalso(rol: "GestorCae"), new TenantActualAmbiental { TenantId = _tenant });

        var resultado = await handler.Handle(new ObtenerConversacionesQuery(Busqueda: "planta visible"), CancellationToken.None);

        resultado.Elementos.Should().ContainSingle().Which.Asunto.Should().Be("Anclada al Cliente propio");
    }

    [Fact]
    public async Task Buscar_por_Centro_no_liga_con_un_Centro_de_otro_Tenant_aunque_el_hilo_apunte_a_su_identificador()
    {
        var otroTenant = Guid.NewGuid();
        var (_, _, propios) = await CrearCentrosAsync(_tenant, "B12345674", "B87654323", "Almacén Propio Bilbao");
        var (_, _, ajenos) = await CrearCentrosAsync(otroTenant, "B10380202", "B10380228", "Almacén Ajeno Vigo");
        await using (var contexto = CrearContexto())
        {
            var (aPropio, _) = HiloSinTexto("Apunta al Centro propio");
            aPropio.AgregarParticipante("planta@centro.test", RolParticipante.Cc, TipoParticipanteOrigen.Centro, propios[0]);
            var (aAjeno, _) = HiloSinTexto("Apunta al Centro de otro Tenant");
            aAjeno.AgregarParticipante("planta@centro.test", RolParticipante.Cc, TipoParticipanteOrigen.Centro, ajenos[0]);
            contexto.Conversaciones.AddRange(aPropio, aAjeno);
            await contexto.SaveChangesAsync();
        }

        var porAjeno = await EjecutarAsync(new ObtenerConversacionesQuery(Busqueda: "ajeno vigo"));
        var porPropio = await EjecutarAsync(new ObtenerConversacionesQuery(Busqueda: "propio bilbao"));

        porAjeno.Elementos.Should().BeEmpty("el nombre de un Centro de otro Tenant no se lee desde este Tenant");
        porPropio.Elementos.Should().ContainSingle().Which.Asunto.Should().Be("Apunta al Centro propio");
    }

    [Fact]
    public async Task Buscar_por_Centro_no_revela_un_Centro_fuera_de_la_cartera_de_quien_busca()
    {
        // Las dos conversaciones se ven (triaje abierto), pero uno de sus Centros no está en la cartera de Centros de
        // quien busca: ese nombre no se puede usar para encontrar ni confirma que el Centro existe.
        var (_, _, centros) = await CrearCentrosAsync(_tenant, "B12345674", "B87654323", "Planta Cartera Dentro", "Planta Cartera Fuera");
        await using (var contexto = CrearContexto())
        {
            var (dentro, _) = HiloSinTexto("Hilo del Centro en cartera");
            dentro.AgregarParticipante("a@centro.test", RolParticipante.Cc, TipoParticipanteOrigen.Centro, centros[0]);
            var (fuera, _) = HiloSinTexto("Hilo del Centro fuera de cartera");
            fuera.AgregarParticipante("b@centro.test", RolParticipante.Cc, TipoParticipanteOrigen.Centro, centros[1]);
            contexto.Conversaciones.AddRange(dentro, fuera);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerConversacionesQueryHandler(
            lectura, lectura, lectura, lectura, lectura, lectura,
            new AlcanceDatosServiceFalso(centroIds: [centros[0]], triajeVisible: true),
            new CurrentUserServiceFalso(rol: "GestorCae"), new TenantActualAmbiental { TenantId = _tenant });

        var resultado = await handler.Handle(new ObtenerConversacionesQuery(Busqueda: "planta cartera"), CancellationToken.None);

        resultado.Elementos.Should().ContainSingle().Which.Asunto.Should().Be("Hilo del Centro en cartera");
    }

    private static (Conversacion Conversacion, Mensaje Mensaje) HiloSinTexto(string asunto)
    {
        var conversacion = new Conversacion(asunto);
        var mensaje = conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@externo.test", "<p>Texto que no nombra ningún lugar.</p>");
        return (conversacion, mensaje);
    }

    private async Task<(Guid ClienteId, Guid EmpresaId, Guid[] CentroIds)> CrearCentrosAsync(
        Guid tenant, string cifCliente, string cifEmpresa, params string[] nombres)
    {
        await using var contexto = CrearContexto(tenant);
        var cliente = Empresa.CrearComoCliente($"Cliente {cifCliente} S.L.", cifCliente, false, null, null);
        var empresa = new Empresa($"Empresa {cifEmpresa} S.L.", cifEmpresa);
        contexto.Empresas.AddRange(cliente, empresa);
        await contexto.SaveChangesAsync();
        var centros = nombres.Select(nombre => new Centro(cliente.Id, empresa.Id, nombre)).ToArray();
        contexto.Centros.AddRange(centros);
        await contexto.SaveChangesAsync();
        return (cliente.Id, empresa.Id, centros.Select(c => c.Id).ToArray());
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
            contextoLectura, contextoLectura, contextoLectura, contextoLectura, contextoLectura, contextoLectura, new AlcanceDatosServiceFalso(),
            new CurrentUserServiceFalso(usuarioId: gestorPropietarioId, rol: "GestorCae"),
            new TenantActualAmbiental { TenantId = _tenant });

        var resultado = await handler.Handle(new ObtenerConversacionesQuery(), CancellationToken.None);

        resultado.Elementos.Should().ContainSingle().Which.Id.Should().Be(conversacionPersonalId);
    }

    private async Task<Application.Common.ResultadoPaginado<ConversacionListaDto>> EjecutarAsync(ObtenerConversacionesQuery query)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerConversacionesQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(rol: "GestorCae"),
            new TenantActualAmbiental { TenantId = _tenant });

        return await handler.Handle(query, CancellationToken.None);
    }

    private CaeManagerDbContext CrearContexto() => CrearContexto(_tenant);

    private CaeManagerDbContext CrearContexto(Guid tenant)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
