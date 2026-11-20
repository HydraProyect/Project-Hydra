using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Gestiones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Visitas;
using FluentAssertions;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Datos que la fila del lienzo «Pulida: bandeja de la cartera» (L-12) necesita de la lista de Conversaciones: archivos
/// adjuntos, acción por aprobar, buzón por el que entró y persona asignada. Cada campo tiene su test, y el último grupo
/// fija que rellenarlos no cambia QUÉ filas salen (la visibilidad sigue siendo la de <c>ConversacionesVisibles</c>, R-3a):
/// ni se amplía el conjunto, ni se duplican filas por una unión con varios adjuntos, ni se leen los datos de una
/// conversación que la regla de visibilidad ya dejó fuera.
/// </summary>
public class ObtenerConversacionesDatosDeFilaTests
{
    [Fact]
    public async Task TotalAdjuntos_cuenta_los_archivos_de_todos_los_mensajes_del_hilo_y_es_cero_sin_ellos()
    {
        var banda = new Banda();
        var conAdjuntos = banda.Hilo("Con adjuntos");
        var segundoMensaje = banda.MensajeEn(conAdjuntos.ConversacionId);
        var sinAdjuntos = banda.Hilo("Sin adjuntos");
        banda.Adjunto(conAdjuntos, "a.pdf");
        banda.Adjunto(conAdjuntos, "b.pdf");
        banda.Adjunto(segundoMensaje, "c.pdf");

        var resultado = await banda.Lista();

        resultado.Single(c => c.Id == conAdjuntos.ConversacionId).TotalAdjuntos.Should().Be(3);
        resultado.Single(c => c.Id == sinAdjuntos.ConversacionId).TotalAdjuntos.Should().Be(0);
    }

    [Fact]
    public async Task TotalAdjuntos_cuenta_tambien_los_adjuntos_ya_resueltos()
    {
        // El clip dice que el hilo trae archivos, no cuántos faltan por repartir.
        var banda = new Banda();
        var hilo = banda.Hilo("Reparto en curso");
        banda.Adjunto(hilo, "pendiente.pdf");
        banda.Adjunto(hilo, "rechazado.pdf").Rechazar("No es de este Cliente empresarial", Guid.NewGuid(), DateTime.UtcNow);

        (await banda.Lista()).Single().TotalAdjuntos.Should().Be(2);
    }

    [Fact]
    public async Task AccionPorAprobar_es_true_con_una_sugerencia_de_Visita_pendiente_y_false_si_ya_se_resolvio()
    {
        var banda = new Banda();
        var pendiente = banda.Hilo("Visita pendiente");
        var resuelta = banda.Hilo("Visita resuelta");
        var sinPropuesta = banda.Hilo("Sin propuesta");
        banda.SugerenciaVisita(pendiente);
        banda.SugerenciaVisita(resuelta).Resolver(ResolucionSugerencia.Descartada);

        var resultado = await banda.Lista();

        resultado.Single(c => c.Id == pendiente.ConversacionId).AccionPorAprobar.Should().BeTrue();
        resultado.Single(c => c.Id == resuelta.ConversacionId).AccionPorAprobar.Should().BeFalse();
        resultado.Single(c => c.Id == sinPropuesta.ConversacionId).AccionPorAprobar.Should().BeFalse();
    }

    [Fact]
    public async Task AccionPorAprobar_es_true_con_un_detalle_de_sugerencia_de_Gestion_pendiente_y_false_si_ya_se_resolvio()
    {
        var banda = new Banda();
        var pendiente = banda.Hilo("Gestión pendiente");
        var resuelta = banda.Hilo("Gestión resuelta");
        banda.DetalleDeGestion(pendiente);
        banda.DetalleDeGestion(resuelta).Resolver(ResolucionSugerencia.Confirmada);

        var resultado = await banda.Lista();

        resultado.Single(c => c.Id == pendiente.ConversacionId).AccionPorAprobar.Should().BeTrue();
        resultado.Single(c => c.Id == resuelta.ConversacionId).AccionPorAprobar.Should().BeFalse();
    }

