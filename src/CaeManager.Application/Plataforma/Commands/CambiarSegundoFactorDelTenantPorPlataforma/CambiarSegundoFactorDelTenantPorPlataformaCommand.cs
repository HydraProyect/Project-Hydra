using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plataforma.Commands.CambiarSegundoFactorDelTenantPorPlataforma;

/// <summary>
/// Un Actor de Plataforma TALVEG cambia la exigencia de segundo factor para todos los perfiles de un
/// Tenant (decisiones del 2026-11-08; ASVS 4.0.3 V4.1.3, V4.2.1 y V7.1.3). El Administrador del
/// Tenant usa <c>ActualizarSeguridadAccesoCommand</c>; esta vía es de otro plano y solo toca esa
/// exigencia: no lleva el plazo de inactividad, que sigue fijando el Administrador del Tenant.
///
/// <para>
/// El actor no es Gestor CAE, ni Operador CAE, ni Administrador del Tenant, y este comando no le da
/// ningún rol. Lo autoriza, todo a la vez:
/// <list type="bullet">
/// <item>una Sesión Privilegiada vigente, revalidada contra la base en este comando, con la
/// capacidad <see cref="CapacidadPrivilegio.AjusteSegundoFactorTenant"/>: concesión por Tenant,
/// emitida por un AdminPlataforma a otro usuario de plataforma, nunca global;</item>
/// <item>sin simulación de usuario: el Actor real es quien cambia el ajuste;</item>
/// <item>el Tenant operado es el Tenant objetivo de la sesión, y no es un Tenant de plataforma (sus
/// cuentas son las de los Actores de Plataforma TALVEG);</item>
/// <item>un motivo con la referencia de la petición.</item>
/// </list>
/// Fuera de una Sesión Privilegiada no pasa nadie: <c>AutorizacionEscrituraBehavior</c> dejaría pasar
/// el rol de escritura de un usuario de negocio, y por eso la primera comprobación de este handler es
/// la sesión.
/// </para>
///
/// <para>
/// <b>Escritura y auditoría.</b> La conexión de la sesión lleva <c>cae_app_soporte</c> (solo
/// <c>SELECT</c>). El acto lo hace <c>app_cambiar_segundo_factor_tenant_por_plataforma</c>, que repite
/// en la base cada precondición, con la auditoría en la misma transacción y en el Tenant afectado:
/// Actor real, <c>ViaAcceso = SesionPrivilegiada</c>, la sesión que lo ampara, el valor anterior, el
/// nuevo y el motivo. Las sesiones del Tenant lo reciben en la siguiente revalidación de su sello,
/// igual que con el Administrador.
/// </para>
/// </summary>
/// <param name="ExigeSegundoFactorTodosLosPerfiles">El valor nuevo de la exigencia.</param>
/// <param name="Motivo">La referencia de la petición (ticket o descripción), obligatoria. Se guarda en
/// la auditoría.</param>
public record CambiarSegundoFactorDelTenantPorPlataformaCommand(bool ExigeSegundoFactorTodosLosPerfiles, string Motivo)
    : ICommand, IComandoDeAjusteSegundoFactorTenant;

public class CambiarSegundoFactorDelTenantPorPlataformaCommandValidator
    : AbstractValidator<CambiarSegundoFactorDelTenantPorPlataformaCommand>
{
    /// <summary>Mismo techo que la función de la base.</summary>
    public const int LongitudMaximaMotivo = 500;

    public CambiarSegundoFactorDelTenantPorPlataformaCommandValidator()
    {
        RuleFor(c => c.Motivo)
            .Must(m => !string.IsNullOrWhiteSpace(m))
            .WithMessage("Indica el motivo y la referencia de la petición.")
            .MaximumLength(LongitudMaximaMotivo)
            .WithMessage($"El motivo no puede superar {LongitudMaximaMotivo} caracteres.");
    }
}

public class CambiarSegundoFactorDelTenantPorPlataformaCommandHandler(
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ITenantActual tenantActual,
    ITenantsQueryContext tenants,
    IAjusteSegundoFactorPorPlataforma ajuste)
    : IRequestHandler<CambiarSegundoFactorDelTenantPorPlataformaCommand, Result>
{
    internal static readonly Error SoloPlataformaConSesion = Error.Crear(
        "SeguridadAcceso.SoloPlataformaConSesionPrivilegiada",
        "Esta acción solo la hace TALVEG desde una sesión de soporte abierta sobre la organización.");

    public async Task<Result> Handle(
        CambiarSegundoFactorDelTenantPorPlataformaCommand request, CancellationToken cancellationToken)
    {
        // Sin sesión no hay camino, aunque el rol de quien llama escriba.
        if (await sesionPrivilegiadaActual.RevalidarAsync(cancellationToken) is not { } sesion)
            return Result.Fallo(SoloPlataformaConSesion);

        if (sesion.Capacidad != CapacidadPrivilegio.AjusteSegundoFactorTenant)
            return Result.Fallo(Error.Crear(
                "SeguridadAcceso.SesionSinCapacidad",
                "Esta sesión de soporte no permite cambiar la verificación en dos pasos. Hace falta una concesión específica para esta organización."));

        if (sesion.UsuarioSimuladoId is not null)
            return Result.Fallo(Error.Crear(
                "SeguridadAcceso.ActorNoTitular",
                "Cambiar la verificación en dos pasos no se hace simulando a otro usuario."));

        if (tenantActual.TenantId != sesion.TenantObjetivoId)
            return Result.Fallo(Error.Crear(
                "SeguridadAcceso.FueraDelTenantObjetivo",
                "Esta sesión solo puede actuar en la organización sobre la que se abrió."));

        // Las cuentas del Tenant de plataforma son las de los Actores de Plataforma TALVEG: esta vía
        // no rebaja la suya. EsPlataforma aquí solo restringe, no concede nada.
        if (await tenants.Tenants.AnyAsync(t => t.Id == sesion.TenantObjetivoId && t.EsPlataforma, cancellationToken))
            return Result.Fallo(Error.Crear(
                "SeguridadAcceso.TenantDePlataforma",
                "La verificación en dos pasos de TALVEG no se cambia por esta vía."));

        return await ajuste.CambiarAsync(
            sesion.SesionId, request.ExigeSegundoFactorTodosLosPerfiles, request.Motivo.Trim(), cancellationToken);
    }
}
