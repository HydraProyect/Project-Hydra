using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Plataforma.Commands.ConcederPrivilegio;

/// <summary>
/// Un Actor de Plataforma TALVEG con <see cref="CapacidadPrivilegio.AdminPlataforma"/>
/// concede a OTRO usuario de plataforma, sobre un tenant concreto, una de las
/// dos capacidades acotadas que admiten concesión por un tercero:
/// <see cref="CapacidadPrivilegio.Aprovisionamiento"/> (PD-A3) o
/// <see cref="CapacidadPrivilegio.RestablecimientoSegundoFactor"/> (ADR-011 § 8.7, punto 3:
/// Soporte TALVEG restablece la 2FA del Administrador único de ese Tenant).
///
/// <para>
/// <b>Segunda vía de concesión, no una generalización de la primera.</b>
/// <see cref="AutoConcederPrivilegio.AutoConcederPrivilegioCommand"/> sigue
/// siendo el único punto donde alguien se concede algo a sí mismo, y su
/// contrato no cambia. Este comando es la vía —deliberadamente distinta,
/// deliberadamente más estrecha— por la que un tercero recibe una capacidad:
/// solo <see cref="CapacidadPrivilegio.Aprovisionamiento"/> y
/// <see cref="CapacidadPrivilegio.RestablecimientoSegundoFactor"/>, siempre sobre un
/// tenant concreto y nunca <c>AdminPlataforma</c> ni <c>BreakGlass</c> ni
/// <c>SoporteLectura</c>. La matriz cerrada de auto-concesión
/// (<see cref="IAutorizacionAutoConcesion"/>) NO se toca: sigue rechazando las dos
/// para cualquiera, incluida la raíz de bootstrap. Para el restablecimiento de 2FA
/// eso es lo que da los cuatro ojos: quien la ejerce nunca es quien la concede.
/// </para>
///
/// <para>
/// <b>El beneficiario SÍ es un parámetro aquí</b> —al contrario que en la
/// auto-concesión, donde no serlo es la garantía—, porque es exactamente lo
/// que este comando existe para permitir. La garantía la aporta otra cosa:
/// <see cref="IAutorizacionAdminPlataforma.PuedeSobreTenantAsync"/> exige que
/// quien concede tenga <c>AdminPlataforma</c> vigente sobre ESE tenant, y la
/// política RLS (<c>RlsConcesionPorAdminDePlataforma</c>) impone la misma
/// prueba una segunda vez en la base de datos.
/// </para>
///
/// <para>
/// Beneficiario ≠ concedente: quien quiera concederse algo a sí mismo usa la
/// vía de autoconcesión, con su propia matriz y su propio contrato. Mezclar
/// los dos caminos en una sola operación diluiría la garantía de "yo → otro"
/// de aquella.
/// </para>
/// </summary>
/// <param name="UsuarioPlataformaBeneficiarioId">A quién se concede.</param>
/// <param name="TenantObjetivoId">Sobre qué tenant.</param>
/// <param name="DiasDeVigencia">Cuánto vive la concesión.</param>
/// <param name="Motivo">Por qué se concede — obligatorio, a diferencia de la
/// auto-concesión: aquí el motivo no es "equipo unipersonal", es una decisión
/// que involucra a otra persona y tiene que quedar dicha.</param>
/// <param name="Capacidad">Qué se concede: <see cref="CapacidadesConcedibles"/>.</param>
public record ConcederPrivilegioCommand(
    Guid UsuarioPlataformaBeneficiarioId,
    Guid TenantObjetivoId,
    int DiasDeVigencia,
    string Motivo,
    CapacidadPrivilegio Capacidad) : ICommand<Guid>
{
    /// <summary>
    /// Lista cerrada. Una capacidad nueva no entra aquí por existir en el enum:
    /// hace falta ampliar también el <c>WITH CHECK</c> de la política de
    /// las concesiones de privilegio, que impone la misma lista en la base.
    /// </summary>
    public static readonly IReadOnlySet<CapacidadPrivilegio> CapacidadesConcedibles =
        new HashSet<CapacidadPrivilegio>
        {
            CapacidadPrivilegio.Aprovisionamiento,
            CapacidadPrivilegio.RestablecimientoSegundoFactor,
        };
}

public class ConcederPrivilegioCommandValidator : AbstractValidator<ConcederPrivilegioCommand>
{
    /// <summary>
    /// Mismo techo que la auto-concesión (<c>AutoConcederPrivilegioCommandValidator.MaximoDiasDeVigencia</c>):
    /// son la misma decisión de producto —cuánto puede vivir una concesión de
    /// plataforma— vista desde dos comandos. Duplicado a propósito (ambos son
    /// <c>const</c> de su propio validador, no hay un tercer sitio del que
    /// colgar la constante sin acoplar los dos comandos entre sí); un test de
    /// coherencia (<c>VentanaDePrivilegioCuatroHorasTests</c>) vigila que no
    /// diverjan.
    /// </summary>
    public const int MaximoDiasDeVigencia = 90;

