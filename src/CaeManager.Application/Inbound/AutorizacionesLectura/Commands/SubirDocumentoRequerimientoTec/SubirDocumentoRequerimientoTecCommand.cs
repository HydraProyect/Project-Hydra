using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Inbound.AutorizacionesLectura.Queries.ObtenerRequerimientosTec;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.AutorizacionesLectura.Commands.SubirDocumentoRequerimientoTec;

/// <summary>
/// El Tenant TEC sube el documento que le pide un Tenant TCE para un requerimiento concreto (demo Inbound, P2). El
/// requerimiento no se guarda: es el trío (<paramref name="ContratoAccesoId"/>, <paramref name="RequisitoId"/>, sujeto) que
/// calcula <see cref="ObtenerRequerimientosTecQuery"/>, y aquí se recibe explícito y se revalida en el servidor. El
/// documento es un Documento propio del Tenant TEC (su Tenant propietario), con el tipo de su catálogo del mismo nombre
/// exacto que el del Tenant TCE (D1); la lectura del Tenant TCE lo ve porque <c>app_inbound_documentos_del_alcance</c> se
/// calcula al leer.
///
/// <para>
/// <b>Qué se revalida.</b> El requisito sale de la lectura inversa X-2 del contrato (que ya aplica D10: contrato Activo y
/// vinculado y línea Inbound vigente del Tenant TCE) y pide un documento general del Tenant TEC; el sujeto es uno de los
/// que la propia Query asigna a ese requisito (D8), así que las reglas de asignación no se copian aquí. El tipo propio es el
/// único tipo no archivado del catálogo con ese nombre exacto y el ámbito del sujeto; si no hay exactamente uno, falla, sin
/// buscar por parecido. Un tipo de salud que la lectura del Tenant TCE no puede ver (todo el de categoría especial salvo el
/// de solo conclusión de aptitud) no se sube por aquí: no llegaría a la cola del Tenant TCE.
/// </para>
///
/// <para>
/// <b>Crear o renovar.</b> Si el sujeto ya tiene un documento vigente hoy propio de ese tipo, se renueva el efectivo
/// (<see cref="RenovarDocumentoCommand"/>, que lo sustituye y lo saca del alcance); si no, se crea
/// (<see cref="CrearDocumentoCommand"/>). Los dos pasan por su propio pipeline: alcance, firma del archivo, acreditaciones
/// y Gestión automática son los del alta normal. <paramref name="ArchivoUrl"/> es la clave de un PDF ya almacenado.
/// </para>
///
/// <para>
/// Es <see cref="IComandoInbound"/>: la guarda de servicio solo lo deja pasar en el Tenant de origen o con papel Inbound, y
/// <see cref="CapacidadInbound.ParticipanteTec"/> niega el papel Inbound, así que solo lo ejecuta el Administrador del Tenant
/// TEC en su propio Tenant. Nada del resultado identifica a un Gestor CAE del Tenant TCE (P-4).
/// </para>
/// </summary>
[AreaDelEncargo("AutorizacionesLectura")]
public record SubirDocumentoRequerimientoTecCommand(
    Guid ContratoAccesoId,
    Guid RequisitoId,
    SujetoRequerimientoTec Sujeto,
    Guid SujetoId,
    DateOnly FechaEmision,
    DateOnly? FechaVencimiento,
    string ArchivoUrl,
    string? Comentarios) : ICommand<Guid>, IComandoInbound;

public class SubirDocumentoRequerimientoTecCommandValidator : AbstractValidator<SubirDocumentoRequerimientoTecCommand>
{
    public SubirDocumentoRequerimientoTecCommandValidator()
    {
        RuleFor(c => c.ContratoAccesoId).NotEmpty();
        RuleFor(c => c.RequisitoId).NotEmpty();
        RuleFor(c => c.SujetoId).NotEmpty();
        RuleFor(c => c.Sujeto).IsInEnum();
        RuleFor(c => c.ArchivoUrl).NotEmpty().WithMessage("Elige el archivo del documento.");
        RuleFor(c => c.FechaEmision)
            .LessThanOrEqualTo(_ => DiaDeNegocio.Hoy())
            .WithMessage("La fecha de emisión no puede ser futura.");
        RuleFor(c => c.Comentarios).MaximumLength(Documento.LongitudMaximaComentarios);
    }
}

