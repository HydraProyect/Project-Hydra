using System.Security.Cryptography;
using CaeManager.Application.Common;
using CaeManager.Application.ContratosAcceso.LecturaInbound;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.EvidenciasInbound;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.ContratosAcceso.EvidenciasInbound;

/// <param name="TipoDocumentoId">Tipo del catálogo del Tenant del Cliente empresarial que elige el validador.</param>
/// <param name="Conclusion">
/// La conclusión transcrita del certificado de aptitud. Obligatoria si el tipo elegido o el del original es de «solo
/// conclusión de aptitud»; prohibida en cualquier otro caso.
/// </param>
public record RegistrarEvidenciaInboundCommand(
    Guid ContratoAccesoId, Guid DocumentoOrigenId, Guid TipoDocumentoId, ConclusionAptitud? Conclusion) : ICommand<Guid>;

/// <summary>
/// Registra la copia de evidencia validada de un Documento original del Tenant de la Empresa contratista (Inbound M1,
/// PR 4). El orden importa:
/// <list type="number">
/// <item>Autoridad de <see cref="LecturaInboundAutoridad"/> (rol que valida y cartera sobre el Cliente empresarial del
/// contrato); lo que no pasa, o no se ve, responde «no encontrado».</item>
/// <item>Lectura por <see cref="ILectorDocumentosInbound"/> <b>fuera de transacción</b>: el lector lo exige porque cada
/// lectura deja su traza en el Tenant propietario, y un rollback la borraría. Este handler no abre ninguna; la única es la
/// implícita de <c>SaveChangesAsync</c>, al final y después de leer.</item>
/// <item>Si la evidencia es de «solo conclusión de aptitud» (el tipo elegido o el del original lleva la marca): no se
/// abre ni se copia ningún binario; se guardan la conclusión transcrita, el nombre y el documento de identidad.</item>
/// <item>Si no: se lee el binario del original con la clave de su Tenant propietario, se calcula su SHA-256, se copia al
/// almacenamiento del Tenant del Cliente empresarial y solo entonces se guarda la entidad. Si algo falla después de
/// copiar, se elimina la copia antes de propagar el error: no queda un binario huérfano.</item>
/// </list>
/// </summary>
public class RegistrarEvidenciaInboundCommandHandler(
    IContratosAccesoQueryContext contratosContext,
    ITiposDocumentoQueryContext tiposContext,
    IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUser,
    ILectorDocumentosInbound lector,
    IAlmacenOriginalesInbound almacenOriginales,
    IFileStorageService almacen,
    IEvidenciaInboundRepository repositorio,
    IUnitOfWork unitOfWork,
    TimeProvider reloj)
    : IRequestHandler<RegistrarEvidenciaInboundCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(RegistrarEvidenciaInboundCommand request, CancellationToken cancellationToken)
    {
        if (!await LecturaInboundAutoridad.PuedeLeerAsync(request.ContratoAccesoId, contratosContext, alcanceDatos, currentUser, cancellationToken)
            || await currentUser.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return Result.Fallo<Guid>(EvidenciaInboundErrores.NoEncontrada);

        var contrato = await contratosContext.ContratosAcceso
            .Where(c => c.Id == request.ContratoAccesoId)
            .Select(c => new { c.TenantContratistaId, c.EmpresaContratistaId })
            .FirstOrDefaultAsync(cancellationToken);
        if (contrato?.TenantContratistaId is not { } tenantContratista)
            return Result.Fallo<Guid>(EvidenciaInboundErrores.NoEncontrada);

        var tipo = await tiposContext.TiposDocumento
            .Where(t => t.Id == request.TipoDocumentoId && !t.Archivado)
            .Select(t => new { t.SoloConclusionAptitud })
            .FirstOrDefaultAsync(cancellationToken);
        if (tipo is null)
            return Result.Fallo<Guid>(EvidenciaInboundErrores.TipoNoEncontrado);

        var autorizacion = await lector.AutorizacionDelContratoAsync(request.ContratoAccesoId, cancellationToken);
        if (autorizacion is not { VigenteHoy: true })
            return Result.Fallo<Guid>(EvidenciaInboundErrores.NoEncontrada);

        var original = (await lector.DocumentosAutorizadosAsync(request.ContratoAccesoId, cancellationToken))
            .FirstOrDefault(d => d.DocumentoId == request.DocumentoOrigenId);
        if (original is null)
            return Result.Fallo<Guid>(EvidenciaInboundErrores.NoEncontrada);

        var soloConclusion = tipo.SoloConclusionAptitud || original.SoloConclusionAptitud;
        if (soloConclusion != (request.Conclusion is not null))
            return Result.Fallo<Guid>(soloConclusion ? EvidenciaInboundErrores.FaltaConclusion : EvidenciaInboundErrores.ConclusionSobra);

        EvidenciaInbound.Sujeto sujeto;
        if (original.TrabajadorId is { } trabajadorId)
        {
            sujeto = new(trabajadorId, $"{original.TrabajadorNombre} {original.TrabajadorApellidos}".Trim(), original.TrabajadorDni);
        }
        else
        {
            // Documento de la Empresa propia del Tenant de la Empresa contratista: la lectura no trae sus datos, y el
            // sujeto es la Empresa contratista con la que este Tenant la representa en el contrato (su Empresa contraparte
            // local), no una FK hacia el otro Tenant.
            var empresa = await empresasContext.Empresas
                .Where(e => e.Id == contrato.EmpresaContratistaId)
                .Select(e => new { e.RazonSocial, e.Cif })
                .FirstOrDefaultAsync(cancellationToken);
            if (empresa is null)
                return Result.Fallo<Guid>(EvidenciaInboundErrores.NoEncontrada);
            sujeto = new(null, empresa.RazonSocial, empresa.Cif);
        }

        if (soloConclusion && (sujeto.TrabajadorOrigenId is null || string.IsNullOrWhiteSpace(sujeto.DocumentoIdentidad)))
            return Result.Fallo<Guid>(EvidenciaInboundErrores.SinDocumentoIdentidad);

        var procedencia = new EvidenciaInbound.Procedencia(
            request.ContratoAccesoId, tenantContratista, original.DocumentoId, autorizacion.AutorizacionId);

        if (soloConclusion)
        {
            var medica = EvidenciaInbound.Registrar(
                procedencia, request.TipoDocumentoId, sujeto, original.FechaEmision, original.FechaVencimiento,
                soloConclusionAptitud: true, request.Conclusion, archivo: null, usuarioId, reloj.GetUtcNow().UtcDateTime);
            repositorio.Agregar(medica);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Exito(medica.Id);
        }

        var archivo = await lector.ArchivoAutorizadoAsync(request.ContratoAccesoId, request.DocumentoOrigenId, cancellationToken);
        if (archivo?.ArchivoUrl is not { } referencia || !OriginalesInbound.EsDelTenant(referencia, tenantContratista))
            return Result.Fallo<Guid>(EvidenciaInboundErrores.OriginalSinArchivo);

        byte[] contenido;
        try
        {
            contenido = await OriginalesInbound.LeerAsync(almacenOriginales, tenantContratista, referencia, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return Result.Fallo<Guid>(EvidenciaInboundErrores.OriginalSinArchivo);
        }

        var huella = Convert.ToHexString(SHA256.HashData(contenido)).ToLowerInvariant();
        string copia;
        using (var flujo = new MemoryStream(contenido, writable: false))
            copia = await almacen.GuardarAsync(flujo, Path.GetFileName(referencia), cancellationToken);

        try
        {
            var evidencia = EvidenciaInbound.Registrar(
                procedencia, request.TipoDocumentoId, sujeto, original.FechaEmision, original.FechaVencimiento,
                soloConclusionAptitud: false, conclusion: null, new EvidenciaInbound.ArchivoCopiado(copia, huella),
                usuarioId, reloj.GetUtcNow().UtcDateTime);
            repositorio.Agregar(evidencia);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Exito(evidencia.Id);
        }
        catch
        {
            // Sin fila que la referencie, la copia sería un binario huérfano con datos personales del Tenant de la
            // Empresa contratista. Se borra con CancellationToken.None: una cancelación no puede dejarla a medias.
            await almacen.EliminarAsync(copia, CancellationToken.None);
            throw;
        }
    }
}

public static class EvidenciaInboundErrores
{
    public static readonly Error NoEncontrada = Error.Crear(
        "EvidenciaInbound.NoEncontrada", "No encontramos el contrato de acceso, la autorización o el Documento seleccionados.");

    public static readonly Error TipoNoEncontrado = Error.Crear(
        "EvidenciaInbound.TipoNoEncontrado", "No encontramos el tipo de documento seleccionado.");

    public static readonly Error FaltaConclusion = Error.Crear(
        "EvidenciaInbound.FaltaConclusion", "Este tipo de documento solo admite la conclusión de aptitud: indícala.");

    public static readonly Error ConclusionSobra = Error.Crear(
        "EvidenciaInbound.ConclusionSobra", "Solo un certificado de aptitud lleva conclusión de aptitud.");

    public static readonly Error SinDocumentoIdentidad = Error.Crear(
        "EvidenciaInbound.SinDocumentoIdentidad",
        "El Trabajador no tiene documento de identidad registrado por su empresa: la conclusión de aptitud no se puede registrar sin él.");

    public static readonly Error OriginalSinArchivo = Error.Crear(
        "EvidenciaInbound.OriginalSinArchivo", "El Documento original no tiene un archivo disponible.");

    public static readonly Error EstadoNoAdmite = Error.Crear(
        "EvidenciaInbound.EstadoNoAdmite", "La evidencia no está en un estado que admita este cambio.");
}
