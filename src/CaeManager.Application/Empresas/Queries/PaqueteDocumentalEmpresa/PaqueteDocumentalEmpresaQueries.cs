using CaeManager.Application.Common;
using CaeManager.Application.Documentos.PaqueteDocumental;
using CaeManager.Application.Empresas.PaqueteDocumental;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.PaqueteDocumentalEmpresa;

/// <summary>
/// Lo que el diálogo «Preparar paquete documental» de Empresa 360 dice antes de generar: destinos, cuántos van, cuántos
/// faltan o están vencidos y cuántos no los pide ese Cliente empresarial. <paramref name="ClienteEmpresarialId"/> en
/// <c>null</c> es «Todo lo vigente». Regla y fuente en <see cref="IPaqueteDocumentalEmpresaService"/>; autorización en
/// <see cref="AutorizacionPaqueteEmpresa"/>.
/// </summary>
public record ObtenerResumenPaqueteEmpresaQuery(Guid EmpresaId, Guid? ClienteEmpresarialId) : IRequest<Result<ResumenPaqueteEmpresaDto>>;

/// <summary>
/// El zip del paquete («ZIP con carpetas por Trabajador»), para descargarlo por el endpoint autenticado
/// <c>/empresas/{id}/paquete-documental.zip</c>, nunca por una URL pública. Misma selección que el resumen.
/// </summary>
public record DescargarPaqueteEmpresaQuery(Guid EmpresaId, Guid? ClienteEmpresarialId) : IRequest<Result<PaqueteEmpresaDescargaDto>>;

public record PaqueteEmpresaDescargaDto(string NombreArchivo, byte[] Contenido);

/// <summary>
/// La misma puerta para el resumen y la descarga, con el criterio de las acciones vecinas de Empresa 360 y del paquete de
/// una Visita (<c>ObtenerPaqueteDocumentalVisitaQueryHandler</c>):
/// <list type="number">
/// <item>Tenant propietario: la Empresa se lee por el filtro de consulta del Tenant activo (y RLS); de otro Tenant, no existe.</item>
/// <item>Alcance de <b>gestión</b> sobre la Empresa (<see cref="AlcanceDatosServiceExtensions.EmpresaParaGestionVisibleAsync"/>),
/// como la pestaña «Clientes» (<c>ObtenerClientesDeEmpresaQuery</c>), que enseña los mismos destinos. Fuera de la cartera,
/// la misma respuesta que una Empresa inexistente.</item>
/// <item>Rol que descarga (decisión del propietario, 2026-10-08, la del paquete de la Visita): el zip entrega documentos,
/// también los sensibles, y sale de TALVEG, así que solo lo obtiene un rol que escribe. Lista blanca.</item>
/// <item>Sesión Privilegiada (decisión provisional de la coordinadora, 2026-11-08, pendiente de que Chris la confirme): el
/// Actor de Plataforma TALVEG ve el resumen —su alcance lo decide, como hoy en la Visita—, pero no descarga el paquete de
/// Empresa: el Soporte TALVEG no es Gestor CAE y un zip con todo lo vigente de una Empresa es una exportación en lote. Es la
/// opción más restrictiva; el paquete de la Visita no cambia.</item>
/// <item>El destino, si lo hay, es un Cliente empresarial de la Empresa en una Relación Empresarial vigente; otro Id
/// responde como inexistente, para no sondear Clientes ajenos.</item>
/// </list>
/// </summary>
public static class AutorizacionPaqueteEmpresa
{
    public static readonly Error NoEncontrada = Error.Crear("PaqueteEmpresa.NoEncontrada", "Empresa no encontrada.");

    public static readonly Error RolSinDescarga = Error.Crear(
        "PaqueteEmpresa.RolSinDescarga",
        "Tu rol no permite preparar ni descargar el paquete documental de una empresa — solo consultarla.");

    public static readonly Error SinDocumentos = Error.Crear(
        "PaqueteEmpresa.SinDocumentos",
        "No hay documentación vigente que entregar para este destino.");

    public static readonly Error DemasiadoGrande = Error.Crear(
        "PaqueteEmpresa.DemasiadoGrande",
        $"El paquete pasa de {IPaqueteDocumentalEmpresaService.MaximoArchivos} archivos. Elige un Cliente empresarial concreto para prepararlo.");

    public static readonly Error DemasiadoPesado = Error.Crear(
        "PaqueteEmpresa.DemasiadoPesado",
        $"El paquete pasa de {PaqueteDocumentalEmpresaService.MaximoBytes / (1024 * 1024)} MB. Elige un Cliente empresarial concreto para prepararlo.");

    public static readonly Error ArchivosNoDisponibles = Error.Crear(
        "PaqueteEmpresa.ArchivosNoDisponibles",
        "No se pudo abrir ningún archivo del paquete. Inténtalo de nuevo más tarde.");

    public static readonly Error SesionPrivilegiadaSinDescarga = Error.Crear(
        "PaqueteEmpresa.SesionPrivilegiadaSinDescarga",
        "Una sesión privilegiada de plataforma puede ver el resumen del paquete, pero no descargarlo.");

    // Los mismos literales que AutorizacionEscrituraBehavior y ObtenerPaqueteDocumentalVisitaQueryHandler, por el mismo
    // motivo: Application no referencia Infrastructure.Identity.Roles.
    private static readonly string[] RolesQueObtienenElPaquete =
        ["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae"];

