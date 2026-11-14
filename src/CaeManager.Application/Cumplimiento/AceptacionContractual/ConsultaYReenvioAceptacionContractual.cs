using CaeManager.Application.Common;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento;
using MediatR;

namespace CaeManager.Application.Cumplimiento.AceptacionContractual;

/// <summary>
/// Quién puede consultar el registro de aceptaciones del Tenant propietario y pedir el reenvío del acuse:
/// su Administrador, con el Tenant propietario como Tenant de origen. El rol efectivo no basta: un
/// Administrador del Operador CAE externo que opera el Tenant por Asignación de Operación no es quien
/// aceptó ni puede pedir que el texto se le envíe (regla 3 de la #2009).
/// </summary>
internal static class AutoridadSobreAceptacionContractual
{
    public static readonly Error SoloAdministradorDelTenantPropietario = Error.Crear(
        "AceptacionContractual.SoloAdministradorDelTenantPropietario",
        "Solo el Administrador del Tenant propietario puede consultar sus aceptaciones.");

    public static async Task<bool> EsAdministradorDelTenantPropietarioAsync(ICurrentUserService usuarioActual, ITenantActual tenantActual) =>
        tenantActual.TenantId is not null
        && await usuarioActual.ObtenerRolEfectivoAsync() == "Administrador"
        && !RolesReservadosAlTenantDeOrigen.EsContextoCruzado(await usuarioActual.ObtenerTenantOrigenIdAsync(), tenantActual.TenantId);
}

public record AceptacionContractualTenantDto(
    Guid Id,
    string Documento,
    string Version,
    string HashSha256,
    Guid UsuarioId,
    bool DeclaraFacultades,
    DateTime AceptadaEnUtc,
    decimal? ImportePermanencia,
    DateTime? UltimoAcuseEnUtc);

/// <summary>
/// El registro consultable de la #2009: todas las aceptaciones contractuales del Tenant propietario,
/// de la más reciente a la más antigua, con su último acuse. No devuelve la IP: es un dato personal de
/// quien aceptó que el registro guarda como evidencia y que esta vista no necesita.
/// </summary>
public record ObtenerAceptacionesContractualesTenantQuery : IRequest<Result<IReadOnlyList<AceptacionContractualTenantDto>>>;

public class ObtenerAceptacionesContractualesTenantQueryHandler(
    ICurrentUserService usuarioActual,
    ITenantActual tenantActual,
    IAceptacionContractualTenantRepository repositorio)
    : IRequestHandler<ObtenerAceptacionesContractualesTenantQuery, Result<IReadOnlyList<AceptacionContractualTenantDto>>>
{
    public async Task<Result<IReadOnlyList<AceptacionContractualTenantDto>>> Handle(
        ObtenerAceptacionesContractualesTenantQuery request, CancellationToken cancellationToken)
    {
        if (!await AutoridadSobreAceptacionContractual.EsAdministradorDelTenantPropietarioAsync(usuarioActual, tenantActual))
            return Result.Fallo<IReadOnlyList<AceptacionContractualTenantDto>>(AutoridadSobreAceptacionContractual.SoloAdministradorDelTenantPropietario);

        var aceptaciones = await repositorio.ListarAsync(cancellationToken);
        var acuses = await repositorio.UltimoAcusePorAceptacionAsync(cancellationToken);
        IReadOnlyList<AceptacionContractualTenantDto> dto = aceptaciones
            .Select(a => new AceptacionContractualTenantDto(
                a.Id, a.Documento, a.Version, a.HashSha256, a.UsuarioId, a.DeclaraFacultades, a.AceptadaEnUtc,
                a.ImportePermanencia, acuses.TryGetValue(a.Id, out var acuse) ? acuse : null))
            .ToList();
        return Result.Exito(dto);
    }
}

/// <summary>
/// Reenvía el acuse con el texto aceptado al correo de quien lo pide, que tiene que ser el Administrador
/// del Tenant propietario. Nunca a otra dirección: así el texto no sale hacia un correo que nadie del
/// Tenant propietario ha verificado.
/// </summary>
public record ReenviarAcuseAceptacionContractualCommand : ICommand;

public class ReenviarAcuseAceptacionContractualCommandHandler(
    ICurrentUserService usuarioActual,
    ITenantActual tenantActual,
    IEnvioAcuseAceptacionContractual envio)
    : IRequestHandler<ReenviarAcuseAceptacionContractualCommand, Result>
{
    public async Task<Result> Handle(ReenviarAcuseAceptacionContractualCommand request, CancellationToken cancellationToken)
    {
        if (!await AutoridadSobreAceptacionContractual.EsAdministradorDelTenantPropietarioAsync(usuarioActual, tenantActual))
            return Result.Fallo(AutoridadSobreAceptacionContractual.SoloAdministradorDelTenantPropietario);

        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(AutoridadSobreAceptacionContractual.SoloAdministradorDelTenantPropietario);

        return await envio.EnviarAsync(usuarioId.Value, cancellationToken);
    }
}