public static class SubirDocumentoRequerimientoTecErrores
{
    public static readonly Error RequisitoNoEncontrado = Error.Crear(
        "RequerimientoTec.RequisitoNoEncontrado",
        "Este requisito ya no se pide: el contrato de acceso no está activo o el requisito ha cambiado. Vuelve a cargar la lista.");

    public static readonly Error RequisitoEspecifico = Error.Crear(
        "RequerimientoTec.RequisitoEspecifico",
        "Este requisito pide un documento específico del Cliente o del Centro, y todavía no se puede subir desde aquí.");

    public static readonly Error SujetoNoAsignado = Error.Crear(
        "RequerimientoTec.SujetoNoAsignado",
        "Este requisito no se pide a ese Trabajador o Empresa en este contrato de acceso.");

    public static readonly Error TipoDeSaludNoLegible = Error.Crear(
        "RequerimientoTec.TipoDeSaludNoLegible",
        "Este tipo de documento tiene datos de salud que el Cliente no puede ver: no se sube desde aquí.");

    public static Error TipoSinCorrespondencia(string nombre, int encontrados) => Error.Crear(
        "RequerimientoTec.TipoSinCorrespondencia",
        encontrados == 0
            ? $"Tu catálogo no tiene un tipo de documento activo llamado \"{nombre}\" para este sujeto: créalo con ese nombre exacto."
            : $"Tu catálogo tiene {encontrados} tipos de documento activos llamados \"{nombre}\" para este sujeto: deja solo uno.");
}

