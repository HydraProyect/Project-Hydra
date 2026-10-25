using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;

/// <summary>
/// "Marcar subido" del drill-down por plataforma (mockup Documentos TALVEG,
/// pestaña Plataforma — cierra el hallazgo P-04): el domino ya sabía pasar
/// una AcreditacionDocumentoPlataforma a Subida (Lote 2-C), pero ningún
/// Command de Application la disparaba todavía (Lote 2-D, "todavía sin
/// construir" según el propio comentario de la entidad).
///
/// <see cref="ExigirProveedorActivo"/> (MVP2 § 14.5, *kill switch* remoto):
/// solo la extensión de navegador lo pasa a <c>true</c>
/// (<c>MarcarAcreditacionSubidaEndpoints</c>) — el drill-down interno
/// (<c>PlataformaTab.razor</c>) deja el valor por defecto porque "marcar
/// subido" ahí registra una subida que el gestor ya hizo A MANO en la
/// plataforma, sin pasar por la extensión ni por su inyección; bloquearla
/// por un proveedor inactivo rompería ese registro sin motivo — el
/// conector inactivo es sobre la extensión, no sobre la plataforma en sí.
/// Nótese que para cuando este Command corre, la extensión YA descargó el
/// PDF e YA lo inyectó en el DOM de la plataforma (ver
/// <c>extension/background.js</c>, <c>subirDocumento</c>) — el freno real
/// que evita la inyección reconsulta la lista en el service worker antes de
/// entregar el archivo al formulario. Esta comprobación es la
/// segunda línea: que el registro de Hydra nunca diga "subida" para un
/// conector que la plataforma declaró inactivo, ni siquiera si un cliente
/// desactualizado o modificado se saltó el freno del popup.
///
/// Una acreditación <see cref="EstadoAcreditacion.Rechazada"/> no se marca
/// subida (P0-9b, FS-02): la plataforma ya evaluó y devolvió esa versión, y
/// marcarla subida sin versión nueva registraría como enviada la misma copia
/// rechazada y sacaría el rechazo de la cola. La salida es subir una versión
/// corregida (<c>RenovarDocumentoCommand</c>): con archivo nuevo sustituye el
/// Documento y crea acreditaciones pendientes; al corregir el mismo registro
/// reinicia sus acreditaciones a <see cref="EstadoAcreditacion.PendienteDeSubir"/>.
/// Desde ahí sí se marca subida. La lista de la extensión reutiliza esta regla.
///
/// <see cref="ExigirPortalDelProveedor"/> (extensión): el documento solo puede
/// haberse puesto en el portal de la Plataforma CAE a la que pertenece su canal.
/// La extensión declara el host de la pestaña en <see cref="DominioPortal"/> y el
/// servidor lo compara con los dominios del Proveedor del canal
/// (<c>DominioProveedorPlataformaCae</c>, mismo criterio de sufijo que
/// <see cref="ResolucionProveedorPlataformaCaeService.CoincideHost"/>). Es la segunda
/// línea tras el filtro de la propia extensión: protege frente a un cliente
/// desactualizado o con un error, que ya no puede registrar como subido en una
/// plataforma lo que puso en otra. NO es una frontera frente a un cliente hostil con
/// un token válido: el dominio lo declara el propio cliente y podría mentir. Sin
/// dominio declarado, o con un canal sin Proveedor o sin dominios en el
/// catálogo, se rechaza (no hay forma de comprobarlo). El drill-down interno
/// (<c>PlataformaTab.razor</c>) no lo pide: ahí el gestor registra una subida que
/// hizo a mano.
/// </summary>
public record MarcarAcreditacionSubidaCommand(
    Guid AcreditacionId, bool ExigirProveedorActivo = false,
    string? DominioPortal = null, bool ExigirPortalDelProveedor = false) : ICommand;

public class MarcarAcreditacionSubidaCommandHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext,
    ICentrosQueryContext centrosContext, IProveedoresPlataformaCaeQueryContext proveedoresContext,
    IUnitOfWork unitOfWork)
    : IRequestHandler<MarcarAcreditacionSubidaCommand, Result>
{
    public const string CodigoRechazadaSinVersionNueva = "Acreditacion.RechazadaSinVersionNueva";
    public const string CodigoPortalNoDeclarado = "Acreditacion.PortalNoDeclarado";
    public const string CodigoPortalAjeno = "Acreditacion.PortalAjeno";

    public async Task<Result> Handle(MarcarAcreditacionSubidaCommand request, CancellationToken cancellationToken)
    {
        var acreditacion = await acreditacionRepositorio.ObtenerPorIdAsync(request.AcreditacionId, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo(Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación."));

        var documento = await documentoRepositorio.ObtenerPorIdAsync(acreditacion.DocumentoId, cancellationToken);
        if (documento is null || !await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
            return Result.Fallo(Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación."));

        if (!ReglaSubidaAcreditacion.PermiteSubir(acreditacion))
            return Result.Fallo(Error.Crear(
                CodigoRechazadaSinVersionNueva,
                "La plataforma rechazó esta versión. Sube una versión corregida del documento: al guardarla volverá a quedar pendiente de subir."));

        if (request.ExigirProveedorActivo && !await ProveedorActivoAsync(acreditacion.CanalGestionDocumentalId, cancellationToken))
            return Result.Fallo(Error.Crear(
                "Acreditacion.ConectorInactivo", "Este conector está desactivado temporalmente."));

        if (request.ExigirPortalDelProveedor)
        {
            var host = ResolucionProveedorPlataformaCaeService.ExtraerHost(request.DominioPortal);
            if (host is null)
                return Result.Fallo(Error.Crear(
                    CodigoPortalNoDeclarado, "La extensión no indicó en qué portal puso el documento. Actualiza la extensión e inténtalo de nuevo."));

            if (!await PortalEsDelProveedorAsync(acreditacion.CanalGestionDocumentalId, host, cancellationToken))
                return Result.Fallo(Error.Crear(
                    CodigoPortalAjeno, "Este documento es de otra plataforma CAE: no se puede registrar como subido en este portal."));
        }

        acreditacion.MarcarSubida();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }

    private async Task<bool> PortalEsDelProveedorAsync(Guid canalGestionDocumentalId, string host, CancellationToken cancellationToken)
    {
        var proveedorId = await centrosContext.CanalesGestionDocumental
            .Where(c => c.Id == canalGestionDocumentalId)
            .Select(c => c.ProveedorPlataformaCaeId)
            .SingleOrDefaultAsync(cancellationToken);

        if (proveedorId is null) return false; // sin Proveedor no hay dominios con los que comparar

        var dominios = await proveedoresContext.DominiosProveedorPlataformaCae
            .Where(d => d.ProveedorPlataformaCaeId == proveedorId.Value)
            .Select(d => d.Dominio)
            .ToListAsync(cancellationToken);

        return dominios.Any(dominio => ResolucionProveedorPlataformaCaeService.CoincideHost(host, dominio));
    }

    private async Task<bool> ProveedorActivoAsync(Guid canalGestionDocumentalId, CancellationToken cancellationToken)
    {
        var proveedorId = await centrosContext.CanalesGestionDocumental
            .Where(c => c.Id == canalGestionDocumentalId)
            .Select(c => c.ProveedorPlataformaCaeId)
            .SingleOrDefaultAsync(cancellationToken);

        if (proveedorId is null) return true; // sin proveedor de catálogo, nada que apagar

        return await proveedoresContext.ProveedoresPlataformaCae
            .Where(p => p.Id == proveedorId.Value)
            .Select(p => p.Activo)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
