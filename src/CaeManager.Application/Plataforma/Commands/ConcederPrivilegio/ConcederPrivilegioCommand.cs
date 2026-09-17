using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Plataforma.Commands.ConcederPrivilegio;

/// <summary>
/// Un Actor de Plataforma TALVEG con <see cref="CapacidadPrivilegio.AdminPlataforma"/>
/// concede la capacidad <see cref="CapacidadPrivilegio.Aprovisionamiento"/> a
/// OTRO usuario de plataforma, sobre un tenant concreto (PD-A3).
///
/// <para>
/// <b>Segunda vía de concesión, no una generalización de la primera.</b>
/// <see cref="AutoConcederPrivilegio.AutoConcederPrivilegioCommand"/> sigue
/// siendo el único punto donde alguien se concede algo a sí mismo, y su
/// contrato no cambia. Este comando es la vía —deliberadamente distinta,
/// deliberadamente más estrecha— por la que un tercero recibe una capacidad:
/// solo <see cref="CapacidadPrivilegio.Aprovisionamiento"/>, nunca
/// <c>AdminPlataforma</c> ni <c>BreakGlass</c> ni <c>SoporteLectura</c>. La
/// matriz cerrada de auto-concesión (<see cref="IAutorizacionAutoConcesion"/>)
/// NO se toca: sigue rechazando <c>Aprovisionamiento</c> para cualquiera,
/// incluida la raíz de bootstrap.
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
public record ConcederPrivilegioCommand(
    Guid UsuarioPlataformaBeneficiarioId,
    Guid TenantObjetivoId,
    int DiasDeVigencia,
    string Motivo) : ICommand<Guid>;

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
        // conceder acceso de aprovisionamiento sobre el propio tenant de
        // plataforma no tendría sentido — el aprovisionamiento es siempre
        // sobre un tenant ajeno.
        if (!await ReglaTenantObjetivoAjeno.SeCumpleAsync(currentUserService, request.TenantObjetivoId))
            return Result.Fallo<Guid>(Error.Crear(
                "ConcesionPrivilegio.TenantPropio",
                "No se concede aprovisionamiento sobre el propio tenant de plataforma."));

        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            usuarioPlataformaId: request.UsuarioPlataformaBeneficiarioId,
            CapacidadPrivilegio.Aprovisionamiento,
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
