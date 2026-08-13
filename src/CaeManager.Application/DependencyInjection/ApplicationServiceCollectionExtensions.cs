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

namespace CaeManager.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var ensamblado = Assembly.GetExecutingAssembly();

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(ensamblado));
        services.AddValidatorsFromAssembly(ensamblado);
        // Scoped: una puerta por petición HTTP o circuito de Blazor — la
        // misma que usan los accesos a datos que no pasan por MediatR.
        services.AddScoped<PuertaAccesoDatos>();
        // Orden importa. LoggingBehavior va el primero de todos: mide lo que
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
        // validarse.
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(SerializacionAccesoDatosBehavior<,>));
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(ConcurrenciaBehavior<,>));
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(AutorizacionEscrituraBehavior<,>));
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // Orquestador puro (solo depende de contratos de Application, ver
        // DeteccionTrabajadoresService) — se registra aquí y no en
        // Infrastructure porque no toca nada específico de infraestructura.
        services.AddScoped<IDeteccionTrabajadoresService, DeteccionTrabajadoresService>();
        services.AddScoped<ISugerenciaVisitaCorreoService, SugerenciaVisitaCorreoService>();
        services.AddScoped<ISugerenciaGestionCorreoService, SugerenciaGestionCorreoService>();
        services.AddScoped<IResolucionParticipanteConversacionService, ResolucionParticipanteConversacionService>();
        services.AddScoped<Contactos.IResolucionDestinatariosAgendaService, Contactos.ResolucionDestinatariosAgendaService>();
        services.AddScoped<IClasificacionRuidoMensajeService, ClasificacionRuidoMensajeService>();
        services.AddScoped<IRelevanciaCaeService, RelevanciaCaeService>();
        services.AddScoped<IMotorCoincidenciaConversacionesService, MotorCoincidenciaConversacionesService>();
        services.AddScoped<IDerivarCanalesAplicablesDocumentoService, DerivarCanalesAplicablesDocumentoService>();
        services.AddScoped<IPaqueteDocumentalVisitaService, PaqueteDocumentalVisitaService>();
        services.AddScoped<IEvaluadorExpedienteVisitaService, EvaluadorExpedienteVisitaService>();
        services.AddScoped<IVerificacionIaDocumentoService, VerificacionIaDocumentoService>();
        services.AddScoped<IValidacionDocumentoOficialService, ValidacionDocumentoOficialService>();

        // Parsers de documento oficial: lógica pura (regex sobre texto),
        // singletons sin estado; el registry los indexa por perfil.
        services.AddSingleton<IParserDocumentoOficial, ParserCorrienteTgss>();
        services.AddSingleton<IParserDocumentoOficial, ParserCorrienteAeat>();
        services.AddSingleton<IParserDocumentoOficial, ParserIta>();
        services.AddSingleton<IParserDocumentoOficial, ParserRnt>();
        services.AddSingleton<IParserDocumentoOficial, ParserRlc>();
        services.AddSingleton<IParserDocumentoOficialRegistry, ParserDocumentoOficialRegistry>();
        services.AddScoped<ICalculoEstadoCentroService, CalculoEstadoCentroService>();
        services.AddScoped<ICalculoEstadoSubcontrataService, CalculoEstadoSubcontrataService>();
        services.AddScoped<ICalculoEstadoDocumentalService, CalculoEstadoDocumentalService>();
        services.AddScoped<IDocumentosFaltantesService, DocumentosFaltantesService>();
        services.AddScoped<IResolucionProveedorPlataformaCaeService, ResolucionProveedorPlataformaCaeService>();

        // Factory pura (Application) — cada IDocumentAIProvider real se
        // registra en Infrastructure (ver docs/ARQUITECTURA-IA-DOCUMENTAL.md § 2).
        services.AddScoped<IDocumentAIProviderFactory, DocumentAIProviderFactory>();
        services.AddScoped<IDocumentAIRouterService, DocumentAIRouterService>();
        services.AddSingleton<ILocalizadorPaginasRelevantesService, LocalizadorPaginasRelevantesService>();

        // Sustituye a AnthropicExtraccionMetadatosDocumentoIaService (Fase 38):
        // VerificacionIaDocumentoService no cambia, solo qué implementación
        // de IExtraccionMetadatosDocumentoIaService resuelve el contenedor.
        services.AddScoped<IExtraccionMetadatosDocumentoIaService, RouterExtraccionMetadatosDocumentoIaService>();

        return services;
    }
}