public class SubirDocumentoRequerimientoTecCommandHandler(
    IAutorizacionInbound autorizacionInbound,
    ICurrentUserService currentUserService,
    ILectorRequerimientosTec lector,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IDocumentosQueryContext documentosContext,
    ISender mediator)
    : IRequestHandler<SubirDocumentoRequerimientoTecCommand, Result<Guid>>
{
    private const string AmbitoGeneralTec = "GeneralTec";

    public async Task<Result<Guid>> Handle(SubirDocumentoRequerimientoTecCommand request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.ParticipanteTec, cancellationToken))
            return Result.Fallo<Guid>(AutorizacionInboundErrores.SinCapacidad);

        if (!await AutorizacionLecturaAutoridad.EsAdministradorAsync(currentUserService))
            return Result.Fallo<Guid>(AutorizacionLecturaAutoridad.SoloAdministrador);

        // X-2 (D10): el requisito se pide hoy por este contrato de acceso al Tenant TEC del contexto firmado.
        var requisito = (await lector.RequisitosAsync(cancellationToken))
            .FirstOrDefault(r => r.ContratoAccesoId == request.ContratoAccesoId && r.RequisitoId == request.RequisitoId);
        if (requisito is null)
            return Result.Fallo<Guid>(SubirDocumentoRequerimientoTecErrores.RequisitoNoEncontrado);

        // Un documento general no cubre un requisito específico del Tenant TCE o del Centro (la Query lo deja pendiente):
        // subirlo no movería el requerimiento, así que falla cerrado.
        if (!string.Equals(requisito.AmbitoDocumento, AmbitoGeneralTec, StringComparison.Ordinal))
            return Result.Fallo<Guid>(SubirDocumentoRequerimientoTecErrores.RequisitoEspecifico);

        // D8: el trío tiene que ser una de las filas que la Query calcula, la única definición de sujeto asignado.
        var filas = await mediator.Send(new ObtenerRequerimientosTecQuery(), cancellationToken);
        if (!filas.Any(f => f.ContratoAccesoId == request.ContratoAccesoId && f.RequisitoId == request.RequisitoId
                            && f.Sujeto == request.Sujeto && f.SujetoId == request.SujetoId))
            return Result.Fallo<Guid>(SubirDocumentoRequerimientoTecErrores.SujetoNoAsignado);

        // D1: el tipo propio por el nombre exacto del catálogo del Tenant TCE. La base compara con su intercalación; el
        // emparejamiento exacto (ordinal), el archivado y el ámbito se deciden en memoria.
        var ambito = request.Sujeto == SujetoRequerimientoTec.Trabajador ? AmbitoAplicacion.Trabajador : AmbitoAplicacion.Empresa;
        var tipos = (await tiposDocumentoContext.TiposDocumento
                .Where(t => t.Nombre == requisito.TipoDocumentoNombre)
                .ToListAsync(cancellationToken))
            .Where(t => string.Equals(t.Nombre, requisito.TipoDocumentoNombre, StringComparison.Ordinal)
                        && !t.Archivado && t.AmbitoAplicacion == ambito)
            .ToList();
        if (tipos.Count != 1)
            return Result.Fallo<Guid>(SubirDocumentoRequerimientoTecErrores.TipoSinCorrespondencia(requisito.TipoDocumentoNombre, tipos.Count));

        var tipo = tipos[0];

        // D7: el mismo filtro que la lectura del Tenant TCE y que la Query; no se relaja.
        if (tipo.Sensibilidad == SensibilidadDocumental.CategoriaEspecialSalud && !tipo.SoloConclusionAptitud)
            return Result.Fallo<Guid>(SubirDocumentoRequerimientoTecErrores.TipoDeSaludNoLegible);

        var vigente = await DocumentoVigenteAsync(tipo.Id, request, cancellationToken);
        return vigente is { } anterior
            ? await mediator.Send(Renovacion(anterior.Id, anterior.Version, request), cancellationToken)
            : await mediator.Send(Alta(tipo.Id, request), cancellationToken);
    }

    /// <summary>
    /// El documento efectivo vigente hoy propio del tipo para el sujeto, con el mismo criterio que la Query
    /// (<see cref="DocumentoEfectivo.ValidoHoy"/> y <see cref="DocumentoEfectivo.Ordenar{T}"/>), o null.
    /// </summary>
    private async Task<(Guid Id, Guid Version)?> DocumentoVigenteAsync(
        Guid tipoId, SubirDocumentoRequerimientoTecCommand request, CancellationToken cancellationToken)
    {
        var esTrabajador = request.Sujeto == SujetoRequerimientoTec.Trabajador;
        var candidatos = await documentosContext.Documentos.Operativos()
            .Where(d => d.TipoDocumentoId == tipoId && d.AnonimizadoEnUtc == null)
            .Where(d => esTrabajador ? d.TrabajadorId == request.SujetoId : d.TrabajadorId == null && d.EmpresaId == request.SujetoId)
            .Select(d => new { d.Id, d.Version, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision, d.CreadoEnUtc, d.EsRenuncia })
            .ToListAsync(cancellationToken);

        var hoy = DiaDeNegocio.Hoy();
        var vigentes = candidatos.Where(d => DocumentoEfectivo.ValidoHoy(d.EstadoVigencia, d.FechaVencimiento, hoy, d.EsRenuncia)).ToList();
        if (vigentes.Count == 0)
            return null;

        var efectivo = DocumentoEfectivo.Ordenar(
            vigentes, d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id,
            d => d.EsRenuncia, hoy).First();
        return (efectivo.Id, efectivo.Version);
    }

    // D-12: cuando las factorías de Documento reciban el autor de la subida, se pasa en estos dos únicos puntos.
    private static CrearDocumentoCommand Alta(Guid tipoId, SubirDocumentoRequerimientoTecCommand request) =>
        // Posicional a propósito: el Cliente empresarial, el vehículo y el proyecto van vacíos, y nombrar el argumento del
        // Cliente empresarial sumaría una ubicación al trinquete de ClienteIdNoSeExtiendeTests.
        new(
            request.Sujeto == SujetoRequerimientoTec.Trabajador ? request.SujetoId : null,
            null,
            request.Sujeto == SujetoRequerimientoTec.Empresa ? request.SujetoId : null,
            null,
            null,
            tipoId, request.FechaEmision, request.FechaVencimiento, request.ArchivoUrl, request.Comentarios);

    private static RenovarDocumentoCommand Renovacion(Guid documentoId, Guid version, SubirDocumentoRequerimientoTecCommand request) =>
        new(documentoId, request.FechaEmision, request.FechaVencimiento, request.ArchivoUrl, request.Comentarios, version);
}
