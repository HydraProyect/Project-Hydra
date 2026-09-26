using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Commands.CrearCanalGestion;

/// <summary>
/// Alta de un acceso de gestión documental del Centro (PLAN-EJECUCION-UX.md
/// § 0.6, Lote 0-E). Hasta ese lote la tabla no tenía ningún escritor — ni
/// Command ni seeder, solo la Query de lectura (ROADMAP.md, Fase "Canal de
/// gestión documental" § 1).
///
/// <see cref="ProveedorPlataformaCaeId"/> sustituye al antiguo
/// <c>NombrePlataforma</c> de texto libre (Lote 2-B, § Parte 2 (a)):
/// referencia el catálogo global, resuelto en la UI a partir de la URL antes
/// de llegar aquí — este Command solo valida que el Id exista.
///
/// El primer canal de un Centro se marca principal solo: un Centro con un
/// único acceso y "ninguno por defecto" no describe ninguna situación real.
/// </summary>
public record CrearCanalGestionCommand(
    Guid CentroId,
    TipoCanalGestion Tipo,
    string EtiquetaProposito,
    Guid? ProveedorPlataformaCaeId,
    string? UrlAcceso,
    string? Usuario,
    string? Contrasena,
    string? EmailsDestinatarios,
    string? NombreContacto,
    string? Notas) : ICommand<Guid>, IEscrituraDeDatosDeCredencial
{
    // Solo un canal de Plataforma guarda usuario y contraseña: uno de correo los
    // descarta (el formulario puede enviarlos ocultos tras cambiar de tipo), y
    // una plataforma cuyo acceso se apunta después no escribe nada que proteger.
    bool IEscrituraDeDatosDeCredencial.EscribeDatosDeCredencial =>
        Tipo == TipoCanalGestion.Plataforma
        && (!string.IsNullOrEmpty(Usuario) || !string.IsNullOrEmpty(Contrasena));
}

public class CrearCanalGestionCommandValidator : AbstractValidator<CrearCanalGestionCommand>
{
    public CrearCanalGestionCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty();

        RuleFor(c => c.EtiquetaProposito)
            .NotEmpty().WithMessage("Describe para qué sirve este acceso (por ejemplo, «Gestión general»).")
            .MaximumLength(CanalGestionDocumental.LongitudMaximaEtiquetaProposito);

        RuleFor(c => c.ProveedorPlataformaCaeId)
            .NotEmpty().WithMessage("Elige la plataforma — resuélvela desde la URL de acceso o selecciónala manualmente.")
            .When(c => c.Tipo == TipoCanalGestion.Plataforma);

        RuleFor(c => c.UrlAcceso).MaximumLength(CanalGestionDocumental.LongitudMaximaUrlAcceso);

        RuleFor(c => c.EmailsDestinatarios)
            .NotEmpty().WithMessage("Los correos de destino son obligatorios.")
            .MaximumLength(CanalGestionDocumental.LongitudMaximaEmailsDestinatarios)
            .When(c => c.Tipo == TipoCanalGestion.Email);

        RuleFor(c => c.NombreContacto).MaximumLength(CanalGestionDocumental.LongitudMaximaNombreContacto);
        RuleFor(c => c.Notas).MaximumLength(CanalGestionDocumental.LongitudMaximaNotas);
    }
}

public class CrearCanalGestionCommandHandler(
    ICanalGestionDocumentalRepository repositorio, IAlcanceDatosService alcanceDatos,
    IProveedoresPlataformaCaeQueryContext proveedoresContext, ICentrosQueryContext centrosContext,
    IAltaAcreditacionesPlataformaService altaAcreditaciones, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearCanalGestionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearCanalGestionCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("CanalGestion.CentroNoEncontrado", "No encontramos este centro."));

        // P1-X2: un Centro sin gestión CAE no recibe documentación, así que un
        // canal nuevo no tendría a qué servir. Los que ya tuviera se conservan.
        if (await centrosContext.Centros.AnyAsync(
                c => c.Id == request.CentroId && c.GestionCae == ModalidadGestionCae.SinGestionCae, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "CanalGestion.CentroSinGestionCae", "Este centro no requiere gestión CAE: no necesita canales de gestión."));

        if (request.Tipo == TipoCanalGestion.Plataforma
            && !await proveedoresContext.ProveedoresPlataformaCae.AnyAsync(p => p.Id == request.ProveedorPlataformaCaeId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("CanalGestion.ProveedorNoEncontrado", "No encontramos esta plataforma en el catálogo."));

        var existentes = await repositorio.ObtenerPorCentroAsync(request.CentroId, cancellationToken);

        var canal = request.Tipo == TipoCanalGestion.Plataforma
            ? CanalGestionDocumental.DePlataforma(
                request.CentroId, request.EtiquetaProposito, request.ProveedorPlataformaCaeId!.Value,
                request.UrlAcceso, request.Usuario, request.Contrasena, request.Notas)
            : CanalGestionDocumental.PorEmail(
                request.CentroId, request.EtiquetaProposito, request.EmailsDestinatarios!,
                request.NombreContacto, request.Notas);

        if (existentes.Count == 0)
            canal.MarcarComoPrincipal();

        repositorio.Agregar(canal);

        // Un acceso de plataforma nuevo es un sitio más donde acreditar: los
        // Documentos que el Centro exige a quienes ya trabajan en él nacen
        // pendientes de subir a este acceso. Un acceso por correo no acredita.
        // Mismo SaveChangesAsync que el acceso.
        await altaAcreditaciones.AgregarPendientesAsync(new AltasConAcreditacion { Canales = [canal] }, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(canal.Id);
    }
}
