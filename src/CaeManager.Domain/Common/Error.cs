namespace CaeManager.Domain.Common;

/// <summary>
/// Fallo de negocio esperable, transportado por <see cref="Result"/>/
/// <see cref="Result{T}"/> — nunca una excepción (ver CODING_STANDARDS.md).
///
/// <b>Convención de <see cref="Codigo"/></b> (P2 #27 de
/// docs/business/MATURITY_REVIEW.md, "catálogo central de códigos de
/// error"): <c>Entidad.Motivo</c>, PascalCase en las dos partes, sin
/// espacios — <c>"Cliente.NoEncontrado"</c>, <c>"Documento.TipoDocumentoNoEncontrado"</c>.
/// Es la convención que ya seguían, de facto y sin excepción, los ~130
/// puntos donde se llama a <see cref="Crear"/> en este código antes de que
/// existiera este comentario — aquí queda escrita para que no dependa de
/// que cada sesión nueva la deduzca por imitación.
///
/// No hay un enum/catálogo centralizado que enumere cada código posible: con
/// ~130 sitios de uso ya en producción, migrarlos todos a constantes
/// compartidas es un cambio mecánico de alto volumen y bajo valor frente al
/// riesgo (ninguna duplicidad de código con significado distinto detectada
/// al auditar los códigos existentes). Lo que sí se exige, y no existía
/// antes: el mismo código para el mismo motivo, siempre — reutiliza el
/// código de un caso ya existente (p. ej. <c>"Cliente.NoEncontrado"</c>
/// vuelve a aparecer en cada Command/Query que necesite ese mismo fallo) en
/// vez de inventar uno nuevo con matices de redacción.
/// </summary>
public sealed class Error : IEquatable<Error>
{
    public static readonly Error Ninguno = new(string.Empty, string.Empty);

    public string Codigo { get; }
    public string Mensaje { get; }

    private Error(string codigo, string mensaje)
    {
        Codigo = codigo;
        Mensaje = mensaje;
    }

    public static Error Crear(string codigo, string mensaje) => new(codigo, mensaje);

    public bool Equals(Error? other) => other is not null && Codigo == other.Codigo;
    public override bool Equals(object? obj) => Equals(obj as Error);
    public override int GetHashCode() => Codigo.GetHashCode();
}
