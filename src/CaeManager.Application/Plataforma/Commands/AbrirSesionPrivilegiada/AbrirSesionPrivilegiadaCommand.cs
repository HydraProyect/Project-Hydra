using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;

/// <summary>
/// Abre una sesión privilegiada de plataforma sobre un tenant concreto.
///
/// Traslada al plano 3 la ceremonia que la vía heredada
/// (<c>AbrirAccesoSoporteCommand</c>) ya aplicaba en producción, sin perder
/// ninguno de sus controles: motivo obligatorio, ventana acotada, 2FA activa y
/// autoridad sobre el tenant. Lo que cambia es dónde vive cada control y qué
/// entidad lo porta, no cuántos hay.
/// </summary>
public record AbrirSesionPrivilegiadaCommand(
    Guid ConcesionPrivilegioId,
    Guid TenantObjetivoId,
    string Motivo,
    int DiasDeVentana,
    string? Ticket = null) : ICommand<Guid>;

public class AbrirSesionPrivilegiadaCommandValidator : AbstractValidator<AbrirSesionPrivilegiadaCommand>
{
    public AbrirSesionPrivilegiadaCommandValidator()
    {
        RuleFor(c => c.ConcesionPrivilegioId).NotEmpty();
        RuleFor(c => c.TenantObjetivoId).NotEmpty();

        // El dominio ya exige motivo no vacío y su tope; aquí se repite para
        // devolver un mensaje de formulario en vez de una excepción. La regla
        // vinculante es la del agregado — este validador es cortesía, no
        // frontera de seguridad.
        RuleFor(c => c.Motivo)
            .NotEmpty().WithMessage("Indica por qué necesitas entrar en los datos de este cliente.")
            .MaximumLength(SesionPrivilegiada.LongitudMaximaMotivo);

        RuleFor(c => c.DiasDeVentana)
            .InclusiveBetween(1, (int)SesionPrivilegiada.VentanaMaxima.TotalDays)
            .WithMessage($"La ventana de acceso debe estar entre 1 y {SesionPrivilegiada.VentanaMaxima.TotalDays:0} días.");

        RuleFor(c => c.Ticket)
            .MaximumLength(SesionPrivilegiada.LongitudMaximaTicket);
    }
}

/// <summary>
/// <b>Las tres precondiciones se comprueban por separado y cada una devuelve su
/// propio error.</b> No es verbosidad: colapsarlas en una condición genérica
/// —"no autorizado"— dejaría que la desaparición de una de ellas pasara
/// inadvertida. Si mañana alguien borra la comprobación de 2FA, el test de 2FA
/// se pone rojo; con una condición única, los demás tests seguirían verdes y el
/// agregado parecería sano.
///
/// <code>
/// autorización para abrir  ─┐
/// 2FA activa               ─┼─→  SesionPrivilegiada.Abrir(...)  →  7 invariantes
/// concesión cargada        ─┘
/// </code>
///
/// Ninguna de las tres puede ser invariante del agregado: la autorización
/// depende de una política que va a cambiar de fuente, el 2FA vive en Identity
/// —que el dominio no alcanza— y la concesión llega ya resuelta. Que estén fuera
/// no las degrada; las convierte en precondiciones nombradas, con sus propios
/// tests. Lo que no vale es suponer que el agregado las cubre.
/// </summary>
public class AbrirSesionPrivilegiadaCommandHandler(
    IPlataformaQueryContext plataformaContext,
    IPlataformaWriter writer,
    IAutorizacionAperturaSesion autorizacion,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<AbrirSesionPrivilegiadaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AbrirSesionPrivilegiadaCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<Guid>(Error.Crear(
                "SesionPrivilegiada.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        // Precondición 1 — autoridad para abrir. Pregunta de negocio, no
        // comprobación de rol: ver IAutorizacionAperturaSesion.
        if (!await autorizacion.PuedeAbrirAsync(usuarioId.Value, request.TenantObjetivoId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "SesionPrivilegiada.NoAutorizado",
                "No tienes autorización para abrir un acceso de soporte sobre este cliente."));

        // Precondición 2 — 2FA. Se conserva de la ceremonia heredada porque
        // quien entra en datos de un cliente ajeno con una cuenta comprometida
        // por contraseña sola es exactamente el escenario que este acceso
        // existe para poder rastrear y contener.
        if (!await currentUserService.TieneDobleFactorActivoAsync())
            return Result.Fallo<Guid>(Error.Crear(
                "SesionPrivilegiada.SinDobleFactor",
                "Activa la autenticación en dos pasos en tu cuenta antes de abrir un acceso de soporte."));

        // Precondición 3 — la concesión existe, es tuya y viene con su alcance.
        //
        // Que sea TUYA se comprueba dos veces, con el mismo predicado y una sola
        // fuente de identidad:
        //
        //   usuarioId ─┬─→ app.usuario_id  →  RLS filtra las filas visibles
        //              └─→ concesion.UsuarioPlataformaId == usuarioId  (aquí)
        //
        // No es sustituir RLS por C#: RLS sigue siendo la primera barrera y es
        // la única que impide siquiera OBSERVAR una concesión ajena. Es defensa
        // en profundidad sobre el predicado crítico, y la razón es concreta: la
        // efectividad de RLS depende del rol con el que conecta la aplicación,
        // que es una propiedad de despliegue imposible de verificar desde el
        // código. Que una ceremonia de alto privilegio dependa de una sola
        // condición operativa para no aceptar la concesión de otro es demasiado
        // poco.
        //
        // Lo que esta comprobación NO hace: no sustituye ninguna otra frontera
        // de aislamiento. Un rol con BYPASSRLS seguiría invalidando el resto de
        // garantías; aquí solo se blinda la propiedad de la concesión.
        //
        // Devuelve el MISMO error que "no existe" a propósito: así la respuesta
        // observable es idéntica la dispare RLS o la dispare esta línea, y no
        // aparece un canal nuevo que distinga "no existe" de "existe pero no es
        // tuya".
        var concesion = await plataformaContext.ConcesionesPrivilegio
            .Include(c => c.TenantsAlcanzados)
            .FirstOrDefaultAsync(c => c.Id == request.ConcesionPrivilegioId, cancellationToken);

        if (concesion is null || concesion.UsuarioPlataformaId != usuarioId.Value)
            return Result.Fallo<Guid>(Error.Crear(
                "SesionPrivilegiada.ConcesionNoEncontrada", "No encontramos esa concesión de privilegio."));

        // Y a partir de aquí manda el dominio. CubreEn comprueba las tres cosas
        // juntas —estado, ventana y alcance— así que una concesión revocada,
        // caducada o que no cubra este tenant no llega a abrir nada.
        SesionPrivilegiada sesion;
        try
        {
            sesion = SesionPrivilegiada.Abrir(
                concesion,
                request.TenantObjetivoId,
                request.Motivo,
                DateTime.UtcNow,
                TimeSpan.FromDays(request.DiasDeVentana),
                usuarioSimuladoId: null,
                request.Ticket);
        }
        catch (InvalidOperationException excepcion)
        {
            // Los invariantes del agregado lanzan; aquí se traducen a un Result
            // legible en vez de reventar la petición. No se capturan
            // ArgumentException a propósito: esos indican que el validador dejó
            // pasar algo que no debía, y eso es un fallo de programación que
            // tiene que verse.
            return Result.Fallo<Guid>(Error.Crear("SesionPrivilegiada.NoAbrible", excepcion.Message));
        }

        writer.AnadirSesion(sesion);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(sesion.Id);
    }
}
