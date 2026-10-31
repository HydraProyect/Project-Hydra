using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plataforma.Commands.AutoConcederPrivilegio;

/// <summary>
/// Un usuario de plataforma se concede a <b>sí mismo</b> una concesión de
/// privilegio de alcance global: <see cref="CapacidadPrivilegio.AdminPlataforma"/>
/// (el acto fundacional) o <see cref="CapacidadPrivilegio.SoporteLectura"/>
/// (Soporte TALVEG universal, ADR-011 § 8.9).
///
/// <para>
/// <b>Sin Tenant objetivo desde 2026-09-25.</b> El propietario decidió que
/// Soporte TALVEG no pida acceso Tenant a Tenant: la concesión de lectura es
/// global y el control por Tenant pasa entero a la apertura de cada
/// <see cref="SesionPrivilegiada"/> (Tenant ajeno, motivo, ventana, 2FA). Por
/// eso el comando ya no tiene parámetro de Tenant: una concesión global con un
/// Tenant "de adorno" sería un dato que afirma algo que el sistema no aplica.
/// </para>
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
/// ADR-011 § 8.5.7 acepta la auto-concesión mientras el equipo de plataforma
/// sea unipersonal, con la autoría registrada desde el primer día; la
/// segregación de funciones —que una segunda persona apruebe— llega cuando haya
/// equipo. La base impone la misma matriz por su cuenta: desde
/// <c>PartePoliticaConcesionesPorOperacion</c>, la política
/// <c>privilegio_autoconcesion</c> solo admite una fila propia y global que sea
/// <c>SoporteLectura</c> ordinaria con fin (si ya tienes <c>AdminPlataforma</c>
/// vigente) o la <c>AdminPlataforma</c> fundacional (si eres la raíz y el
/// bootstrap no está consumido). Con fin, además, dentro del tope de vigencia y
/// con el inicio junto al momento del alta
/// (<c>TopeVigenciaConcesionEnPoliticasDeAlta</c>).
/// </para>
/// </summary>
/// <param name="Capacidad">Qué podrá hacer la sesión que se abra bajo esta concesión.</param>
/// <param name="DiasDeVigencia">
/// Cuánto vive la concesión. Distinto de la ventana de cada sesión. Para
/// SoporteLectura es además el plazo de renovación de la llave universal;
/// para AdminPlataforma se ignora porque la concesión raíz no tiene fecha de fin.
/// </param>
public record AutoConcederPrivilegioCommand(
    CapacidadPrivilegio Capacidad,
    int DiasDeVigencia) : ICommand<Guid>;

public class AutoConcederPrivilegioCommandValidator : AbstractValidator<AutoConcederPrivilegioCommand>
{
    public AutoConcederPrivilegioCommandValidator()
    {
        // La concesión raíz de AdminPlataforma nace sin fecha de fin y no usa este dato.
        RuleFor(c => c.DiasDeVigencia)
            .InclusiveBetween(1, ConcesionPrivilegio.MaximoDiasDeVigencia)
            .WithMessage($"La vigencia de la concesión debe estar entre 1 y {ConcesionPrivilegio.MaximoDiasDeVigencia} días.")
            .When(c => c.Capacidad != CapacidadPrivilegio.AdminPlataforma);

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

        // Sin regla de Tenant ajeno aquí: una concesión global no nombra ningún
        // Tenant. La regla no desaparece, se queda donde se usa la llave —la
        // apertura de cada sesión, AbrirSesionPrivilegiadaCommand precondición
        // 2—, y es precisamente la que impide que la concesión universal cubra
        // la propia organización de quien la tiene.
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SoporteLecturaGlobal(
            // Beneficiario y autor son el mismo, y los dos salen de la sesión.
            // No hay forma de que difieran desde este camino.
            usuarioPlataformaId: usuarioId.Value,
            vigenciaDesde: ahora,
            vigenciaHasta: ahora.AddDays(request.DiasDeVigencia),
            concedidaPorUsuarioId: usuarioId.Value,
            motivoConcesion: "Auto-concesión de Soporte TALVEG universal (equipo de plataforma unipersonal, " +
                             "ADR-011 § 8.5.7 y § 8.9).");

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

        // Un solo SaveChanges con un acoplamiento que conviene saber: la política
        // privilegio_autoconcesion exige el bootstrap SIN consumir al insertar la
        // concesión, y funciona porque EF Core ejecuta los comandos independientes
        // ordenados por nombre de tabla (la de concesiones antes que la del
        // estado del bootstrap). Si se añade una relación entre las dos
        // tablas o cambia ese orden, el arranque falla cerrado con 42501;
        // ConcesionesPrivilegioPorOperacionBajoRuntimeTests lo vigila.
        estado.Consumir(ahora);
        writer.AnadirConcesion(concesion);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(concesion.Id);
    }
}