    [Fact]
    public async Task AccionPorAprobar_no_cuenta_una_propuesta_de_Gestion_que_es_ruido_salvo_que_el_Gestor_CAE_la_confirmara()
    {
        // Un detalle que repite algo ya reclamado cuenta como ruido en la misma fila; no puede ser a la vez «acción por aprobar».
        var banda = new Banda();
        var repeticion = banda.Hilo("Repetición de algo ya reclamado");
        var confirmada = banda.Hilo("Repetición que el Gestor CAE confirmó que importa");
        var mixta = banda.Hilo("Una repetición y una propuesta real");
        banda.Repeticion(banda.DetalleDeGestion(repeticion));
        banda.Repeticion(banda.DetalleDeGestion(confirmada)).ConfirmarManualmente();
        banda.Repeticion(banda.DetalleDeGestion(mixta));
        banda.DetalleDeGestion(mixta);

        var resultado = await banda.Lista();

        resultado.Single(c => c.Id == repeticion.ConversacionId).AccionPorAprobar.Should().BeFalse();
        resultado.Single(c => c.Id == repeticion.ConversacionId).MensajesRuido.Should().Be(1);
        resultado.Single(c => c.Id == confirmada.ConversacionId).AccionPorAprobar.Should().BeTrue();
        resultado.Single(c => c.Id == mixta.ConversacionId).AccionPorAprobar.Should().BeTrue();
    }

    [Fact]
    public async Task AccionPorAprobar_no_cuenta_una_sugerencia_de_Visita_en_un_mensaje_con_motivo_de_ruido_salvo_que_el_Gestor_CAE_lo_confirmara()
    {
        var banda = new Banda();
        var ruido = banda.Hilo("Resumen automático sin cambios");
        var confirmado = banda.Hilo("Resumen que el Gestor CAE confirmó que importa");
        var sinMotivo = banda.Hilo("Notificación automática sin motivo de ruido");
        banda.SugerenciaVisita(ruido);
        banda.SugerenciaVisita(confirmado);
        banda.SugerenciaVisita(sinMotivo);
        banda.MotivoRuido(ruido, MotivoRuidoMensaje.ResumenSinCambios);
        banda.MotivoRuido(confirmado, MotivoRuidoMensaje.ResumenSinCambios).ConfirmarManualmente();
        banda.MotivoRuido(sinMotivo, MotivoRuidoMensaje.Ninguno);

        var resultado = await banda.Lista();

        resultado.Single(c => c.Id == ruido.ConversacionId).AccionPorAprobar.Should().BeFalse();
        resultado.Single(c => c.Id == confirmado.ConversacionId).AccionPorAprobar.Should().BeTrue();
        resultado.Single(c => c.Id == sinMotivo.ConversacionId).AccionPorAprobar.Should().BeTrue();
    }

