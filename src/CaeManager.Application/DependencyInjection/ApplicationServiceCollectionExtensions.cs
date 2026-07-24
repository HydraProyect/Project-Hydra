using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Verificacion;
using CaeManager.Application.Trabajadores.Deteccion;
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
        // Orden importa: un Command bloqueado por rol ni siquiera llega a validarse.
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(AutorizacionEscrituraBehavior<,>));
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // Orquestador puro (solo depende de contratos de Application, ver
        // DeteccionTrabajadoresService) — se registra aquí y no en
        // Infrastructure porque no toca nada específico de infraestructura.
        services.AddScoped<IDeteccionTrabajadoresService, DeteccionTrabajadoresService>();
        services.AddScoped<IVerificacionIaDocumentoService, VerificacionIaDocumentoService>();

        return services;
    }
}
