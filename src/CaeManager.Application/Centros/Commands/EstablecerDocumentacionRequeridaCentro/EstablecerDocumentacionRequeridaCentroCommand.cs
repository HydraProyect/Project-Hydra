using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Common.Archivos;
using CaeManager.Application.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Commands.EstablecerDocumentacionRequeridaCentro;

/// <summary>
/// Alta/edición de la posición explícita de un Centro sobre un TipoDocumento
/// (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.4) — upsert sobre la fila única (TipoDocumentoId,
/// CentroId) de <see cref="TipoDocumentoCentro"/>. Sin <c>Version</c>: la
/// entidad no la tiene (EntidadConTenant, no EntidadBase — "sin ciclo de vida
/// propio", mismo criterio que el resto de altas/bajas de esta tabla puente
/// desde /tipos-documento).
/// </summary>
public record EstablecerDocumentacionRequeridaCentroCommand(
    Guid CentroId,
    Guid TipoDocumentoId,
    bool Incluido,
    int? PeriodicidadEspecialMeses,
    bool BloqueaAcceso,
    string? ArchivoUrl,
    string? NombreArchivoOriginal,
    int? ToleranciaDias) : ICommand;

public class EstablecerDocumentacionRequeridaCentroCommandValidator : AbstractValidator<EstablecerDocumentacionRequeridaCentroCommand>
{
    public EstablecerDocumentacionRequeridaCentroCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty();
        RuleFor(c => c.TipoDocumentoId).NotEmpty();
        RuleFor(c => c.PeriodicidadEspecialMeses).GreaterThan(0).When(c => c.PeriodicidadEspecialMeses is not null)
            .WithMessage("La periodicidad especial debe ser un número entero de meses mayor que cero.");
        RuleFor(c => c.ToleranciaDias).InclusiveBetween(0, TipoDocumentoCentro.ToleranciaMaximaDias).When(c => c.ToleranciaDias is not null)
            .WithMessage($"La tolerancia debe ser un número entero de días entre 0 y {TipoDocumentoCentro.ToleranciaMaximaDias}, o vacía para heredar la del Cliente.");
        RuleFor(c => c.ArchivoUrl).MaximumLength(TipoDocumentoCentro.LongitudMaximaArchivoUrl);
        RuleFor(c => c.NombreArchivoOriginal).MaximumLength(TipoDocumentoCentro.LongitudMaximaNombreArchivo);
    }
}

