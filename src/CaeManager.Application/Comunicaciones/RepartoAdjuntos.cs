using CaeManager.Application.Common;
using CaeManager.Application.Common.Archivos;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones;

/// <summary>Lo que hace falta saber de un adjunto antes de repartirlo: su hilo, para el alcance, su dirección y su archivo.</summary>
internal sealed record AdjuntoParaReparto(
    Guid ConversacionId, Guid? ClienteId, Guid? EmpresaId, Guid? ConexionIntegracionId, DireccionMensaje Direccion,
    EstadoRepartoAdjunto Estado, string NombreArchivo, string ArchivoUrl)
{
    public Task<bool> VisibleAsync(IAlcanceDatosService alcanceDatos, CancellationToken cancellationToken) =>
        alcanceDatos.ConversacionVisibleAsync(ClienteId, EmpresaId, ConexionIntegracionId, cancellationToken);
}

internal static class RepartoAdjuntos
{
    public static Task<AdjuntoParaReparto?> ObtenerAsync(
        IComunicacionesQueryContext contexto, Guid adjuntoId, CancellationToken cancellationToken) =>
        (from a in contexto.AdjuntosMensaje
         join m in contexto.Mensajes on a.MensajeId equals m.Id
         join c in contexto.Conversaciones on m.ConversacionId equals c.Id
         where a.Id == adjuntoId
         select new AdjuntoParaReparto(
             c.Id, c.ClienteId, c.EmpresaId, c.ConexionIntegracionId, m.Direccion, a.EstadoReparto, a.NombreArchivo, a.ArchivoUrl))
        .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Si el hilo existe y entra en el alcance de la sesión. Con acceso total el alcance dice sí: la existencia se mira aquí.</summary>
    public static async Task<bool> ConversacionVisibleAsync(
        IComunicacionesQueryContext contexto, IAlcanceDatosService alcanceDatos, Guid conversacionId, CancellationToken cancellationToken)
    {
        var conversacion = await contexto.Conversaciones
            .Where(c => c.Id == conversacionId)
            .Select(c => new { c.ClienteId, c.EmpresaId, c.ConexionIntegracionId })
            .FirstOrDefaultAsync(cancellationToken);

        return conversacion is not null && await alcanceDatos.ConversacionVisibleAsync(
            conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken);
    }
}

/// <summary>
/// Lo que comparten leer un adjunto para dividirlo y dividirlo. Dividir abre el contenido, así que manda
/// <see cref="AccesoATriage"/>, el criterio de la descarga, y no solo la visibilidad del hilo: en un hilo sin ancla la
/// cartera no acota y deciden los roles de gestión CAE.
///
/// El hilo se ancla a la contraparte, no a la Empresa gestionada, así que esta no se deduce de él: se recibe y aquí se
/// exige en la cartera. Que sea una Empresa propia del Tenant lo comprueba <see cref="ObtenerCatalogoDivisionPdfQuery"/>.
/// </summary>
internal static class DivisionDeAdjuntos
{
    public const string CodigoArchivoNoValido = "Adjunto.ArchivoNoValido";

    /// <summary>
    /// Por qué no se puede dividir, o <c>null</c> si se puede. Lo ajeno y lo inexistente responden igual, y antes de
    /// nada más: el Guid de un adjunto fuera de alcance no debe decir de qué tipo es ni en qué estado está.
    /// </summary>
    public static async Task<Error?> MotivoParaNoDividirAsync(
        AdjuntoParaReparto? origen, Guid empresaGestionadaId, IAlcanceDatosService alcanceDatos,
        ICurrentUserService currentUserService, CancellationToken cancellationToken)
    {
        if (origen is null || !await AccesoATriage.AdjuntoAlcanzableAsync(
                alcanceDatos, currentUserService, origen.ClienteId, origen.EmpresaId, origen.ConexionIntegracionId, cancellationToken))
            return Error.Crear("Adjunto.NoEncontrado", "No encontramos este adjunto.");

        if (origen.Direccion != DireccionMensaje.Entrante)
            return Error.Crear("Adjunto.NoEntrante", "Solo se reparten los adjuntos recibidos.");

        if (origen.Estado != EstadoRepartoAdjunto.Pendiente)
            return Error.Crear("Adjunto.YaResuelto", "Este adjunto ya está resuelto.");

        if (!Path.GetExtension(origen.NombreArchivo).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return Error.Crear("Adjunto.NoEsPdf", "Solo se divide un adjunto PDF.");

        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(empresaGestionadaId, cancellationToken))
            return Error.Crear(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable, "La Empresa gestionada no está en tu cartera.");

        return null;
    }

    public static Error ArchivoNoValido() => Error.Crear(CodigoArchivoNoValido, ValidadorFirmaArchivo.MensajeDocumentoNoValido);

    /// <summary>El contenido del adjunto, o <c>null</c> si falta, no se puede leer, supera el tope de Dividir o no es un PDF.</summary>
    public static async Task<byte[]?> LeerPdfAsync(
        IFileStorageService almacen, string archivoUrl, string nombreArchivo, CancellationToken cancellationToken)
    {
        Stream flujoAdjunto;
        try { flujoAdjunto = await almacen.AbrirAsync(archivoUrl, cancellationToken); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException
            or InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }

        await using var flujo = flujoAdjunto;
        using var memoria = new MemoryStream();
        var buffer = new byte[81920];
        int leidos;
        while ((leidos = await flujo.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memoria.Length + leidos > LimitesDivisionPdf.TamanoMaximoOriginalBytes)
                return null;
            await memoria.WriteAsync(buffer.AsMemory(0, leidos), cancellationToken);
        }

        var contenido = memoria.ToArray();
        return ValidadorFirmaArchivo.TieneFormatoDeDocumento(contenido, nombreArchivo) ? contenido : null;
    }
}
