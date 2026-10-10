using CaeManager.Domain.Documentos;

namespace CaeManager.Web.Features.Documentos.Components;

/// <summary>
/// Un documento con problema de la lista de una ficha 360, descrito con lo justo para que
/// <see cref="DrawerGestionDocumento"/> lo abra: el Documento que hay que renovar, o —si falta—
/// de quién es y de qué Tipo de documento. La ficha entrega la serie en el orden en que su lista
/// los muestra y el formulario ofrece «Guardar y siguiente» mientras quede alguno.
/// <para>
/// No lleva estado ni permisos: lo que se guarde pasa por el comando, con su autorización y su
/// alcance, igual que al abrir el formulario de uno en uno.
/// </para>
/// </summary>
public sealed record PasoSerieDocumento
{
    private PasoSerieDocumento() { }

    public Guid? DocumentoId { get; private init; }
    public AmbitoAplicacion Ambito { get; private init; }
    public Guid PropietarioId { get; private init; }
    public Guid? TipoDocumentoId { get; private init; }

    /// <summary>Un Documento que ya existe: se abre su renovación.</summary>
    public static PasoSerieDocumento Renovar(Guid documentoId) => new() { DocumentoId = documentoId };

    /// <summary>Un documento que falta: alta con el propietario y el Tipo de documento ya elegidos.</summary>
    public static PasoSerieDocumento Subir(AmbitoAplicacion ambito, Guid propietarioId, Guid tipoDocumentoId) =>
        new() { Ambito = ambito, PropietarioId = propietarioId, TipoDocumentoId = tipoDocumentoId };

    /// <summary>La fila de una lista de ficha: con Documento se renueva; sin él, se sube el que falta.</summary>
    public static PasoSerieDocumento DeFila(Guid? documentoId, AmbitoAplicacion ambito, Guid propietarioId, Guid tipoDocumentoId) =>
        documentoId is { } id ? Renovar(id) : Subir(ambito, propietarioId, tipoDocumentoId);

    /// <summary>
    /// ¿La fila entra en la serie? Entra lo que se arregla con este formulario: lo que la ficha
    /// ofrece «Renovar» o «Subir». «Confirmar» la vigencia no pasa por el formulario.
    /// </summary>
    public static bool EsDeSerie(EstadoDocumento estado) =>
        EstadoDocumentoFicha360.Accion(estado) is AccionDocumentoFicha360.Renovar or AccionDocumentoFicha360.Subir;
}
