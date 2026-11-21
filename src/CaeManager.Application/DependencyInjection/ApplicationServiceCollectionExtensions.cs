using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using System.Reflection;
using CaeManager.Application.Alertas;
using CaeManager.Application.Centros;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Application.Comunicaciones.Matching;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.ValidacionOficial;
using CaeManager.Application.Documentos.ValidacionOficial.Parsers;
using CaeManager.Application.Documentos.Verificacion;
using CaeManager.Application.DocumentosIa;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Trabajadores.Deteccion;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Application.Visitas.PaqueteDocumental;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CaeManager.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var ensamblado = Assembly.GetExecutingAssembly();

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(ensamblado));
        services.AddValidatorsFromAssembly(ensamblado);
        services.AddOptions<CaeManager.Application.AsistenteIa.AsistenteConsultaOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CaeManager.Application.AsistenteIa.LimitadorConsultaIa>();
        // Scoped: una puerta por petición HTTP o circuito de Blazor — la
        // misma que usan los accesos a datos que no pasan por MediatR.
        services.AddScoped<PuertaAccesoDatos>();
        // La autorización de Inbound (B-03): capacidades desde el papel activo y el rol; todo handler Inbound la llama.
        services.AddScoped<CaeManager.Application.Inbound.Autorizacion.IAutorizacionInbound,
            CaeManager.Application.Inbound.Autorizacion.AutorizacionInbound>();
        // Coste de acceso a datos del mismo scope (lo lee la cabecera Server-Timing).
        services.AddScoped<CosteCarga>();
        services.AddScoped<CaeManager.Application.Integraciones.ConectoresPlataformaCae.IOperacionesConectorPlataformaCae,
            CaeManager.Application.Integraciones.ConectoresPlataformaCae.OperacionesConectorPlataformaCae>();
        services.AddScoped<CaeManager.Application.Dashboard.Queries.MedicionMensualCuadroDireccion>();
        // Puerto del Núcleo por el que Facturación lee la liquidación del Operador CAE sin depender de Outbound (D-F2).
        services.AddScoped<CaeManager.Application.Common.IFuenteLiquidacionOperadorCae,
            CaeManager.Application.Dashboard.TarifasOperadorCae.FuenteLiquidacionOperadorCae>();
        // Puerto del Núcleo por el que la tabla de Buzones lee de Comunicaciones su último correo y sus conversaciones abiertas sin depender de Outbound.
        services.AddScoped<CaeManager.Application.Integraciones.IActividadBuzones,
            CaeManager.Application.Comunicaciones.Queries.ObtenerActividadBuzones.ActividadBuzonesService>();
        // Puerto del Núcleo por el que la pantalla de Buzones lee, por la vista unificada de Comunicaciones, los buzones de
        // cada Empresa gestionada (D-6 b, L-55) sin depender de Outbound. Solo lectura.
        services.AddScoped<CaeManager.Application.Integraciones.IBuzonesDeTenantsBeneficiarios,
            CaeManager.Application.Comunicaciones.VistaUnificada.BuzonesDeTenantsBeneficiariosService>();
        // Lectura de la «Nota interna» de las fichas 360: una sola decisión para todas (ver PoliticaNotaInterna).
        services.AddScoped<CaeManager.Application.Common.IPoliticaNotaInterna, CaeManager.Application.Common.PoliticaNotaInterna>();
        // Singleton a propósito (Horizonte 2.4): la ventana de tasa de
        // error/latencia degradada tiene que acumular entre requests de todo
        // el proceso, no reiniciarse en cada petición como el resto de
        // dependencias Scoped de este método.
        services.AddSingleton<VentanaSaludOperativa>();
        // Orden global del menú lateral: una caché por proceso, invalidada al guardar.
        services.AddSingleton<CaeManager.Application.Plataforma.OrdenMenu.CacheOrdenMenuLateral>();
        // Esqueleto común de los Commands que cambian una tarea del asistente de flujos.
        services.AddScoped<CaeManager.Application.AsistenteIa.Tareas.ModificacionTareaAsistente>();
        services.AddScoped<CaeManager.Application.AsistenteIa.Edicion.RevisionPlanAsistente>();
        services.AddScoped<CaeManager.Application.Clientes.ReasignadorCarteraCliente>();
        services.AddScoped<
            CaeManager.Application.Usuarios.Commands.CambiarActivacionUsuario.IAvisoDeCarteraSinGestorCae,
            CaeManager.Application.Usuarios.Commands.CambiarActivacionUsuario.AvisoDeCarteraSinGestorCae>();
        // Webhook de Stripe: aplica el estado actual de la Subscription, no la copia del evento.
        services.AddScoped<CaeManager.Application.Comercial.Common.IProcesadorWebhookSuscripcion,
            CaeManager.Application.Comercial.Common.ProcesadorWebhookSuscripcion>();
        services.AddScoped<CaeManager.Application.Integraciones.ResponsableBuzonElegible>();
        services.AddScoped<CaeManager.Application.Operaciones.IAsignacionAutomaticaDePrincipal,
            CaeManager.Application.Operaciones.AsignacionAutomaticaDePrincipal>();
        // Quién puede restablecer la 2FA de otra cuenta, separado del acto: el
        // Administrador del Tenant (P0-8) o Soporte TALVEG con Sesión Privilegiada
        // (ADR-011 § 8.7, punto 3). La compuesta elige por la sesión, nunca prueba los dos.
        services.AddScoped<CaeManager.Application.Usuarios.Commands.RestablecerSegundoFactor.AutorizacionRestablecerSegundoFactorAdministrador>();
        services.AddScoped<CaeManager.Application.Usuarios.Commands.RestablecerSegundoFactor.AutorizacionRestablecerSegundoFactorPorSoporte>();
        services.AddScoped<
            CaeManager.Application.Usuarios.Commands.RestablecerSegundoFactor.IAutorizacionRestablecerSegundoFactor,
            CaeManager.Application.Usuarios.Commands.RestablecerSegundoFactor.AutorizacionRestablecerSegundoFactorCompuesta>();
        // TryAdd, no Add: Program.cs registra la implementación real
        // (SentryAlertaOperativa, Infrastructure) después de AddApplication()
        // y la sustituye — ver AlertaOperativaInerte para el porqué de este
        // valor por defecto en vez de exigir que cada host que monte el
        // pipeline de MediatR (incluidos los fixtures mínimos de
        // CaeManager.IntegrationTests) registre uno explícito.
        services.TryAddSingleton<IAlertaOperativa, AlertaOperativaInerte>();
        // Mismo motivo y mismo mecanismo: AutorizacionEscrituraBehavior necesita
        // resolver la sesión privilegiada, y MediatR construye todos los
        // behaviors para cualquier request. Web registra la implementación real
        // después, vía AddInfrastructure(). Ver SesionPrivilegiadaAusente para
        // por qué este valor por defecto no debilita la autorización.
        services.TryAddScoped<CaeManager.Application.Plataforma.ISesionPrivilegiadaActual,
            CaeManager.Application.Plataforma.SesionPrivilegiadaAusente>();
        // Mismo patrón: la implementación real (Infrastructure) mueve el rol
        // de PostgreSQL además de abrir el ámbito; este valor por defecto solo
        // abre el ámbito, correcto para los fixtures mínimos de
        // CaeManager.IntegrationTests que no tienen conexión que elevar.
        services.TryAddScoped<CaeManager.Application.Plataforma.IElevacionEscrituraPrivilegiada,
            CaeManager.Application.Plataforma.ElevacionEscrituraPrivilegiadaInerte>();
        // Mismo patrón (issue #2001), pero el valor por defecto dice «no registrado»: fallo cerrado
        // si un host resolviera una Sesión Privilegiada sin la implementación real de Infrastructure.
        services.TryAddScoped<CaeManager.Application.Plataforma.IRegistroLecturaSesionPrivilegiada,
            CaeManager.Application.Plataforma.RegistroLecturaSesionPrivilegiadaNoDisponible>();
        services.TryAddScoped<CaeManager.Application.Plataforma.ICoordenadaSesionPrivilegiada,
            CaeManager.Application.Plataforma.CoordenadaSesionPrivilegiadaAusente>();
        services.AddScoped<IAutorizacionEscrituraEfectiva, AutorizacionEscrituraEfectiva>();
        services.AddScoped<CaeManager.Application.Comercial.LineaOutbound.IEstadoLineaOutbound,
            CaeManager.Application.Comercial.LineaOutbound.EstadoLineaOutboundService>();
        services.AddScoped<CaeManager.Application.Comercial.LineaOutbound.IOutboundCerradoTenantTec,
            CaeManager.Application.Comercial.LineaOutbound.OutboundCerradoTenantTecService>();
        // «Inbound sin Outbound» (decisión del 2026-11-18): el criterio único que cierra Outbound a un Tenant solo Inbound
        // con el papel de Tenant de origen. Lo consumen la guarda de comandos, el alcance y los endpoints mínimos de Outbound.
        services.AddScoped<CaeManager.Application.Comercial.LineaOutbound.ITenantSoloInbound,
            CaeManager.Application.Comercial.LineaOutbound.TenantSoloInboundService>();
        // El criterio memoiza la respuesta como el alcance: la MISMA instancia Scoped se expone como IMemoPorContexto para
        // que InvalidacionAlcanceBehavior la descarte en cada Command. Infrastructure lo vuelve a registrar con la
        // caducidad configurada del alcance (CaducidadAlcanceOptions).
        services.AddScoped<CriterioDeOutbound>();
        services.AddScoped<ICriterioDeOutbound>(sp => sp.GetRequiredService<CriterioDeOutbound>());
        services.AddScoped<IMemoPorContexto>(sp => sp.GetRequiredService<CriterioDeOutbound>());
        services.AddScoped<Importacion.RecorteDeAnalisisAlAmbito>();
        services.AddSingleton<CaeManager.Application.HerramientasPdf.IDisponibilidadHerramientasPdf, CaeManager.Application.HerramientasPdf.DisponibilidadHerramientasPdf>();
        // Foto a PDF: fuera de MediatR, para no retener la puerta de acceso a datos del circuito mientras decodifica.
        services.AddSingleton<CaeManager.Application.HerramientasPdf.GeneradorPdfDesdeFotos>();
        // Un solo limitador de CPU por proceso para Herramientas PDF (miniaturas y composición), no uno por circuito.
        services.AddSingleton<CaeManager.Application.HerramientasPdf.LimitadorConversionesPdf>();
        // Valor por defecto: la implementación real (Infrastructure) es la misma instancia scoped de
        // AlcanceDatosService, que es quien memoiza.
        services.TryAddScoped<IInvalidadorAlcance, InvalidadorAlcanceInerte>();
        // Memoizaciones de lectura del layout (MemoPorContexto): Scoped (petición o circuito), nunca singleton, con
        // clave por usuario y contexto. Cada una se expone también como IMemoPorContexto, la MISMA instancia, para
        // que InvalidacionAlcanceBehavior las descarte en cada Command.
        AgregarMemoPorContexto<CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados.MemoClientesAutorizados>(services);
        AgregarMemoPorContexto<CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual.MemoPerfilVocabularioActual>(services);
        AgregarMemoPorContexto<CaeManager.Application.Tenants.Queries.UsaRotulosPrimeraPersona.MemoRotulosPrimeraPersona>(services);
        AgregarMemoPorContexto<CaeManager.Application.Operaciones.IncorporacionCartera.Queries.MemoParticipaEnIncorporacionCartera>(services);
        AgregarMemoPorContexto<CaeManager.Application.Usuarios.Queries.ObtenerAvatarPropio.MemoAvatarPropio>(services);
        AgregarMemoPorContexto<CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma.MemoAdministradorPlataforma>(services);
        // Frescura del estado materializado (diseño § 5): el aviso es uno por proceso; la caché, una por circuito
        // (Scoped, nunca singleton) y no caduca por tiempo sino por la versión del Tenant.
        services.TryAddSingleton<CaeManager.Application.EstadoMaterializado.Frescura.INotificadorEstadoTenant,
            CaeManager.Application.EstadoMaterializado.Frescura.NotificadorEstadoTenant>();
        services.AddScoped<CaeManager.Application.EstadoMaterializado.Frescura.CacheEstadoCircuito>();
        // Señal «actúa por Encargo de administración» (decisión D-8, 2026-10-08): la da LA MISMA
        // instancia que resuelve el rol efectivo, porque es la misma función. Se reenvía aquí, y no
        // en cada composición, para que no exista un contenedor donde las dos se resuelvan a
        // objetos distintos.
        //
        // Un ICurrentUserService registrado que NO da la señal es un error de composición, y falla
        // al resolver: degradarlo en silencio a «sin encargo» dejaría el rol elevado sin
        // exclusiones, sin vía de auditoría y sin corte del circuito el día que alguien envuelva o
        // sustituya CurrentUserService (revisión previa a PR, 2026-10-09). Un doble de prueba que
        // entre en un contenedor con AddApplication() declara la señal él mismo (los compartidos
        // dicen «sin encargo») o registra IEncargoDeAdministracionActual a mano.
        //
        // Sin ningún ICurrentUserService registrado no hay sesión de usuario, y «sin encargo» es
        // la respuesta exacta. Medido el 2026-10-09: en src/ solo la aplicación web llama a
        // AddApplication(), y siempre registra CurrentUserService; ese caso solo se da en
        // contenedores de prueba.
        services.TryAddScoped<IEncargoDeAdministracionActual>(sp =>
            sp.GetService<ICurrentUserService>() switch
            {
                null => SinEncargoDeAdministracion.Instancia,
                IEncargoDeAdministracionActual senal => senal,
                var otro => throw new InvalidOperationException(
                    $"{otro.GetType().FullName} está registrado como {nameof(ICurrentUserService)} y no implementa "
                    + $"{nameof(IEncargoDeAdministracionActual)}: el rol efectivo podría elevarse por Encargo de "
                    + "administración sin que lo supieran ni las exclusiones ni la auditoría. La señal la da la misma "
                    + "clase que resuelve el rol efectivo."),
            });
        // Orden importa. Por fuera de todo solo va CosteCargaBehavior, que únicamente cuenta
        // despachos. Después, LoggingBehavior, el primero de los que actúan: mide lo que
        // el usuario espera de verdad, incluido el tiempo en la cola de
        // acceso a datos, y su ámbito de log correlaciona todo lo que
        // registren los behaviors de dentro. Puede ir ahí porque no toca la
        // base de datos (ver su comentario). Luego SerializacionAccesoDatos:
        // la serialización del DbContext tiene que abarcar el resto del
        // request, incluidos los demás behaviors (AutorizacionEscritura
        // consulta el rol del usuario). Luego ConcurrenciaBehavior, que
        // envuelve a los otros dos: el choque de concurrencia nace dentro del
        // handler, al guardar, así que quien lo captura tiene que estar por
        // fuera. Después, un Command bloqueado por rol ni siquiera llega a
        // validarse — y GateComercialTenantBehavior (Horizonte 1.7) va justo
        // a continuación por el mismo motivo, con el estado del TENANT en
        // vez del rol del usuario.
        // CosteCargaBehavior, por fuera de todos: solo suma un despacho al acumulador del scope
        // (Server-Timing «med»), no toca la base ni cambia el resultado.
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(CosteCargaBehavior<,>));
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(SerializacionAccesoDatosBehavior<,>));
        // InvalidacionAlcanceBehavior, dentro de la serialización: antes y después de cada Command
        // descarta el alcance memoizado del scope (D-8 del piloto Outbound), sin soltar la puerta.
        // Va por fuera de AutorizacionEscrituraBehavior y del handler: ambos autorizan con el
        // alcance recién resuelto, no con el que memoizó el circuito.
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(InvalidacionAlcanceBehavior<,>));
        // D-4 (#2005): tras una orden de Acreditación con éxito, reevalúa el sello de las Visitas de su Documento. Dentro
        // de la serialización (usa el DbContext del scope) y por fuera de la restauración, la autorización y el handler:
        // solo actúa cuando la orden ya pasó todas las guardas y guardó; una orden rechazada no reevalúa nada.
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(Visitas.Antelacion.ReevaluarExpedientesTrasCambioDeAcreditacionBehavior<,>));
        // La instantánea queda dentro de la puerta y envuelve todos los resultados de fallo.
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(RestauracionCambiosDeComandoBehavior<,>));
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(CaeManager.Application.Empresas.DuplicadosEmpresaBehavior<,>));
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(ConcurrenciaBehavior<,>));
        // Guarda de pestaña obsoleta (D-6 b, R-2) antes de autorizar: un comando de una pestaña cuya selección ya no
        // es la vigente no llega a tocar datos.
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(GuardaPestanaObsoletaBehavior<,>));
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(AutorizacionEscrituraBehavior<,>));
        // GateComercialTenantBehavior va justo después: un Command ya
        // bloqueado por rol ni siquiera necesita la consulta a Tenants que
        // hace este behavior (Horizonte 1.7, "Billing mínimo viable").
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(GateComercialTenantBehavior<,>));
        // La guarda por servicio (DN-21, B-02b), después del rol y del estado del Tenant: un comando de Inbound con papel
        // Outbound, o uno que no es de Inbound con papel Inbound, falla cerrado antes del handler.
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(GuardaPorServicioBehavior<,>));
        // El límite del Tenant TEC participante (D3, 2026-11-19), después del papel: Outbound cerrado para el Tenant
        // beneficiario marcado y sin línea Outbound vigente. Antes de la elevación a cae_app_aprovisionamiento, que no ve
        // la línea.
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(LimiteTenantTecBehavior<,>));
        // El ámbito de petición de Inbound (B-03), dentro de la guarda: con papel Inbound, AlcanceDatosService solo da alcance
        // dentro de una petición de Inbound; los lectores de otros módulos siguen en alcance cero.
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(AmbitoInboundBehavior<,>));
        // Después de la autorización de escritura: lo que el rol elevado por un Encargo de
        // administración abriría y el encargo no cubre (decisión D-8, 2026-10-08). Se aplica
        // a Commands y a Queries: varias de las áreas excluidas son de solo lectura.
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(ExclusionesDelEncargoBehavior<,>));
        // Las peticiones de flujo (IStreamRequest) no pasan por los IPipelineBehavior: su barrera del
        // encargo es este behavior de flujo, con la misma lista y el mismo fallo cerrado (Codex r2, #2241).
        services.AddTransient(typeof(MediatR.IStreamPipelineBehavior<,>), typeof(ExclusionesDelEncargoStreamBehavior<,>));
        // Aparte de AutorizacionEscritura porque responde a otra pregunta: no
        // "¿puede escribir?" sino "¿puede ver ESTE recurso?" — y se aplica a
        // Queries, que aquel deja pasar por definición.
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(AutorizacionSecretosDeTenantBehavior<,>));
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        // Issue #2001: la lectura bajo Sesión Privilegiada se registra después de autorizar y validar
        // y ANTES de elevar: su función solo la ejecuta cae_app_soporte, el rol de la conexión fuera
        // del ámbito de elevación. Sin fila, el handler no corre (fallo cerrado).
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(RegistroLecturaSesionPrivilegiadaBehavior<,>));
        // El ÚLTIMO de todos, a propósito (PD-A3): es el único que abre el rol
        // de escritura acotada de PostgreSQL, y reduce esa ventana al handler
        // solo — nada más externo en este pipeline debe correr elevado.
        services.AddTransient(
            typeof(MediatR.IPipelineBehavior<,>), typeof(ElevacionEscrituraAprovisionamientoBehavior<,>));

        // Orquestador puro (solo depende de contratos de Application, ver
        // DeteccionTrabajadoresService) — se registra aquí y no en
        // Infrastructure porque no toca nada específico de infraestructura.
        services.AddScoped<IDeteccionTrabajadoresService, DeteccionTrabajadoresService>();
        // Parser local de ITA/RNT/TC2 (sin IA): sin estado; lo usa el compositor que registra
        // Infrastructure delante del cliente de IA. Su lector de PDF (PdfPig) es de Infrastructure.
        services.AddSingleton<Trabajadores.Extraccion.IExtractorTrabajadoresLocal, Trabajadores.Extraccion.ExtractorTrabajadoresLocalService>();
        services.AddScoped<ISugerenciaVisitaCorreoService, SugerenciaVisitaCorreoService>();
        services.AddScoped<ISugerenciaGestionCorreoService, SugerenciaGestionCorreoService>();
        services.AddScoped<IResolucionParticipanteConversacionService, ResolucionParticipanteConversacionService>();
        services.AddScoped<Contactos.IResolucionDestinatariosAgendaService, Contactos.ResolucionDestinatariosAgendaService>();
        services.AddScoped<
            Reclamaciones.Commands.EnviarReclamacion.IRegistroEnvioReclamacionService,
            Reclamaciones.Commands.EnviarReclamacion.RegistroEnvioReclamacionService>();
        // Qué se puede pedir sin vencimiento (documento que falta, «Sin confirmar» sin fecha): lo leen el lote y el envío.
        services.AddScoped<Reclamaciones.IPendientesDeReclamacionService, Reclamaciones.PendientesDeReclamacionService>();
        services.AddScoped<Reclamaciones.ReclamacionEmpresaEmpleadoraService>();
        services.AddScoped<Comunicaciones.RellenoMacroRespuesta>();
        services.AddScoped<Reclamaciones.DestinoDeReclamacionesEnviadasService>();
        // Estado en las plataformas del Centro y última reclamación de cada documento: lo leen las fichas 360 que fijan un Centro.
        services.AddScoped<
            Documentos.SituacionEnCentro.ISituacionDocumentosEnCentrosService,
            Documentos.SituacionEnCentro.SituacionDocumentosEnCentrosService>();
        // La vista previa de una reclamación llama a PrepararAsync de los handlers de envío (una sola
        // implementación de «qué se envía y a quién»): hace falta resolverlos como clases concretas.
        services.AddScoped<Reclamaciones.Commands.EnviarReclamacion.EnviarReclamacionCommandHandler>();
        services.AddScoped<Reclamaciones.Commands.EnviarReclamacionEmpresa.EnviarReclamacionEmpresaCommandHandler>();
        services.AddScoped<Reclamaciones.Commands.EnviarReclamacionEmpresaEmpleadora.EnviarReclamacionEmpresaEmpleadoraCommandHandler>();
        services.AddScoped<IClasificacionRuidoMensajeService, ClasificacionRuidoMensajeService>();
        services.AddScoped<IRelevanciaCaeService, RelevanciaCaeService>();
        services.AddScoped<IMotorCoincidenciaConversacionesService, MotorCoincidenciaConversacionesService>();
        services.AddScoped<IAltaAcreditacionesPlataformaService, AltaAcreditacionesPlataformaService>();
        services.AddScoped<IConciliacionAcreditacionesEmpresaService, ConciliacionAcreditacionesEmpresaService>();
        services.AddScoped<Gestiones.INacimientoGestionAutomaticaService, Gestiones.NacimientoGestionAutomaticaService>();
        services.AddScoped<CaeManager.Application.Common.Archivos.IVerificadorArchivoAlmacenado, CaeManager.Application.Common.Archivos.VerificadorArchivoAlmacenado>();
        services.AddScoped<IPaqueteDocumentalVisitaService, PaqueteDocumentalVisitaService>();
        services.AddScoped<CaeManager.Application.Empresas.PaqueteDocumental.IPaqueteDocumentalEmpresaService, CaeManager.Application.Empresas.PaqueteDocumental.PaqueteDocumentalEmpresaService>();
        // D-P11: el límite de una sola Empresa propia por Tenant propietario, el mismo para todos sus escritores.
        services.AddScoped<CaeManager.Application.Empresas.ILimiteEmpresaPropia, CaeManager.Application.Empresas.LimiteEmpresaPropia>();
        services.AddScoped<IEvaluadorExpedienteVisitaService, EvaluadorExpedienteVisitaService>();
        // Visita que asigna al Centro y crea Gestiones (decisión de Chris, 2026-11-19): la misma regla para la vista previa y el alta.
        services.AddScoped<Visitas.AltaDeVisita.IPlanAltaVisitaService, Visitas.AltaDeVisita.PlanAltaVisitaService>();
        services.AddScoped<Visitas.AltaDeVisita.IAltaDeVisitaConAsignaciones, Visitas.AltaDeVisita.AltaDeVisitaConAsignaciones>();
        services.AddScoped<IVerificacionIaDocumentoService, VerificacionIaDocumentoService>();
        services.AddScoped<CaeManager.Application.Cumplimiento.IInstruccionTratamientoIaService, CaeManager.Application.Cumplimiento.InstruccionTratamientoIaService>();
        services.AddScoped<CaeManager.Application.Cumplimiento.AceptacionContractual.IRegistroAceptacionContractualAlta, CaeManager.Application.Cumplimiento.AceptacionContractual.RegistroAceptacionContractualAlta>();
        services.AddScoped<CaeManager.Application.Cumplimiento.AceptacionContractual.IEnvioAcuseAceptacionContractual, CaeManager.Application.Cumplimiento.AceptacionContractual.EnvioAcuseAceptacionContractual>();
        services.AddScoped<IValidacionDocumentoOficialService, ValidacionDocumentoOficialService>();
        services.AddScoped<IRegistroAccesoDocumentoSensibleService, RegistroAccesoDocumentoSensibleService>();
        services.AddScoped<IRegistroAccesoDatoSensibleService, RegistroAccesoDatoSensibleService>();
        services.AddScoped<IRegistroExportacionService, RegistroExportacionService>();
        services.AddScoped<IRegistroCambioContexto, RegistroCambioContextoService>();
        // Web registra la suya, con la cookie de contexto; fuera de Web no hay pestañas.
        services.TryAddScoped<IGuardaSeleccionVigente, SinGuardaDeSeleccion>();

        // Parsers de documento oficial: lógica pura (regex sobre texto),
        // singletons sin estado; el registry los indexa por perfil.
        services.AddSingleton<IParserDocumentoOficial, ParserCorrienteTgss>();
        services.AddSingleton<IParserDocumentoOficial, ParserCorrienteAeat>();
        services.AddSingleton<IParserDocumentoOficial, ParserIta>();
        services.AddSingleton<IParserDocumentoOficial, ParserRnt>();
        services.AddSingleton<IParserDocumentoOficial, ParserRlc>();
        services.AddSingleton<IParserDocumentoOficialRegistry, ParserDocumentoOficialRegistry>();
        services.AddScoped<ICalculoEstadoCentroService, CalculoEstadoCentroService>();
        services.AddScoped<IEvaluacionDeAccesoPorCentroService, EvaluacionDeAccesoPorCentroService>();
        services.AddScoped<IEvaluacionDeAccesoDelTenant, EvaluacionDeAccesoPorCentroService>();
        services.AddScoped<CaeManager.Application.EstadoMaterializado.IRecalculoEstadoMaterializado, CaeManager.Application.EstadoMaterializado.RecalculoEstadoMaterializadoService>();
        services.AddScoped<CaeManager.Application.EstadoMaterializado.ILectorEstadoCentros, CaeManager.Application.EstadoMaterializado.LectorEstadoCentros>();
        // Lo que el Cuadro de dirección y el Dashboard ejecutivo leen de las tablas del estado. Un contenedor que no registra
        // las tablas (composición de prueba) recibe el lector que nunca lee: todo se calcula, que es la referencia.
        services.AddScoped<CaeManager.Application.EstadoMaterializado.ILectorCumplimientoDeDireccion>(sp =>
            sp.GetService<CaeManager.Application.EstadoMaterializado.IEstadoMaterializadoQueryContext>() is null
                ? CaeManager.Application.EstadoMaterializado.SinLectorDeCumplimientoDeDireccion.Instancia
                : Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<CaeManager.Application.EstadoMaterializado.LectorCumplimientoDeDireccion>(sp));
        services.AddScoped<IRequisitosObligatoriosIncumplidosService, RequisitosObligatoriosIncumplidosService>();
        services.AddScoped<CaeManager.Application.EstadoMaterializado.ILectorEstadoMaterializado, CaeManager.Application.EstadoMaterializado.LectorEstadoMaterializado>();
        services.AddScoped<CaeManager.Application.EstadoMaterializado.ILectorEstadoRequisitos, CaeManager.Application.EstadoMaterializado.LectorEstadoRequisitos>();
        services.AddScoped<ICalculoEstadoSubcontrataService, CalculoEstadoSubcontrataService>();
        services.AddScoped<ICalculoEstadoDocumentalService, CalculoEstadoDocumentalService>();
        services.AddScoped<IDocumentosFaltantesService, DocumentosFaltantesService>();
        services.AddScoped<Asignaciones.IResolverClientePrincipalService, Asignaciones.ResolverClientePrincipalService>();
        services.AddScoped<IResolucionProveedorPlataformaCaeService, ResolucionProveedorPlataformaCaeService>();

        // Factory pura (Application) — cada IDocumentAIProvider real se
        // registra en Infrastructure (ver Project-Hydra-Negocio/tecnico/docs/ARQUITECTURA-IA-DOCUMENTAL.md § 2).
        services.AddScoped<IDocumentAIProviderFactory, DocumentAIProviderFactory>();
        services.AddScoped<IDocumentAIRouterService, DocumentAIRouterService>();
        services.AddSingleton<ILocalizadorPaginasRelevantesService, LocalizadorPaginasRelevantesService>();

        // Sustituye a AnthropicExtraccionMetadatosDocumentoIaService (Fase 38):
        // VerificacionIaDocumentoService no cambia, solo qué implementación
        // de IExtraccionMetadatosDocumentoIaService resuelve el contenedor.
        services.AddScoped<IExtraccionMetadatosDocumentoIaService, RouterExtraccionMetadatosDocumentoIaService>();

        return services;
    }

    private static void AgregarMemoPorContexto<TMemo>(IServiceCollection services)
        where TMemo : class, IMemoPorContexto
    {
        services.AddScoped<TMemo>();
        services.AddScoped<IMemoPorContexto>(sp => sp.GetRequiredService<TMemo>());
    }
}
