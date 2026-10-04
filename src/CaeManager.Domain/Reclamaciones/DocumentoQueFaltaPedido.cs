namespace CaeManager.Domain.Reclamaciones;

/// <summary>
/// Un documento que nunca se subió y se pide en una <see cref="ReclamacionDocumental"/>: no hay Documento al que apuntar, así
/// que lo identifican el Tipo de documento y, si es de Trabajador, a quién le falta
/// (<paramref name="TrabajadorId"/> nulo = documento de Empresa: el sujeto es la Empresa de la reclamación).
/// </summary>
public readonly record struct DocumentoQueFaltaPedido(Guid TipoDocumentoId, Guid? TrabajadorId);
