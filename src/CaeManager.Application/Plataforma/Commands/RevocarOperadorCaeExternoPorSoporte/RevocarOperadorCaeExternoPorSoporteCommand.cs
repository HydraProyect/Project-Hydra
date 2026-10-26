using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Plataforma.Commands.RevocarOperadorCaeExternoPorSoporte;

/// <summary>
/// Soporte TALVEG revoca la Delegación de un Operador CAE externo sobre un Tenant propietario
/// <b>a petición de ese Tenant propietario</b> (petición del propietario del 2026-10-26,
/// enmienda de ADR-004 § 12.2). Es la salida del Administrador del Tenant propietario, que
/// no puede revocarla él mismo: la revocación normal
/// (<c>DesactivarDelegacionTenantCommand</c>) es solo del equipo del Operador CAE externo con
/// alcance sobre ese Tenant (<c>EquipoDelOperadorCaeExterno</c>).
///
/// <para>
/// <b>Otro plano y otra vía, no una ampliación de aquella.</b> Soporte TALVEG es un Actor de
/// Plataforma TALVEG (plano 3): no es Gestor CAE, ni Operador CAE, ni Administrador del Tenant,
/// y este comando no le da ningún rol. Lo autoriza, todo a la vez:
/// <list type="bullet">
/// <item>una Sesión Privilegiada vigente, revalidada contra la base en este comando, con la
/// capacidad <see cref="CapacidadPrivilegio.RevocacionOperadorCaeExterno"/>: concesión por
/// Tenant, emitida por un AdminPlataforma a otro usuario de plataforma, nunca global. La
/// concesión global de <c>SoporteLectura</c> no basta;</item>
/// <item>sin simulación de usuario: el Actor real es quien revoca;</item>
/// <item>el Tenant operado es el Tenant objetivo de la sesión, y la Delegación es de ese
/// Tenant propietario y de propósito Operador CAE externo;</item>
/// <item>un motivo con la referencia de la petición del Tenant propietario.</item>
/// </list>
/// Fuera de una Sesión Privilegiada no pasa nadie, tampoco el Administrador del Tenant
/// propietario: <c>AutorizacionEscrituraBehavior</c> dejaría pasar su rol de escritura, y por
/// eso la primera comprobación de este handler es la sesión.
/// </para>
///
/// <para>
/// <b>Escritura y auditoría.</b> La conexión de la sesión lleva <c>cae_app_soporte</c> (solo
/// <c>SELECT</c>). El acto lo hace <c>app_revocar_operador_cae_externo_por_soporte</c>, que
/// repite en la base cada precondición y aplica los mismos efectos que la revocación normal
/// (Delegación inactiva, Asignación de Operación externa cerrada como <c>Revocada</c> y sus
/// carteras cerradas en cascada recordando quién era principal), con la auditoría en la misma
/// transacción, en el Tenant propietario: Actor real, <c>ViaAcceso = SesionPrivilegiada</c>,
/// la sesión que lo ampara y el motivo.
/// </para>
/// </summary>
/// <param name="DelegacionTenantId">La Delegación (Asignación de Operación delegada) que se revoca.</param>
/// <param name="Motivo">La referencia de la petición del Tenant propietario (ticket o
/// descripción), obligatoria. Se guarda en la auditoría.</param>
public record RevocarOperadorCaeExternoPorSoporteCommand(Guid DelegacionTenantId, string Motivo)
    : ICommand, IComandoDeRevocacionOperadorCaeExterno;

public class RevocarOperadorCaeExternoPorSoporteCommandValidator
    : AbstractValidator<RevocarOperadorCaeExternoPorSoporteCommand>
{
    /// <summary>Mismo techo que la función de la base.</summary>
    public const int LongitudMaximaMotivo = 500;

    public RevocarOperadorCaeExternoPorSoporteCommandValidator()
    {
        RuleFor(c => c.DelegacionTenantId).NotEmpty().WithMessage("Indica la delegación que quieres revocar.");
        RuleFor(c => c.Motivo)
            .Must(m => !string.IsNullOrWhiteSpace(m))
            .WithMessage("Indica el motivo y la referencia de la petición del Tenant propietario.")
            .MaximumLength(LongitudMaximaMotivo)
            .WithMessage($"El motivo no puede superar {LongitudMaximaMotivo} caracteres.");
    }
}

public class RevocarOperadorCaeExternoPorSoporteCommandHandler(
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ITenantActual tenantActual,
    IRevocacionOperadorCaeExternoPorSoporte revocacion)
    : IRequestHandler<RevocarOperadorCaeExternoPorSoporteCommand, Result>
{
    internal static readonly Error SoloSoporteConSesion = Error.Crear(
        "DelegacionTenant.SoloSoporteConSesionPrivilegiada",
        "Esta acción solo la hace Soporte TALVEG desde una sesión de soporte abierta sobre la organización.");

    public async Task<Result> Handle(
        RevocarOperadorCaeExternoPorSoporteCommand request, CancellationToken cancellationToken)
    {
        // Sin sesión no hay camino: ni un usuario de negocio ni el Administrador del Tenant
        // propietario usan esta vía, aunque su rol escriba.
        if (await sesionPrivilegiadaActual.RevalidarAsync(cancellationToken) is not { } sesion)
            return Result.Fallo(SoloSoporteConSesion);

        if (sesion.Capacidad != CapacidadPrivilegio.RevocacionOperadorCaeExterno)
            return Result.Fallo(Error.Crear(
                "DelegacionTenant.SesionSinCapacidad",
                "Esta sesión de soporte no permite revocar accesos. Hace falta una concesión específica para esta organización."));

        if (sesion.UsuarioSimuladoId is not null)
            return Result.Fallo(Error.Crear(
                "DelegacionTenant.ActorNoTitular",
                "La revocación no se hace simulando a otro usuario."));

        if (tenantActual.TenantId != sesion.TenantObjetivoId)
            return Result.Fallo(Error.Crear(
                "DelegacionTenant.FueraDelTenantObjetivo",
                "Esta sesión solo puede actuar en la organización sobre la que se abrió."));

        return await revocacion.RevocarAsync(
            sesion.SesionId, request.DelegacionTenantId, request.Motivo.Trim(), cancellationToken);
    }
}
