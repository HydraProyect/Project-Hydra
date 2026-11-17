using CaeManager.Application.Common;
using CaeManager.Application.Inbound.AutorizacionesLectura.Commands.SubirDocumentoRequerimientoTec;
using CaeManager.Application.Inbound.AutorizacionesLectura.Queries.ObtenerRequerimientosTec;
using CaeManager.Domain.Common;
using CaeManager.Web.Documentos;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Features.Inbound.TenantTec.Servicios;

/// <summary>
/// Subida del Tenant TEC a un requerimiento con datos reales (demo Inbound, P2). La pantalla manda el Id del requerimiento;
/// aquí se busca su trío en <see cref="ObtenerRequerimientosTecQuery"/> y se manda a
/// <see cref="SubirDocumentoRequerimientoTecCommand"/>, que lo revalida entero en el servidor: el Id no es autoridad.
///
/// <para>
/// El archivo sigue el camino del alta individual (<c>DrawerGestionDocumento</c>): lectura con reserva del presupuesto de
/// memoria, firma del contenido real, conversión a un único PDF y guardado cifrado. Si el comando falla, la clave recién
/// guardada se borra, porque ningún Documento la adopta. Sin emisión se toma hoy, como el alta individual. Todavía no hay
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
            return new RespuestaSubidaTec(ResultadoSubidaTec.Error);

        if (await GuardarPdfAsync(solicitud, ct) is not { } archivoUrl)
            return new RespuestaSubidaTec(ResultadoSubidaTec.Error);

        Result<Guid> resultado;
        try
        {
            resultado = await mediator.Send(new SubirDocumentoRequerimientoTecCommand(
                requerimiento.ContratoAccesoId, requerimiento.RequisitoId, requerimiento.Sujeto, requerimiento.SujetoId,
                solicitud.Emision ?? DiaDeNegocio.Hoy(), solicitud.Vencimiento, archivoUrl, solicitud.Comentarios), ct);
        }
        catch
        {
            await almacenamiento.EliminarAsync(archivoUrl, CancellationToken.None);
            throw;
        }

        if (resultado.EsFallido)
        {
            await almacenamiento.EliminarAsync(archivoUrl, CancellationToken.None);
            registro.LogWarning("La subida al requerimiento {RequerimientoId} no se ha guardado: {Codigo}.",
                solicitud.RequerimientoId, resultado.Error.Codigo);
            return new RespuestaSubidaTec(ResultadoSubidaTec.Error);
        }

        // La lista vuelve a leerse para ofrecer el siguiente pendiente, ya sin el que se acaba de subir.
        var siguiente = (await mediator.Send(new ObtenerRequerimientosTecQuery(), ct))
            .FirstOrDefault(f => f.Id != solicitud.RequerimientoId && f.Estado == EstadoRequerimientoTecSinVeredicto.PendienteDeSubir);
        return new RespuestaSubidaTec(
            ResultadoSubidaTec.Subido, siguiente?.Id, siguiente is null ? null : $"{siguiente.TipoDocumentoNombre} · {siguiente.SujetoNombre}");
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