    public ConcederPrivilegioCommandValidator()
    {
        RuleFor(c => c.UsuarioPlataformaBeneficiarioId).NotEmpty();
        RuleFor(c => c.TenantObjetivoId).NotEmpty();
        RuleFor(c => c.Capacidad)
            .Must(ConcederPrivilegioCommand.CapacidadesConcedibles.Contains)
            .WithMessage("Solo se conceden a otra persona el aprovisionamiento y el restablecimiento de la verificación en dos pasos.");

        RuleFor(c => c.DiasDeVigencia)
            .InclusiveBetween(1, MaximoDiasDeVigencia)
            .WithMessage($"La vigencia de la concesión debe estar entre 1 y {MaximoDiasDeVigencia} días.");

        RuleFor(c => c.Motivo)
            .NotEmpty()
            .MaximumLength(ConcesionPrivilegio.LongitudMaximaMotivo)
            .WithMessage("El motivo es obligatorio y no puede superar " +
                         $"{ConcesionPrivilegio.LongitudMaximaMotivo} caracteres.");
    }
}

public class ConcederPrivilegioCommandHandler(
    IPlataformaWriter writer,
    IAutorizacionAdminPlataforma autorizacionAdminPlataforma,
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ConcederPrivilegioCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ConcederPrivilegioCommand request, CancellationToken cancellationToken)
    {
        var concedenteId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (concedenteId is null)
            return Result.Fallo<Guid>(Error.Crear(
                "ConcesionPrivilegio.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        // Beneficiario ≠ concedente: quien quiera concederse algo a sí mismo
        // usa AutoConcederPrivilegioCommand, con su propio contrato.
        if (request.UsuarioPlataformaBeneficiarioId == concedenteId.Value)
            return Result.Fallo<Guid>(Error.Crear(
                "ConcesionPrivilegio.BeneficiarioEsConcedente",
                "Para concederte un privilegio a ti mismo usa la vía de autoconcesión."));

        // 2FA: crear autoridad con una cuenta comprometida por contraseña
        // sola es justo lo que esta ceremonia existe para poder contener —
        // mismo motivo que en la auto-concesión.
        if (!await currentUserService.TieneDobleFactorActivoAsync())
            return Result.Fallo<Guid>(Error.Crear(
                "ConcesionPrivilegio.SinDobleFactor",
                "Activa la autenticación en dos pasos en tu cuenta antes de conceder privilegios."));

        // La autoridad para conceder: AdminPlataforma vigente SOBRE ESE
        // tenant, no en abstracto. La política RLS (RlsConcesionPorAdminDePlataforma)
        // impone la misma prueba una segunda vez contra base de datos.
        if (!await autorizacionAdminPlataforma.PuedeSobreTenantAsync(
                concedenteId.Value, request.TenantObjetivoId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "ConcesionPrivilegio.NoAutorizado",
                "No tienes autorización de plataforma sobre ese tenant."));

        // Mismo control que la auto-concesión: el privilegio de plataforma no
        // se ejerce sobre la propia casa. Se evalúa contra el tenant de
        // origen de quien concede (no del beneficiario, que puede diferir):
        // conceder aprovisionamiento o restablecimiento de 2FA sobre el propio
        // tenant de plataforma no tendría sentido — los dos se ejercen siempre
        // sobre un tenant ajeno.
        if (!await ReglaTenantObjetivoAjeno.SeCumpleAsync(currentUserService, request.TenantObjetivoId))
            return Result.Fallo<Guid>(Error.Crear(
                "ConcesionPrivilegio.TenantPropio",
                "No se conceden privilegios sobre el propio tenant de plataforma."));

        // Toda capacidad que abre una Sesión Privilegiada solo la ejerce un Actor
        // de Plataforma TALVEG: el beneficiario tiene que ser una cuenta del
        // Tenant de origen de quien concede. Bajo la RLS de cuentas, una de otro
        // Tenant ni se ve (null). Se ata a CapacidadesQuePuedenAbrirSesion y no a
        // una capacidad concreta para que una capacidad concedible nueva que
        // abra sesión herede el control sin que nadie tenga que acordarse; hoy
        // cubre Aprovisionamiento y RestablecimientoSegundoFactor. Para el
        // restablecimiento, la función de base lo vuelve a exigir al ejercerla.
        if (CapacidadesQuePuedenAbrirSesion.Admite(request.Capacidad))
        {
            var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
            var tenantBeneficiarioId = await directorioUsuarios.ObtenerTenantDeUsuarioAsync(
                request.UsuarioPlataformaBeneficiarioId, cancellationToken);
            if (tenantOrigenId is null || tenantBeneficiarioId != tenantOrigenId)
                return Result.Fallo<Guid>(Error.Crear(
                    "ConcesionPrivilegio.BeneficiarioNoEsDePlataforma",
                    "Esta capacidad solo se concede a personas de Soporte TALVEG."));
        }

        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            usuarioPlataformaId: request.UsuarioPlataformaBeneficiarioId,
            request.Capacidad,
            tenantIds: [request.TenantObjetivoId],
            vigenciaDesde: ahora,
            vigenciaHasta: ahora.AddDays(request.DiasDeVigencia),
            concedidaPorUsuarioId: concedenteId.Value,
            motivoConcesion: request.Motivo);

        writer.AnadirConcesion(concesion);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(concesion.Id);
    }
}
