using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

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
        // Solo el acto ordinario tiene tenant: la concesión fundacional es
        // global por definición y ahí un tenant objetivo no significa nada.
        RuleFor(c => c.TenantObjetivoId)
            .NotEmpty()
            .When(c => c.Capacidad != CapacidadPrivilegio.AdminPlataforma);

        RuleFor(c => c.TenantObjetivoId)
            .Empty()
            .When(c => c.Capacidad == CapacidadPrivilegio.AdminPlataforma)
            .WithMessage("La concesión fundacional es global: no lleva tenant objetivo.");

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
        // Dos, y cada una con su propia autoridad — ver IAutorizacionAutoConcesion.
        // Impersonacion y BreakGlass siguen fuera: la primera no tiene camino de
        // autorización y la segunda no tiene camino de escritura, así que
        // emitirlas crearía filas que afirman algo que el sistema no sabe honrar.
        RuleFor(c => c.Capacidad)
            .Must(capacidad => capacidad is CapacidadPrivilegio.SoporteLectura
                                         or CapacidadPrivilegio.AdminPlataforma)
            .WithMessage("Esa capacidad no puede auto-concederse.");
    }
}

public class AutoConcederPrivilegioCommandHandler(
    IPlataformaWriter writer,
    IAutorizacionAutoConcesion autorizacion,
    IPlataformaQueryContext plataformaContext,
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

        // Este es el ACTO FUNDACIONAL de la autoridad, no el registro de una
        // autoridad previa.
        //
        // Antes de A0 esta comprobación reutilizaba la autorización de apertura,
        // con el argumento de que quien ya podía abrir una sesión podía dejar
        // constancia de que iba a hacerlo. Ese argumento murió: desde A0 abrir
        // exige una concesión, así que no hay ninguna autoridad previa de la que
        // esta fila sea mero reflejo. Mantener aquel razonamiento sería
        // circular — conceder exigiría poder abrir, y abrir exigiría la
        // concesión que se está creando.
        //
        //     EsPlataforma  →  primera concesión  →  abrir sesión
        //
        // La raíz de bootstrap existe exactamente para romper ese ciclo, y es
        // la única superficie que le queda a EsPlataforma como autoridad.
        // Conceder a un TERCERO sí sería autoridad nueva y sigue sin existir:
        // este comando solo concede a quien lo invoca.
        // La autoridad depende de QUÉ se pide, no de quién lo pide en abstracto:
        //
        //   AdminPlataforma  ←  identidad raíz designada ∧ bootstrap sin consumir
        //   SoporteLectura   ←  AdminPlataforma vigente
        //
        // Antes de A2 esto era una sola pregunta —"¿perteneces al tenant de
        // plataforma?"— y ese tenant es también el operativo de la empresa, así
        // que cualquier gestor podía acuñarse autoridad. No era un bootstrap:
        // era una carrera de privilegios.
        if (!await autorizacion.PuedeAutoConcederseAsync(usuarioId.Value, request.Capacidad, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "ConcesionPrivilegio.NoAutorizado",
                "No tienes autorización para concederte esa capacidad."));

        // 2FA en los dos caminos: crear autoridad con una cuenta comprometida por
        // contraseña sola es justo el escenario que esta ceremonia existe para
        // poder rastrear y contener.
        if (!await currentUserService.TieneDobleFactorActivoAsync())
            return Result.Fallo<Guid>(Error.Crear(
                "ConcesionPrivilegio.SinDobleFactor",
                "Activa la autenticación en dos pasos en tu cuenta antes de concederte privilegios."));

        if (request.Capacidad == CapacidadPrivilegio.AdminPlataforma)
            return await ArrancarLaPlataformaAsync(usuarioId.Value, cancellationToken);

        if (!await ReglaTenantObjetivoAjeno.SeCumpleAsync(currentUserService, request.TenantObjetivoId))
            return Result.Fallo<Guid>(Error.Crear(
                "ConcesionPrivilegio.TenantPropio",
                "No se concede acceso de soporte sobre tu propia organización."));

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

    /// <summary>
    /// El acto fundacional. Crea la concesión raíz y consume el bootstrap
    /// <b>en el mismo SaveChanges</b>: si fueran dos operaciones, un fallo entre
    /// medias dejaría o una concesión con el bootstrap todavía abierto —dos
    /// raíces posibles— o el bootstrap gastado sin concesión, que con la regla de
    /// no reapertura es irreversible y deja la plataforma sin autoridad.
    /// </summary>
    private async Task<Result<Guid>> ArrancarLaPlataformaAsync(Guid usuarioId, CancellationToken cancellationToken)
    {
        var estado = await plataformaContext.EstadoBootstrapPlataforma
            .FirstOrDefaultAsync(cancellationToken);

        // La autorización ya comprobó que este usuario es la raíz y que queda
        // bootstrap; volver a mirarlo aquí no es redundancia sino la carga del
        // agregado que hay que mutar. Si no está, es que el despliegue nunca
        // designó raíz — y entonces la autorización tampoco habría pasado.
        if (estado is null)
            return Result.Fallo<Guid>(Error.Crear(
                "ConcesionPrivilegio.SinRaizDesignada",
                "Este despliegue no tiene una identidad raíz designada."));

        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.RaizDeBootstrap(usuarioId, ahora, vigenciaHasta: null);

        estado.Consumir(ahora);
        writer.AnadirConcesion(concesion);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(concesion.Id);
    }
}
