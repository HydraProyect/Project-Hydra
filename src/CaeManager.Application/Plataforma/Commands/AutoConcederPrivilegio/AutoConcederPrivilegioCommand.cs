using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Plataforma.Commands.AutoConcederPrivilegio;

/// <summary>
/// Un usuario de plataforma se concede a <b>sí mismo</b> una concesión de
/// privilegio sobre un tenant concreto.
///
/// <para>
/// <b>Es auto-concesión, no "gestión de concesiones" en pequeño.</b> La
/// diferencia no es de tamaño sino de contrato: una operación genérica
/// <c>Conceder(usuario, tenant, capacidad)</c> abriría de golpe las preguntas
/// que este incremento deja fuera — quién puede conceder, a quién, qué
/// capacidades, sobre qué tenants, cómo se revoca, cómo se audita, qué pasa con
/// las concesiones ya existentes. Ese es un incremento propio.
/// </para>
///
/// <para>
/// <b>El beneficiario no es un parámetro.</b> No hay ningún
/// <c>UsuarioPlataformaId</c> en este comando, y es la garantía principal: "yo →
/// otro" no se rechaza, es <i>irrepresentable</i>. Una validación que
/// comprobara <c>beneficiario == usuarioActual</c> sería más débil, porque
/// alguien podría relajarla sin cambiar la forma de la operación. Aquí habría
/// que añadir un parámetro, y eso se ve.
/// </para>
///
/// <para>
/// El ADR § 4bis.7.7 acepta la auto-concesión mientras el equipo de plataforma
/// sea unipersonal, con la autoría registrada desde el primer día; la
/// segregación de funciones —que una segunda persona apruebe— llega cuando haya
/// equipo. Y el <c>WITH CHECK</c> de RLS (F2b-5) no se toca: sigue admitiendo
/// solo filas que nombren a <c>app.usuario_id</c>, que es exactamente lo que
/// esta operación produce.
/// </para>
/// </summary>
/// <param name="TenantObjetivoId">Sobre qué tenant se concede el privilegio.</param>
/// <param name="Capacidad">Qué podrá hacer la sesión que se abra bajo esta concesión.</param>
/// <param name="DiasDeVigencia">Cuánto vive la concesión. Distinto de la ventana de cada sesión.</param>
public record AutoConcederPrivilegioCommand(
    Guid TenantObjetivoId,
    CapacidadPrivilegio Capacidad,
    int DiasDeVigencia) : ICommand<Guid>;

public class AutoConcederPrivilegioCommandValidator : AbstractValidator<AutoConcederPrivilegioCommand>
{
    /// <summary>
    /// Días máximos de vigencia de la concesión. Más generoso que la ventana de
    /// una sesión (30 días) porque son cosas distintas: la concesión dice
    /// durante cuánto tiempo <i>podrías</i> abrir sesiones; la ventana, cuánto
    /// dura cada una.
    /// </summary>
    public const int MaximoDiasDeVigencia = 90;

    public AutoConcederPrivilegioCommandValidator()
    {
        RuleFor(c => c.TenantObjetivoId).NotEmpty();

        RuleFor(c => c.DiasDeVigencia)
            .InclusiveBetween(1, MaximoDiasDeVigencia)
            .WithMessage($"La vigencia de la concesión debe estar entre 1 y {MaximoDiasDeVigencia} días.");

        // Restricción de producto, no invariante de seguridad, y conviene
        // distinguirlo: la seguridad de que una sesión no escriba no depende de
        // esta línea sino de F2b-2 (denegación por vía de acceso) y F2b-3 (rol
        // de BD de solo lectura), que aplican sea cual sea la capacidad.
        //
        // Se acota a SoporteLectura porque es la única capacidad con camino
        // completo: BreakGlass no tiene camino de escritura, Impersonacion no
        // tiene camino de autorización, y AdminPlataforma no da acceso a
        // contenido. Auto-concederse cualquiera de esas tres crearía una fila
        // que afirma algo que el sistema todavía no sabe honrar.
        RuleFor(c => c.Capacidad)
            .Equal(CapacidadPrivilegio.SoporteLectura)
            .WithMessage("Por ahora solo puede concederse acceso de soporte de solo lectura.");
    }
}

public class AutoConcederPrivilegioCommandHandler(
    IPlataformaWriter writer,
    IAutorizacionAperturaSesion autorizacion,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<AutoConcederPrivilegioCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AutoConcederPrivilegioCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<Guid>(Error.Crear(
                "ConcesionPrivilegio.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        // Se reutiliza la autoridad de apertura a propósito, y no porque sea
        // "parecida": con auto-concesión, la concesión no añade autoridad
        // ninguna — es el registro de una autoridad que el usuario ya tiene por
        // la puerta de plataforma. Si puede abrir una sesión sobre ese tenant,
        // puede dejar constancia de que va a hacerlo. Conceder a un tercero sí
        // sería autoridad nueva, y por eso no está aquí.
        if (!await autorizacion.PuedeAbrirAsync(usuarioId.Value, request.TenantObjetivoId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "ConcesionPrivilegio.NoAutorizado",
                "No tienes autorización para concederte acceso de soporte sobre este cliente."));

        // 2FA también aquí: si no, quedaría un camino para dejar la concesión
        // preparada sin segundo factor y activarla después. La ceremonia se
        // comprueba en cada paso que crea autoridad, no solo en el último.
        if (!await currentUserService.TieneDobleFactorActivoAsync())
            return Result.Fallo<Guid>(Error.Crear(
                "ConcesionPrivilegio.SinDobleFactor",
                "Activa la autenticación en dos pasos en tu cuenta antes de concederte acceso de soporte."));

        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            // Beneficiario y autor son el mismo, y los dos salen de la sesión.
            // No hay forma de que difieran desde este camino.
            usuarioPlataformaId: usuarioId.Value,
            request.Capacidad,
            tenantIds: [request.TenantObjetivoId],
            vigenciaDesde: ahora,
            vigenciaHasta: ahora.AddDays(request.DiasDeVigencia),
            concedidaPorUsuarioId: usuarioId.Value,
            motivoConcesion: "Auto-concesión (equipo de plataforma unipersonal, ADR-011 § 4bis.7.7).");

        writer.AnadirConcesion(concesion);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(concesion.Id);
    }
}
