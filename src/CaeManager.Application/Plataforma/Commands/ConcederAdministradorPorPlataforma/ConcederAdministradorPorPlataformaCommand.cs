using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Plataforma.Commands.ConcederAdministradorPorPlataforma;

/// <summary>
/// Un Actor de Plataforma TALVEG concede Administrador a una cuenta de un Tenant ya existente
/// (decisión del 2026-10-30): la salida de un Tenant sin Administrador que pueda actuar, por
/// ejemplo con su único Administrador sin activar. El Administrador del Tenant o el del Operador
/// CAE externo usan <c>EditarUsuarioCommand</c>; esta vía es de otro plano.
///
/// <para>
/// El actor no es Gestor CAE, ni Operador CAE, ni Administrador del Tenant, y este comando no le
/// da ningún rol a él. Lo autoriza, todo a la vez:
/// <list type="bullet">
/// <item>una Sesión Privilegiada vigente, revalidada contra la base en este comando, con la
/// capacidad <see cref="CapacidadPrivilegio.ConcesionAdministrador"/>: concesión por Tenant,
/// emitida por un AdminPlataforma a otro usuario de plataforma, nunca global;</item>
/// <item>sin simulación de usuario: el Actor real es quien concede;</item>
/// <item>el Tenant operado es el Tenant objetivo de la sesión, y la cuenta es de ese Tenant;</item>
/// <item>un motivo con la referencia de la petición.</item>
/// </list>
/// Fuera de una Sesión Privilegiada no pasa nadie: <c>AutorizacionEscrituraBehavior</c> dejaría
/// pasar el rol de escritura de un usuario de negocio, y por eso la primera comprobación de este
/// handler es la sesión.
/// </para>
///
/// <para>
/// <b>Escritura y auditoría.</b> La conexión de la sesión lleva <c>cae_app_soporte</c> (solo
/// <c>SELECT</c>). El acto lo hace <c>app_conceder_administrador_por_plataforma</c>, que repite en
/// la base cada precondición, deja Administrador como único rol y rota el sello, con la auditoría
/// en la misma transacción, en el Tenant afectado: Actor real,
/// <c>ViaAcceso = SesionPrivilegiada</c>, la sesión que lo ampara y el motivo.
/// </para>
/// </summary>
/// <param name="UsuarioId">La cuenta del Tenant que pasa a ser Administrador.</param>
/// <param name="Motivo">La referencia de la petición (ticket o descripción), obligatoria. Se
/// guarda en la auditoría.</param>
public record ConcederAdministradorPorPlataformaCommand(Guid UsuarioId, string Motivo)
    : ICommand, IComandoDeConcesionAdministrador;

public class ConcederAdministradorPorPlataformaCommandValidator
    : AbstractValidator<ConcederAdministradorPorPlataformaCommand>
{
    /// <summary>Mismo techo que la función de la base.</summary>
    public const int LongitudMaximaMotivo = 500;

    public ConcederAdministradorPorPlataformaCommandValidator()
    {
        RuleFor(c => c.UsuarioId).NotEmpty().WithMessage("Indica la cuenta que pasa a ser Administrador.");
        RuleFor(c => c.Motivo)
            .Must(m => !string.IsNullOrWhiteSpace(m))
            .WithMessage("Indica el motivo y la referencia de la petición.")
            .MaximumLength(LongitudMaximaMotivo)
            .WithMessage($"El motivo no puede superar {LongitudMaximaMotivo} caracteres.");
    }
}

public class ConcederAdministradorPorPlataformaCommandHandler(
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ITenantActual tenantActual,
    IConcesionAdministradorPorPlataforma concesion)
    : IRequestHandler<ConcederAdministradorPorPlataformaCommand, Result>
{
    internal static readonly Error SoloPlataformaConSesion = Error.Crear(
        "Usuarios.SoloPlataformaConSesionPrivilegiada",
        "Esta acción solo la hace TALVEG desde una sesión de soporte abierta sobre la organización.");

    public async Task<Result> Handle(
        ConcederAdministradorPorPlataformaCommand request, CancellationToken cancellationToken)
    {
        // Sin sesión no hay camino, aunque el rol de quien llama escriba.
        if (await sesionPrivilegiadaActual.RevalidarAsync(cancellationToken) is not { } sesion)
            return Result.Fallo(SoloPlataformaConSesion);

        if (sesion.Capacidad != CapacidadPrivilegio.ConcesionAdministrador)
            return Result.Fallo(Error.Crear(
                "Usuarios.SesionSinCapacidad",
                "Esta sesión de soporte no permite conceder Administrador. Hace falta una concesión específica para esta organización."));

        if (sesion.UsuarioSimuladoId is not null)
            return Result.Fallo(Error.Crear(
                "Usuarios.ActorNoTitular",
                "Conceder Administrador no se hace simulando a otro usuario."));

        if (tenantActual.TenantId != sesion.TenantObjetivoId)
            return Result.Fallo(Error.Crear(
                "Usuarios.FueraDelTenantObjetivo",
                "Esta sesión solo puede actuar en la organización sobre la que se abrió."));

        return await concesion.ConcederAsync(
            sesion.SesionId, request.UsuarioId, request.Motivo.Trim(), cancellationToken);
    }
}
