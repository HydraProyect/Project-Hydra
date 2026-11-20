namespace CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Dokify;

/// <summary>Sobre qué clase de elemento de dokify se pregunta: cada una cuelga de su propia ruta (<c>company</c>, <c>employee</c>, <c>machine</c>).</summary>
public enum ElementoDokify
{
    Empresa,
    Trabajador,
    Maquina,
}

/// <summary>
/// El <c>status_code</c> de un documento en dokify. Solo se traducen los códigos que consta que existen; cualquier otro,
/// o ausente o ilegible, es <see cref="Desconocido"/> y nunca <see cref="Validado"/>: un estado que no se sabe leer no
/// acredita nada.
/// </summary>
public enum EstadoDocumentoDokify
{
    Desconocido,
    SinAnexar,
    Validado,
    Caducado,
    Anulado,
}

/// <summary>Una página de un listado de dokify (<c>page</c> y <c>size</c>, la primera es la 0).</summary>
public sealed record PaginaDokify<T>(IReadOnlyList<T> Datos, int Numero, int TotalPaginas, int TotalElementos)
{
    public bool HayMasPaginas => Numero + 1 < TotalPaginas;
}

/// <summary>El CIF y el nombre de una Empresa en dokify. El CIF de un autónomo es su NIF: no se escribe en un log.</summary>
public sealed record EmpresaDokify(string Vat, string Nombre)
{
    public override string ToString() => nameof(EmpresaDokify);
}

/// <summary>Un Trabajador en dokify. Datos personales: <see cref="ToString"/> no los escribe (OWASP ASVS 4.0.3 V7.1.2).</summary>
public sealed record TrabajadorDokify(string Vat, string Nombre, string Apellidos)
{
    public override string ToString() => nameof(TrabajadorDokify);
}

/// <summary>
/// Una máquina en dokify. La matrícula y el número de serie identifican un bien que puede ser de un autónomo:
/// <see cref="ToString"/> no los escribe, igual que los demás modelos con identificadores (OWASP ASVS 4.0.3 V7.1.2).
/// </summary>
public sealed record MaquinaDokify(string Matricula, string Nombre, string Modelo, string Fabricante, string NumeroSerie)
{
    public override string ToString() => nameof(MaquinaDokify);
}

/// <summary>
/// Una solicitud de documento de un elemento. <see cref="CodigoEstado"/> es el <c>status_code</c> tal como llegó, para
/// poder revisar los que aún no se traducen; <see cref="Estado"/>, su traducción.
/// </summary>
public sealed record DocumentoDokify(
    string TipoDocumentoId, string TipoDocumentoNombre, string SolicitudId, int? CodigoEstado, EstadoDocumentoDokify Estado);

/// <summary>Una asignación de un elemento a un grupo de una Empresa cliente en dokify, con el recuento de sus solicitudes.</summary>
public sealed record AsignacionDokify(
    string Uid, string Grupo, int? Adjuntas, int? Validadas, int? Caducadas, int? Rechazadas, int? Pendientes);

/// <summary>La respuesta de <c>/status</c>. La wiki no dice qué significa <c>true</c> para cada clase de elemento: se guarda tal cual.</summary>
public sealed record EstadoGlobalDokify(bool Status);

/// <summary>El fichero de un documento, ya decodificado. <see cref="ToString"/> no escribe nada del contenido ni del nombre.</summary>
public sealed record DocumentoDescargadoDokify(string NombreFichero, string TipoContenido, byte[] Contenido)
{
    public override string ToString() => nameof(DocumentoDescargadoDokify);
}
