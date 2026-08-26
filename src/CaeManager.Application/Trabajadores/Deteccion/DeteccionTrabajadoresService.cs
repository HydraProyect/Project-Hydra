using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Trabajadores.Deteccion;

/// <summary>
/// Compara el listado de trabajadores extraído por IA de un Documento de
/// Empresa (p. ej. ITA, RNT/TC2) contra los Trabajadores activos de esa
/// misma Empresa (nunca de Subcontrata — tiene su propia documentación
/// separada, y no aparece en documentos de Seguridad Social de la Empresa
/// contratista), y registra una DeteccionTrabajador pendiente por cada
/// discrepancia: alguien en el documento sin alta (Nuevo), o alguien de
/// alta que ya no aparece (Ausente). Solo corre si el TipoDocumento tiene
/// la lectura IA activa a Nivel 1 (Administrador) y, cuando la Empresa
/// presta servicio a algún Cliente, si al menos uno de esos Clientes no la
/// tiene desactivada a Nivel 2 — ver ConfiguracionIaDocumentoCliente
/// (Fase 35). Si ya hay una detección pendiente sin resolver para el mismo
/// documento, no duplica.
/// </summary>
public class DeteccionTrabajadoresService(
    IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext, ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext,
    IFileStorageService almacenamiento,
    IExtraccionTrabajadoresIaService extraccion,
    IDeteccionTrabajadorRepository deteccionRepositorio,
    INotificacionUsuarioRepository notificacionRepositorio,
    IUnitOfWork unitOfWork,
    ILogger<DeteccionTrabajadoresService> logger) : IDeteccionTrabajadoresService
{
    public async Task ProcesarDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default)
    {
        var documento = await documentosContext.Documentos.FirstOrDefaultAsync(d => d.Id == documentoId, cancellationToken);

        if (documento is null || documento.EmpresaId is null || string.IsNullOrWhiteSpace(documento.ArchivoUrl))
            return;

        var tipoDocumento = await tiposDocumentoContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == documento.TipoDocumentoId, cancellationToken);

        if (tipoDocumento is null || !tipoDocumento.LecturaIaActiva || !tipoDocumento.DeteccionTrabajadoresActiva)
            return;

        var empresaId = documento.EmpresaId.Value;

        var clientesVinculados = await empresasContext.EmpresasClientes
            .Where(ec => ec.EmpresaId == empresaId)
            .Select(ec => ec.ClienteId)
            .ToListAsync(cancellationToken);

        if (clientesVinculados.Count > 0 && await TodosLosClientesLoTienenDesactivadoAsync(clientesVinculados, tipoDocumento.Id, cancellationToken))
            return;

        var yaHayDeteccionPendiente = await trabajadoresContext.DeteccionesTrabajador
            .AnyAsync(d => d.DocumentoId == documentoId && !d.Resuelta, cancellationToken);

        if (yaHayDeteccionPendiente)
            return;

        byte[] contenido;
        try
        {
            await using var archivo = await almacenamiento.AbrirAsync(documento.ArchivoUrl, cancellationToken);
            using var buffer = new MemoryStream();
            await archivo.CopyToAsync(buffer, cancellationToken);
            contenido = buffer.ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo abrir el archivo del Documento {DocumentoId} para lectura IA.", documentoId);
            return;
        }

        var resultadoExtraccion = await extraccion.ExtraerAsync(contenido, cancellationToken);

        if (resultadoExtraccion.EsFallido)
        {
            logger.LogInformation(
                "Lectura IA del Documento {DocumentoId} no disponible: {Codigo}", documentoId, resultadoExtraccion.Error.Codigo);
            return;
        }

        var extraidos = resultadoExtraccion.Valor;
        var dnisExtraidos = extraidos.Select(NormalizarDni).ToHashSet();

        var trabajadoresActivos = await trabajadoresContext.Trabajadores
            .Where(t => t.EmpresaId == empresaId && !t.EstaEliminado)
            .ToListAsync(cancellationToken);

        var nuevos = extraidos.Where(e => trabajadoresActivos.All(t => NormalizarDni(t.Dni) != NormalizarDni(e.Dni))).ToList();
        var ausentes = trabajadoresActivos.Where(t => !dnisExtraidos.Contains(NormalizarDni(t.Dni))).ToList();

        if (nuevos.Count == 0 && ausentes.Count == 0)
            return;

        foreach (var nuevo in nuevos)
            deteccionRepositorio.Agregar(DeteccionTrabajador.Nuevo(documentoId, empresaId, nuevo.Nombre, nuevo.Apellidos, nuevo.Dni));

        foreach (var ausente in ausentes)
            deteccionRepositorio.Agregar(DeteccionTrabajador.Ausente(documentoId, empresaId, ausente.Id, ausente.Nombre, ausente.Apellidos, ausente.Dni));

        await NotificarGestoresAsync(empresaId, tipoDocumento, clientesVinculados, nuevos.Count, ausentes.Count, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> TodosLosClientesLoTienenDesactivadoAsync(
        IReadOnlyList<Guid> clientesVinculados, Guid tipoDocumentoId, CancellationToken cancellationToken)
    {
        var overrides = await tiposDocumentoContext.ConfiguracionesIaDocumentoCliente
            .Where(c => c.TipoDocumentoId == tipoDocumentoId && clientesVinculados.Contains(c.ClienteId))
            .ToListAsync(cancellationToken);

        return clientesVinculados.All(clienteId => overrides.Any(o => o.ClienteId == clienteId && !o.Activa));
    }

    private async Task NotificarGestoresAsync(
        Guid empresaId, TipoDocumento tipoDocumento, IReadOnlyList<Guid> clientesVinculados,
        int nuevos, int ausentes, CancellationToken cancellationToken)
    {
        var empresa = await empresasContext.Empresas.FirstOrDefaultAsync(e => e.Id == empresaId, cancellationToken);

        var gestoresANotificar = await empresasContext.Empresas
            .Where(c => clientesVinculados.Contains(c.Id) && c.EjecutivoUsuarioId != null)
            .Select(c => c.EjecutivoUsuarioId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var partes = new List<string>();
        if (nuevos > 0) partes.Add($"{nuevos} nuevo(s)");
        if (ausentes > 0) partes.Add($"{ausentes} ausente(s)");

        var mensaje = $"Se detectaron posibles cambios de personal en \"{empresa?.RazonSocial}\" a partir de un documento {tipoDocumento.Nombre}: {string.Join(", ", partes)}.";

        foreach (var gestorId in gestoresANotificar)
            notificacionRepositorio.Agregar(new NotificacionUsuario(
                gestorId, "Cambios de personal detectados", mensaje,
                urlAccion: $"/empresas/{empresaId}/deteccion-trabajadores", textoAccion: "Revisar"));
    }

    private static string NormalizarDni(string dni) => dni.Trim().ToUpperInvariant();
    private static string NormalizarDni(TrabajadorExtraidoDto trabajador) => NormalizarDni(trabajador.Dni);
}