public class EstablecerDocumentacionRequeridaCentroCommandHandler(
    ITipoDocumentoCentroRepository repositorio,
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAltaAcreditacionesPlataformaService altaAcreditaciones,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork,
    IVerificadorArchivoAlmacenado verificadorArchivo,
    ISerializacionAltasAcreditacionEmpresa serializacionAltas,
    // Opcional solo mientras la PR #2104 cambia los tests que construyen este handler: el contenedor siempre lo resuelve
    // (InvalidadorAlcanceInerte por defecto, el AlcanceDatosService del circuito en la Web). Issue #2118.
    IInvalidadorAlcance? invalidadorAlcance = null)
    : IRequestHandler<EstablecerDocumentacionRequeridaCentroCommand, Result>
{
    public async Task<Result> Handle(EstablecerDocumentacionRequeridaCentroCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de Project-Hydra-Negocio/MATURITY_REVIEW.md —, y
        // Asignación de Cartera: fuera del alcance de gestión el Centro «no existe» (mismo criterio
        // que EditarCentroCommand).
        var centro = await centrosContext.Centros.Where(c => c.Id == request.CentroId)
            .Select(c => new { c.TenantId }).FirstOrDefaultAsync(cancellationToken);
        if (centro is null || !await alcanceDatos.CentroParaGestionVisibleAsync(request.CentroId, cancellationToken))
            return CentroNoEncontrado();

        var tipo = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.Id == request.TipoDocumentoId)
            .Select(t => new { t.Archivado })
            .FirstOrDefaultAsync(cancellationToken);
        if (tipo is null)
            return Result.Fallo(Error.Crear("DocumentacionRequeridaCentro.TipoDocumentoNoEncontrado", "No encontramos este tipo de documento."));

        // Un tipo archivado (catálogo revisado el 2026-10-17) no se empieza a exigir en un Centro: se exige su
        // sustituto. Dejar de exigirlo sí se permite, para poder limpiar la configuración que ya lo tenía.
        if (tipo.Archivado && request.Incluido)
            return Result.Fallo(Error.Crear("DocumentacionRequeridaCentro.TipoDocumentoArchivado", "Ese tipo de documento está archivado; exige su sustituto."));

        // Lo confirmado, no la copia que el contexto del circuito rastreara antes: sirve para rechazar pronto una clave
        // no válida; si la clave pedida es la existente lo decide la relectura bajo el candado (ver GuardarAsync).
        var fila = await repositorio.ObtenerPorParActualizadoAsync(request.TipoDocumentoId, request.CentroId, cancellationToken);

        // OWASP ASVS 4.0.3 V12.1/V12.2: la clave de archivo y el nombre original llegan del cliente; se validan en
        // servidor (el formulario Blazor ya lo hace, otros llamadores no). El endpoint de descarga deriva del nombre
        // original el nombre y el tipo del archivo, así que la clave y el nombre tienen que terminar en la misma
        // extensión. Una clave nueva solo admite PDF o Word, con la firma y la estructura del contenido almacenado. El
        // contenido de una clave que la fila ya tenía no se vuelve a exigir: editar la tolerancia o excluir el tipo no
        // debe quedar bloqueado por un archivo anterior a esta validación.
        //
        // OWASP ASVS 4.0.3 V4.2.1 y API Top 10 API1: una clave nueva tiene que ser de la carpeta del Tenant propietario
        // del Centro, que sale del Centro y no del contexto de quien opera (una clave subida bajo un Tenant beneficiario
        // es de ese Tenant, igual que sus Centros). Se comprueba antes de abrir nada y con el mismo error que un archivo
        // no válido: la respuesta no dice si la clave existe. Si el Centro admite o no ese TipoDocumento es otra regla,
        // sin decidir, que esta validación no toca.
        //
        // Qué clave «ya tenía la fila» se decide frente a una fila concreta, y la que cuenta es la releída bajo el
        // candado: otra operación puede cambiar la clave mientras esta espera, y la clave pedida deja entonces de ser la
        // existente (revisión Codex de la PR #2074, ronda 6). Por eso la comprobación se hace dos veces: aquí, contra lo
        // confirmado antes de esperar, solo para rechazar pronto sin tomar el candado (un rechazo no escribe nada); y
        // dentro de GuardarAsync, contra la fila releída, que es la única que puede admitir la clave. El contenido
        // almacenado también se vuelve a abrir bajo el candado, sin reutilizar el veredicto de antes de esperar: si otra
        // operación borró el archivo durante la espera, se guardaría una clave que ya no se puede descargar (issue #2094,
        // B1). La purga no toma este candado, así que un borrado entre esta comprobación y el guardado sigue siendo
        // posible, como en main; esto solo deja fuera la espera. El precio es que esa segunda apertura (descifrado y
        // reserva de memoria) ocurre con el candado del Tenant tomado, y solo cuando la clave es nueva frente a la fila.
        async Task<bool> ArchivoAdmitidoFrenteAAsync(TipoDocumentoCentro? frente, CancellationToken ct)
        {
            if (request.ArchivoUrl is not { } archivoUrl)
                return true;
            var cambiaClave = archivoUrl != frente?.ArchivoUrl;
            var cambiaNombre = request.NombreArchivoOriginal != frente?.NombreArchivoOriginal;
            if ((cambiaClave || cambiaNombre) && !ExtensionCoincide(archivoUrl, request.NombreArchivoOriginal))
                return false;
            if (!cambiaClave)
                return true;
            if (centro.TenantId == Guid.Empty || !OriginalesInbound.EsDelTenant(archivoUrl, centro.TenantId))
                return false;
            return await ArchivoRequisitoEsValidoAsync(archivoUrl, ct);
        }

        if (!await ArchivoAdmitidoFrenteAAsync(fila, cancellationToken))
            return ArchivoNoValido();

        // Si el Centro pasa a exigir el tipo, los Documentos de ese tipo de
        // quienes ya trabajan en él nacen pendientes de acreditar ante sus
        // accesos de plataforma. Si deja de exigirlo, no se retira ninguna
        // acreditación (ver IAltaAcreditacionesPlataformaService). Mismo
        // SaveChangesAsync que la fila.
        async Task<Result> GuardarAsync(CancellationToken ct)
        {
            // La Asignación de Cartera se vuelve a decidir bajo el candado: pudo revocarse mientras se esperaba, y el
            // alcance memoizado (en Blazor Server, el del circuito) es de antes; se descarta, igual que en
            // BloqueoRespuestaSugerida.ComprobarEnvioPorBuzonAsync (issue #2118).
            invalidadorAlcance?.Invalidar();
            if (!await alcanceDatos.CentroParaGestionVisibleAsync(request.CentroId, ct))
                return CentroNoEncontrado();

            // Se vuelve a leer bajo el candado, con lo confirmado en la base: la fila de arriba (o la que el contexto,
            // uno por circuito, rastreara antes) puede tener el Incluido de antes de esperar. Con ese valor viejo, pedir
            // lo que otra operación acababa de cambiar no sería un cambio para EF, que no guardaría nada, y el comando
            // devolvería éxito con la base intacta (revisión Codex de la PR #2074, ronda 4, hallazgo 2). En el
            // reintento, que empieza con el contexto vacío, la lectura consulta la base igualmente.
            var actual = await repositorio.ObtenerPorParActualizadoAsync(request.TipoDocumentoId, request.CentroId, ct);

            // La clave se admite frente a esta fila, no frente a la de antes de esperar (ver arriba): si otra operación
            // cambió X por Y, pedir X ya no es conservar la clave existente y X se valida como cualquier clave nueva.
            if (!await ArchivoAdmitidoFrenteAAsync(actual, ct))
                return ArchivoNoValido();

            if (actual is null)
            {
                actual = new TipoDocumentoCentro(
                    request.TipoDocumentoId, request.CentroId, request.Incluido,
                    request.PeriodicidadEspecialMeses, request.BloqueaAcceso, request.ArchivoUrl, request.NombreArchivoOriginal,
                    request.ToleranciaDias);
                repositorio.Agregar(actual);
            }
            else
            {
                actual.Actualizar(
                    request.Incluido, request.PeriodicidadEspecialMeses, request.BloqueaAcceso,
                    request.ArchivoUrl, request.NombreArchivoOriginal, request.ToleranciaDias);
            }

            await altaAcreditaciones.AgregarPendientesAsync(new AltasConAcreditacion { Requisitos = [actual] }, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Exito();
        }

        // Pasar a exigir el tipo se serializa con el alta de un Documento de Empresa (ver
        // ISerializacionAltasAcreditacionEmpresa): sin ello, el Documento lee el requisito antiguo, esta edición no ve el
        // Documento y el par se pierde (revisión Codex de la PR #2074, ronda 3, hallazgo 2). Dejar de exigirlo no da de
        // alta nada, pero escribe la misma fila: sin el candado, una exclusión que leyó antes de que una exigencia
        // confirmara podría no guardar nada, o mezclar sus campos con los de la otra; con él, las dos escrituras de la
        // fila se ordenan y la segunda lee lo que confirmó la primera (revisión Codex de la PR #2074, ronda 4,
        // hallazgo 2).
        return await serializacionAltas.EjecutarAsync(GuardarAsync, cancellationToken);
    }

    private static Result CentroNoEncontrado() =>
        Result.Fallo(Error.Crear("DocumentacionRequeridaCentro.CentroNoEncontrado", "No encontramos este centro."));

    private static Result ArchivoNoValido() =>
        Result.Fallo(Error.Crear("DocumentacionRequeridaCentro.ArchivoNoValido", ValidadorFirmaArchivo.MensajeRequisitoNoValido));

    private static bool ExtensionCoincide(string archivoUrl, string? nombreOriginal) =>
        nombreOriginal is not null
        && string.Equals(Path.GetExtension(nombreOriginal), Path.GetExtension(archivoUrl), StringComparison.OrdinalIgnoreCase);

    private async Task<bool> ArchivoRequisitoEsValidoAsync(string archivoUrl, CancellationToken cancellationToken) =>
        Path.GetExtension(archivoUrl).ToLowerInvariant() is ".pdf" or ".docx"
        && await verificadorArchivo.EsDocumentoValidoAsync(archivoUrl, cancellationToken);
}