    [Fact]
    public async Task AccionPorAprobar_no_cuenta_un_detalle_de_Gestion_real_en_un_mensaje_con_motivo_de_ruido_salvo_que_el_Gestor_CAE_lo_confirmara()
    {
        // Mismo motivo de ruido que para la Visita: el mensaje entero es ruido, y la fila lo cuenta como tal en MensajesRuido.
        var banda = new Banda();
        var ruido = banda.Hilo("Resumen automático con un detalle de Gestión");
        var confirmado = banda.Hilo("Resumen que el Gestor CAE confirmó que importa");
        var sinMotivo = banda.Hilo("Notificación automática sin motivo de ruido");
        banda.DetalleDeGestion(ruido);
        banda.DetalleDeGestion(confirmado);
        banda.DetalleDeGestion(sinMotivo);
        banda.MotivoRuido(ruido, MotivoRuidoMensaje.ResumenSinCambios);
        banda.MotivoRuido(confirmado, MotivoRuidoMensaje.ResumenSinCambios).ConfirmarManualmente();
        banda.MotivoRuido(sinMotivo, MotivoRuidoMensaje.Ninguno);

        var resultado = await banda.Lista();

        var filaRuido = resultado.Single(c => c.Id == ruido.ConversacionId);
        filaRuido.AccionPorAprobar.Should().BeFalse();
        filaRuido.MensajesRuido.Should().Be(1);
        resultado.Single(c => c.Id == confirmado.ConversacionId).AccionPorAprobar.Should().BeTrue();
        // Una clasificación sin motivo (Ninguno) no es ruido: esconder la propuesta real sería un falso negativo.
        var filaSinMotivo = resultado.Single(c => c.Id == sinMotivo.ConversacionId);
        filaSinMotivo.AccionPorAprobar.Should().BeTrue();
        filaSinMotivo.MensajesRuido.Should().Be(0);
    }

    [Fact]
    public async Task AccionPorAprobar_es_false_con_el_marco_solo_Inbound_aunque_haya_propuestas_pendientes()
    {
        // D6=a: el detalle no proyecta las sugerencias de Outbound al Tenant solo Inbound; la lista no debe revelar que existen.
        var banda = new Banda();
        var hilo = banda.Hilo("Con propuestas");
        banda.SugerenciaVisita(hilo);
        banda.DetalleDeGestion(hilo);

        using (AmbitoComunicacionesSoloInbound.Fijar(acotada: true))
            (await banda.Lista()).Single().AccionPorAprobar.Should().BeFalse();

        using (AmbitoComunicacionesSoloInbound.Fijar(acotada: false))
            (await banda.Lista()).Single().AccionPorAprobar.Should().BeTrue();
    }

    [Fact]
    public async Task Buzon_trae_el_nombre_de_la_conexion_y_si_es_el_buzon_personal_del_usuario()
    {
        var banda = new Banda();
        var compartido = banda.Hilo("Por el buzón compartido");
        var personal = banda.Hilo("Por mi buzón personal");
        var sinConexion = banda.Hilo("Creado a mano");
        banda.Conversacion(compartido).AsociarConexion(banda.Conexion("Norte").Id, "hilo-1");
        banda.Conversacion(personal).AsociarConexion(banda.Conexion("Mi buzón", gestorPropietarioId: banda.Usuario).Id, "hilo-2");

        var resultado = await banda.Lista();

        var filaCompartida = resultado.Single(c => c.Id == compartido.ConversacionId);
        filaCompartida.BuzonNombre.Should().Be("Norte");
        filaCompartida.BuzonEsPersonal.Should().BeFalse();
        var filaPersonal = resultado.Single(c => c.Id == personal.ConversacionId);
        filaPersonal.BuzonNombre.Should().Be("Mi buzón");
        filaPersonal.BuzonEsPersonal.Should().BeTrue();
        var filaSinConexion = resultado.Single(c => c.Id == sinConexion.ConversacionId);
        filaSinConexion.BuzonNombre.Should().BeNull();
        filaSinConexion.BuzonEsPersonal.Should().BeFalse();
    }

