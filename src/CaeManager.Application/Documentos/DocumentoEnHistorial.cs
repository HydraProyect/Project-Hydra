using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos;

/// <summary>
/// El rechazo de siempre cuando una operación quiere modificar un documento que ya pasó al historial (lo sustituyó
/// otro, D5 y D8 del diseño del documento efectivo): el historial es inmutable. Un solo mensaje y un solo código para
/// que cada comando que lo comprueba (renovar, corregir la lectura de la IA, firmar en campo) responda igual, y para
/// que la pantalla pueda llevar al documento vigente con <see cref="Documento.SustituidoPorDocumentoId"/>.
/// </summary>
public static class DocumentoEnHistorial
{
    public const string Codigo = "Documento.EnHistorial";

    public static Error NuevoError() => Error.Crear(
        Codigo,
        "Este documento ya está en el historial porque otro lo sustituyó. Actúa sobre el documento vigente.");
}