    /// <summary>
    /// <c>null</c> si puede; si no, el error que responder. Devuelve también los destinos admisibles y si quien pregunta va
    /// en una Sesión Privilegiada (ve el resumen, no descarga).
    /// </summary>
    public static async Task<(Error? Error, IReadOnlyList<DestinoPaqueteEmpresaDto> Destinos, bool EnSesionPrivilegiada)> ComprobarAsync(
        Guid empresaId, Guid? clienteEmpresarialId,
        IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos, ICurrentUserService currentUserService,
        ISesionPrivilegiadaActual sesionPrivilegiadaActual, IPaqueteDocumentalEmpresaService paquete, CancellationToken cancellationToken)
    {
        if (!await empresasContext.Empresas.AnyAsync(e => e.Id == empresaId, cancellationToken)
            || !await alcanceDatos.EmpresaParaGestionVisibleAsync(empresaId, cancellationToken))
            return (NoEncontrada, [], false);

        var enSesionPrivilegiada = await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not null;
        if (!enSesionPrivilegiada
            && (await currentUserService.ObtenerRolEfectivoAsync() is not { } rol || !RolesQueObtienenElPaquete.Contains(rol)))
            return (RolSinDescarga, [], false);

        var destinos = await paquete.ObtenerDestinosAsync(empresaId, cancellationToken);
        if (clienteEmpresarialId is { } destinoId && destinos.All(d => d.ClienteEmpresarialId != destinoId))
            return (NoEncontrada, [], enSesionPrivilegiada);

        return (null, destinos, enSesionPrivilegiada);
    }
}

public class ObtenerResumenPaqueteEmpresaQueryHandler(
    IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IPaqueteDocumentalEmpresaService paquete)
    : IRequestHandler<ObtenerResumenPaqueteEmpresaQuery, Result<ResumenPaqueteEmpresaDto>>
{
    public async Task<Result<ResumenPaqueteEmpresaDto>> Handle(ObtenerResumenPaqueteEmpresaQuery request, CancellationToken cancellationToken)
    {
        var (error, destinos, enSesionPrivilegiada) = await AutorizacionPaqueteEmpresa.ComprobarAsync(
            request.EmpresaId, request.ClienteEmpresarialId, empresasContext, alcanceDatos, currentUserService, sesionPrivilegiadaActual, paquete, cancellationToken);
        if (error is not null)
            return Result.Fallo<ResumenPaqueteEmpresaDto>(error);

        var resumen = await paquete.ResumirAsync(request.EmpresaId, request.ClienteEmpresarialId, destinos, cancellationToken);
        return Result.Exito(enSesionPrivilegiada ? resumen with { PuedeDescargar = false } : resumen);
    }
}

/// <summary>
/// DEC-36: el zip entrega el contenido de cada Documento que contiene, así que cada uno cuenta como una
/// <see cref="TipoAccesoDocumentoSensible.Apertura"/> y se registra si es sensible, antes de entregar nada y solo los que de
/// verdad entraron. El registro falla en cerrado: sin constancia no hay zip.
/// </summary>
public class DescargarPaqueteEmpresaQueryHandler(
    IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IPaqueteDocumentalEmpresaService paquete,
    IRegistroAccesoDocumentoSensibleService registroAcceso)
    : IRequestHandler<DescargarPaqueteEmpresaQuery, Result<PaqueteEmpresaDescargaDto>>
{
    public async Task<Result<PaqueteEmpresaDescargaDto>> Handle(DescargarPaqueteEmpresaQuery request, CancellationToken cancellationToken)
    {
        var (error, destinos, enSesionPrivilegiada) = await AutorizacionPaqueteEmpresa.ComprobarAsync(
            request.EmpresaId, request.ClienteEmpresarialId, empresasContext, alcanceDatos, currentUserService, sesionPrivilegiadaActual, paquete, cancellationToken);
        if (error is not null)
            return Result.Fallo<PaqueteEmpresaDescargaDto>(error);
        if (enSesionPrivilegiada)
            return Result.Fallo<PaqueteEmpresaDescargaDto>(AutorizacionPaqueteEmpresa.SesionPrivilegiadaSinDescarga);

        var cliente = destinos.FirstOrDefault(d => d.ClienteEmpresarialId == request.ClienteEmpresarialId)?.RazonSocial;

        PaqueteEmpresaZip? zip;
        try
        {
            zip = await paquete.ConstruirAsync(request.EmpresaId, request.ClienteEmpresarialId, cliente, cancellationToken);
        }
        catch (PaqueteEmpresaDemasiadoGrandeException)
        {
            return Result.Fallo<PaqueteEmpresaDescargaDto>(AutorizacionPaqueteEmpresa.DemasiadoGrande);
        }
        catch (PaqueteDocumentalDemasiadoPesadoException)
        {
            return Result.Fallo<PaqueteEmpresaDescargaDto>(AutorizacionPaqueteEmpresa.DemasiadoPesado);
        }
        catch (PaqueteEmpresaSinArchivosException)
        {
            return Result.Fallo<PaqueteEmpresaDescargaDto>(AutorizacionPaqueteEmpresa.ArchivosNoDisponibles);
        }

        if (zip is null)
            return Result.Fallo<PaqueteEmpresaDescargaDto>(AutorizacionPaqueteEmpresa.SinDocumentos);

        foreach (var documentoId in zip.DocumentoIds)
            await registroAcceso.RegistrarSiSensibleAsync(documentoId, TipoAccesoDocumentoSensible.Apertura, cancellationToken);

        return Result.Exito(new PaqueteEmpresaDescargaDto(zip.NombreArchivo, zip.Contenido));
    }
}