    [Fact]
    public async Task EjecutivoAsignadoNombre_resuelve_el_nombre_de_quien_el_directorio_deja_ver()
    {
        var banda = new Banda();
        var marta = Guid.NewGuid();
        var deOtroTenant = Guid.NewGuid();
        var asignada = banda.Hilo("Asignada a Marta");
        var ajena = banda.Hilo("Asignada a alguien que no se ve");
        var sinAsignar = banda.Hilo("Sin asignar");
        banda.Conversacion(asignada).Asignar(marta);
        banda.Conversacion(ajena).Asignar(deOtroTenant);
        var directorio = new DirectorioConNombres(new Dictionary<Guid, string> { [marta] = "Marta Vidal" });

        var resultado = await banda.Lista(directorio);

        resultado.Single(c => c.Id == asignada.ConversacionId).EjecutivoAsignadoNombre.Should().Be("Marta Vidal");
        resultado.Single(c => c.Id == asignada.ConversacionId).EjecutivoAsignadoId.Should().Be(marta);
        // Un Id que el directorio no resuelve no se enseña con el nombre de otro: queda sin nombre, con su Id.
        resultado.Single(c => c.Id == ajena.ConversacionId).EjecutivoAsignadoNombre.Should().BeNull();
        resultado.Single(c => c.Id == sinAsignar.ConversacionId).EjecutivoAsignadoNombre.Should().BeNull();
        directorio.IdsPedidos.Should().BeEquivalentTo([marta, deOtroTenant], "una sola llamada con los Ids distintos de la página, sin uno por fila");
        directorio.Llamadas.Should().Be(1);
    }

    [Fact]
    public async Task EjecutivoAsignadoNombre_es_null_sin_directorio()
    {
        var banda = new Banda();
        var hilo = banda.Hilo("Asignada");
        banda.Conversacion(hilo).Asignar(Guid.NewGuid());

        (await banda.Lista()).Single().EjecutivoAsignadoNombre.Should().BeNull();
    }

    [Fact]
    public async Task Rellenar_los_datos_de_la_fila_no_cambia_que_filas_salen_ni_su_numero()
    {
        var banda = new Banda();
        var hilos = Enumerable.Range(1, 4).Select(i => banda.Hilo($"Hilo {i}")).ToList();
        // Fuera de la cartera (otra Empresa contraparte) y fuera por el buzón personal de otro Gestor CAE: R-3a los excluye.
        var deOtraCartera = banda.Hilo("De otra cartera", empresaId: Guid.NewGuid());
        var deBuzonAjeno = banda.Hilo("De un buzón personal ajeno");
        banda.Conversacion(deBuzonAjeno).AsociarConexion(banda.Conexion("Ajeno", gestorPropietarioId: Guid.NewGuid()).Id, "hilo-ajeno");

        var antes = await banda.Pagina();

        // Todo lo que une la fila con otras tablas, repetido a propósito: varios mensajes y adjuntos por hilo, varias
        // sugerencias y detalles, una conexión, una persona asignada. Una unión mal hecha duplicaría filas.
        var marta = Guid.NewGuid();
        var directorio = new DirectorioConNombres(new Dictionary<Guid, string> { [marta] = "Marta Vidal" });
        var conexion = banda.Conexion("Norte");
        foreach (var hilo in hilos)
        {
            var segundo = banda.MensajeEn(hilo.ConversacionId);
            banda.Adjunto(hilo, "a.pdf");
            banda.Adjunto(hilo, "b.pdf");
            banda.Adjunto(segundo, "c.pdf");
            banda.SugerenciaVisita(hilo);
            banda.SugerenciaVisita(segundo);
            banda.DetalleDeGestion(hilo);
            banda.DetalleDeGestion(hilo);
            banda.Conversacion(hilo).AsociarConexion(conexion.Id, $"hilo-{hilo.ConversacionId:N}");
            banda.Conversacion(hilo).Asignar(marta);
        }
        // Los datos de las conversaciones excluidas existen, y no pueden colarlas ni contar para nadie.
        banda.Adjunto(deOtraCartera, "ajeno.pdf");
        banda.Adjunto(deBuzonAjeno, "ajeno.pdf");
        banda.SugerenciaVisita(deOtraCartera);
        banda.DetalleDeGestion(deOtraCartera);
        banda.DetalleDeGestion(deBuzonAjeno);

        var despues = await banda.Pagina(directorio);

        despues.TotalElementos.Should().Be(antes.TotalElementos).And.Be(hilos.Count);
        despues.Elementos.Should().HaveCount(antes.Elementos.Count);
        despues.Elementos.Select(c => c.Id).Order().Should().Equal(antes.Elementos.Select(c => c.Id).Order());
        despues.Elementos.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        despues.Elementos.Select(c => c.Id).Should().NotContain([deOtraCartera.ConversacionId, deBuzonAjeno.ConversacionId]);
        despues.Elementos.Should().OnlyContain(c => c.TotalAdjuntos == 3 && c.AccionPorAprobar
            && c.BuzonNombre == "Norte" && c.EjecutivoAsignadoNombre == "Marta Vidal");
    }

