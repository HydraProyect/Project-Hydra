using CaeManager.Application.Common;
using CaeManager.Application.Inbound.AutorizacionesLectura.Commands.SubirDocumentoRequerimientoTec;
using CaeManager.Application.Inbound.AutorizacionesLectura.Queries.ObtenerRequerimientosTec;
using CaeManager.Domain.Common;
using CaeManager.Web.Documentos;
using FluentValidation;
using MediatR;
using ValidadorFirmaArchivoAplicacion = CaeManager.Application.Common.Archivos.ValidadorFirmaArchivo;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Features.Inbound.TenantTec.Servicios;

/// <summary>
/// Subida del Tenant TEC a un requerimiento con datos reales (demo Inbound, P2). La pantalla manda el Id del requerimiento;
/// aquí se busca su trío en <see cref="ObtenerRequerimientosTecQuery"/> y se manda a
/// <see cref="SubirDocumentoRequerimientoTecCommand"/>, que lo revalida entero en el servidor: el Id no es autoridad.
///
/// <para>
/// El archivo sigue el camino del alta individual (<c>DrawerGestionDocumento</c>): lectura con reserva del presupuesto de
/// memoria, firma del contenido real, conversión a un único PDF y guardado cifrado. Si el comando responde con un fallo o
/// la validación lo rechaza, la clave recién guardada se borra (de mejor esfuerzo), porque ningún Documento la adopta; si
/// lanza otra excepción, se conserva, porque el alta pudo confirmarse antes. El motivo claro llega a la pantalla. Sin emisión se toma hoy, como el alta individual. Todavía no hay
/// filtro de contenido de datos de salud (B-20): nunca responde <see cref="ResultadoSubidaTec.NoAceptadoPorDatosDeSalud"/>.
/// </para>
/// </summary>
public sealed class SubidaDocumentoTenantTecConBackend(
    IMediator mediator,
    IFileStorageService almacenamiento,
    IConversorWordPdfService conversorWord,
    IPresupuestoMemoriaDocumentos presupuesto,
    ILogger<SubidaDocumentoTenantTecConBackend> registro)
    : ISubidaDocumentoTenantTec
{
    private const int FactorMemoriaLectura = ArchivoSubidoReservado.FactorMemoriaLectura;

    public bool Disponible => true;

    public async Task<RespuestaSubidaTec> SubirAsync(SolicitudSubidaTec solicitud, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(solicitud);

        var requerimiento = (await mediator.Send(new ObtenerRequerimientosTecQuery(), ct))
            .FirstOrDefault(f => f.Id == solicitud.RequerimientoId);
        if (requerimiento is null)
            return new RespuestaSubidaTec(ResultadoSubidaTec.Error, Mensaje: SubirDocumentoRequerimientoTecErrores.RequisitoNoEncontrado.Mensaje);

        if (await GuardarPdfAsync(solicitud, ct) is not { } archivoUrl)
            return new RespuestaSubidaTec(ResultadoSubidaTec.Error, Mensaje: ValidadorFirmaArchivoAplicacion.MensajeDocumentoNoValido);

        Result<Guid> resultado;
        try
        {
            resultado = await mediator.Send(new SubirDocumentoRequerimientoTecCommand(
                requerimiento.ContratoAccesoId, requerimiento.RequisitoId, requerimiento.Sujeto, requerimiento.SujetoId,
                solicitud.Emision ?? DiaDeNegocio.Hoy(), solicitud.Vencimiento, archivoUrl, solicitud.Comentarios), ct);
        }
        catch (ValidationException validacion)
        {
            // La validación corre antes de cualquier handler: ningún Documento ha adoptado la clave y el motivo se puede decir.
            await DescartarHuerfanoAsync(archivoUrl);
            return new RespuestaSubidaTec(ResultadoSubidaTec.Error,
                Mensaje: string.Join(" ", validacion.Errors.Select(e => e.ErrorMessage).Distinct()));
        }
        catch (Exception excepcion)
        {
            // Una excepción puede llegar después de que el alta o la renovación confirmara (por ejemplo, al liberar la
            // conexión): borrar dejaría un Documento sin archivo. Como el alta individual, se prefiere el huérfano.
            registro.LogWarning(excepcion, "La subida al requerimiento {RequerimientoId} ha fallado; se conserva el archivo {ArchivoUrl} por si un Documento lo adoptó.",
                solicitud.RequerimientoId, archivoUrl);
            throw;
        }

        if (resultado.EsFallido)
        {
            await DescartarHuerfanoAsync(archivoUrl);
            registro.LogWarning("La subida al requerimiento {RequerimientoId} no se ha guardado: {Codigo}.",
                solicitud.RequerimientoId, resultado.Error.Codigo);
            // El motivo del servidor llega a la pantalla (D1 pide un error claro); ninguno identifica a un Gestor CAE (P-4).
            return new RespuestaSubidaTec(ResultadoSubidaTec.Error, Mensaje: resultado.Error.Mensaje);
        }

        // El documento ya está guardado: si la segunda lectura falla, la respuesta sigue siendo «Subido», sin siguiente.
        var siguiente = await SiguientePendienteAsync(solicitud.RequerimientoId, ct);
        return new RespuestaSubidaTec(
            ResultadoSubidaTec.Subido, siguiente?.Id, siguiente is null ? null : $"{siguiente.TipoDocumentoNombre} · {siguiente.SujetoNombre}");
    }

    /// <summary>
    /// La lista vuelve a leerse para ofrecer el siguiente pendiente, ya sin el que se acaba de subir. Es solo una ayuda para
    /// encadenar: un fallo aquí (también una cancelación) no deshace ni oculta que el documento se guardó.
    /// </summary>
    private async Task<RequerimientoTecDto?> SiguientePendienteAsync(Guid subidoId, CancellationToken ct)
    {
        try
        {
            return (await mediator.Send(new ObtenerRequerimientosTecQuery(), ct))
                .FirstOrDefault(f => f.Id != subidoId && f.Estado == EstadoRequerimientoTecSinVeredicto.PendienteDeSubir);
        }
        catch (Exception excepcion)
        {
            registro.LogWarning(excepcion, "El documento del requerimiento {RequerimientoId} se ha guardado, pero no se ha podido leer el siguiente pendiente.", subidoId);
            return null;
        }
    }

    /// <summary>Borra de mejor esfuerzo la clave que ningún Documento adopta: si el borrado falla, queda registrada y la respuesta no cambia.</summary>
    private async Task DescartarHuerfanoAsync(string archivoUrl)
    {
        try
        {
            await almacenamiento.EliminarAsync(archivoUrl, CancellationToken.None);
        }
        catch (Exception excepcion)
        {
            registro.LogWarning(excepcion, "No se ha podido borrar el archivo huérfano {ArchivoUrl} de una subida a un requerimiento.", archivoUrl);
        }
    }

    /// <summary>Lee, comprueba, convierte y guarda el archivo; null si no es un documento válido.</summary>
    private async Task<string?> GuardarPdfAsync(SolicitudSubidaTec solicitud, CancellationToken ct)
    {
        if (solicitud.Tamano <= 0 || solicitud.Tamano > LimitesArchivoSubido.TamanoMaximoBytes)
            return null;

        byte[] pdf;
        var reserva = await presupuesto.ReservarAsync(solicitud.Tamano * FactorMemoriaLectura, ct);
        try
        {
            var contenido = new byte[solicitud.Tamano];
            await solicitud.Contenido.ReadExactlyAsync(contenido, ct);
            // Un flujo que trae más de lo que declaró no se trunca en silencio: se rechaza.
            if (await solicitud.Contenido.ReadAsync(new byte[1], ct) > 0)
                return null;

            // La extensión la eligió quien sube: se comprueba la firma del contenido real antes de convertir o guardar.
            if (!ValidadorFirmaArchivo.EsDocumentoValido(contenido, solicitud.NombreArchivo))
                return null;

            List<(byte[] Contenido, string NombreArchivo)> contenidos = [(contenido, solicitud.NombreArchivo)];
            using (ConversorArchivosPdf.ReservarDecodificacion(presupuesto, reserva, contenidos))
            {
                pdf = await ConversorArchivosPdf.UnificarAsync(contenidos, conversorWord, ct);
            }
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        finally
        {
            // Antes de guardar: el almacenamiento reserva por su cuenta y esperaría a esta reserva.
            reserva.Dispose();
        }

        using var flujoPdf = new MemoryStream(pdf);
        return await almacenamiento.GuardarAsync(flujoPdf, "documento.pdf", ct);
    }
}