    [Fact]
    public async Task El_directorio_se_consulta_una_vez_por_pagina_con_los_asignados_de_la_pagina()
    {
        // Sin N+1 en la única lectura que sale de Application hacia otro servicio: con 25 hilos y páginas de 20, el directorio
        // se consulta una sola vez, con solo los asignados de la página. Las lecturas de adjuntos, propuestas y buzón por
        // lote las acota el presupuesto de sentencias de la pantalla en CI, no este test.
        var banda = new Banda();
        var personas = Enumerable.Range(0, 25).Select(_ => Guid.NewGuid()).ToList();
        for (var i = 0; i < 25; i++)
            banda.Conversacion(banda.Hilo($"Hilo {i}")).Asignar(personas[i]);
        var directorio = new DirectorioConNombres(personas.ToDictionary(id => id, id => $"Persona {id:N}"));

        var pagina = await banda.Pagina(directorio);

        pagina.Elementos.Should().HaveCount(20);
        pagina.TotalElementos.Should().Be(25);
        directorio.Llamadas.Should().Be(1);
        directorio.IdsPedidos.Should().BeEquivalentTo(pagina.Elementos.Select(c => c.EjecutivoAsignadoId!.Value));
    }

    private sealed class Banda
    {
        private readonly Guid _empresa = Guid.NewGuid();
        public Guid Usuario { get; } = Guid.NewGuid();
        public ComunicacionesQueryContextFalso Comunicaciones { get; } = new();
        private readonly IntegracionesQueryContextFalso _integraciones = new();

        /// <summary>Un hilo anclado a la Empresa contraparte que la cartera del usuario alcanza, con su primer mensaje.</summary>
        public Mensaje Hilo(string asunto, Guid? empresaId = null)
        {
            var conversacion = new Conversacion(asunto, empresaId: empresaId ?? _empresa);
            Comunicaciones.ListaConversaciones.Add(conversacion);
            return MensajeEn(conversacion.Id);
        }

        public Mensaje MensajeEn(Guid conversacionId)
        {
            var mensaje = new Mensaje(
                conversacionId, DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@ejemplo.test",
                "<p>Texto.</p>", new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc));
            Comunicaciones.ListaMensajes.Add(mensaje);
            return mensaje;
        }

        public Conversacion Conversacion(Mensaje hilo) =>
            Comunicaciones.ListaConversaciones.Single(c => c.Id == hilo.ConversacionId);

        public AdjuntoMensaje Adjunto(Mensaje mensaje, string nombre)
        {
            var adjunto = new AdjuntoMensaje(mensaje.Id, nombre, "application/pdf", 1024, $"almacen/{Guid.NewGuid():N}");
            Comunicaciones.ListaAdjuntosMensaje.Add(adjunto);
            return adjunto;
        }

        public SugerenciaVisitaCorreo SugerenciaVisita(Mensaje mensaje)
        {
            var sugerencia = new SugerenciaVisitaCorreo(
                mensaje.Id, Guid.NewGuid(), new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 2), "Visita de mantenimiento", 90, 90, 90);
            Comunicaciones.ListaSugerenciasVisitaCorreo.Add(sugerencia);
            return sugerencia;
        }

        public DetalleSugerenciaGestionCorreo DetalleDeGestion(Mensaje mensaje)
        {
            var sugerencia = new SugerenciaGestionCorreo(mensaje.Id, "Renovar el reconocimiento médico", 80);
            var detalle = sugerencia.AgregarDetalle(Guid.NewGuid(), Guid.NewGuid(), 80, 80);
            Comunicaciones.ListaSugerenciasGestionCorreo.Add(sugerencia);
            Comunicaciones.ListaDetallesSugerenciaGestionCorreo.Add(detalle);
            return detalle;
        }

        /// <summary>Marca un detalle de Gestión como repetición de algo ya reclamado (ruido de detalle).</summary>
        public ClasificacionRuidoDetalleGestion Repeticion(DetalleSugerenciaGestionCorreo detalle)
        {
            var clasificacion = new ClasificacionRuidoDetalleGestion(detalle.Id, Guid.NewGuid());
            Comunicaciones.ListaClasificacionesRuidoDetalleGestion.Add(clasificacion);
            return clasificacion;
        }

        public ClasificacionRuidoMensaje MotivoRuido(Mensaje mensaje, MotivoRuidoMensaje motivo)
        {
            var clasificacion = new ClasificacionRuidoMensaje(mensaje.Id, esNotificacionAutomatica: true, proveedorPlataformaCaeId: null, motivo);
            Comunicaciones.ListaClasificacionesRuidoMensaje.Add(clasificacion);
            return clasificacion;
        }

        public ConexionIntegracion Conexion(string nombre, Guid? gestorPropietarioId = null)
        {
            var conexion = new ConexionIntegracion($"{Guid.NewGuid():N}@buzon.test", nombre, gestorPropietarioId: gestorPropietarioId);
            _integraciones.ConexionesLista.Add(conexion);
            return conexion;
        }

        public async Task<IReadOnlyList<ConversacionListaDto>> Lista(IDirectorioUsuariosService? directorio = null) =>
            (await Pagina(directorio)).Elementos;

        public Task<ResultadoPaginado<ConversacionListaDto>> Pagina(IDirectorioUsuariosService? directorio = null) =>
            Handler(directorio).Handle(new ObtenerConversacionesQuery(), CancellationToken.None);

        private ObtenerConversacionesQueryHandler Handler(IDirectorioUsuariosService? directorio) => new(
            new EmpresasQueryContextFalso(), Comunicaciones, _integraciones, new CentrosQueryContextFalso(),
            new GestionesVacias(), new VisitasVacias(),
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [], empresaIdsVisibles: [_empresa]),
            new CurrentUserServiceFalso(Usuario, "GestorCae"), new SinTenant(),
            directorioUsuarios: directorio);
    }

    private sealed class DirectorioConNombres(IReadOnlyDictionary<Guid, string> nombres) : IDirectorioUsuariosService
    {
        public int Llamadas { get; private set; }
        public List<Guid> IdsPedidos { get; } = [];

        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(
            IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default)
        {
            Llamadas++;
            IdsPedidos.AddRange(usuarioIds);
            return Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                usuarioIds.Where(nombres.ContainsKey).ToDictionary(id => id, id => nombres[id]));
        }

        public Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> EsCuentaActivaConRolAsync(Guid usuarioId, Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ObtenerCuentasActivasConRolAsync(Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class VisitasVacias : IVisitasQueryContext
    {
        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(new List<Visita>().AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores => throw new NotSupportedException();
    }

    private sealed class GestionesVacias : IGestionesQueryContext
    {
        public IQueryable<Gestion> Gestiones => new TestAsyncQueryable<Gestion>(new List<Gestion>().AsQueryable());
        public IQueryable<GestionAutomatica> GestionesAutomaticas => throw new NotSupportedException();
        public IQueryable<PasoGestionAutomatica> PasosGestionAutomatica => throw new NotSupportedException();
    }

    private sealed class SinTenant : ITenantActual
    {
        public Guid? TenantId => null;
    }
}
